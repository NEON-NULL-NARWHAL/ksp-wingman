// KSP Wingman · src/KspWingman.Mcp/TowerClient.cs
// The agent's side of LUNA EVO Mission Control's KSC tower (WINGMAN_TOWER_URL): reports where the plane is every few
// seconds, answers the tower's "say intentions", and flies what it's told: a cleared route, a hold, vacating the
// runway after landing. The tower's messages are kept for status.

using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using KRPC.Client.Services.KRPC;
using KRPC.Client.Services.SpaceCenter;

namespace KspWingman.Mcp;

/// <summary>The tower client when WINGMAN_TOWER_URL is set (null without a tower), for the tools.</summary>
public sealed class TowerLink(TowerClient? client)
{
    public TowerClient? Client => client;
}

public sealed class TowerClient : IDisposable
{
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(3);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly Uri _tower;
    private readonly KrpcLink _link;
    private readonly Director _director;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<string> _heard = [];
    private string? _callSign;
    private bool _wantsToLand;

    public TowerClient(Uri tower, string host, int port, Director director)
    {
        _tower = tower;
        _link = new KrpcLink(host, port);
        _director = director;
        _ = Task.Run(Loop);
    }

    /// <summary>The tower's last messages for this plane, oldest first (status shows the last).</summary>
    public IReadOnlyList<string> Heard { get { lock (_heard) return _heard.ToList(); } }

    /// <summary>Asks for an approach. The answer (a clearance or a hold) comes back and is acted on.</summary>
    public async Task<string> RequestApproachAsync()
    {
        _wantsToLand = true;
        var answers = await SayAsync("request-approach", "request approach, KSC runway 09");
        return answers.Count == 0 ? "Asked KSC tower for an approach; its answer will show in status." : string.Join(" ", answers);
    }

    private async Task Loop()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await ReportAsync();
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or WingmanException or System.Text.Json.JsonException or IOException)
            {
                // No tower (yet), or KSP between scenes: try again next time.
            }
            await Task.Delay(Every, _stop.Token).ContinueWith(_ => { });
        }
    }

    private async Task ReportAsync()
    {
        _callSign ??= _link.Wingman<string>("PlayerName");
        var report = _link.Run(c =>
        {
            if (c.KRPC().GameScene != GameScene.Flight)
                return new JsonObject { ["callSign"] = _callSign, ["latitude"] = 0, ["longitude"] = 0, ["situation"] = "SpaceCenter" };
            var v = c.SpaceCenter().ActiveVessel;
            if (FlightTools.Wrecked(v))
                return new JsonObject { ["callSign"] = _callSign, ["latitude"] = 0, ["longitude"] = 0, ["situation"] = "Destroyed" };
            var g = v.Flight(v.Orbit.Body.ReferenceFrame);
            var f = v.Flight(v.SurfaceReferenceFrame);
            return new JsonObject
            {
                ["callSign"] = _callSign,
                ["latitude"] = g.Latitude,
                ["longitude"] = g.Longitude,
                ["altitude"] = g.MeanAltitude,
                ["heading"] = f.Heading,
                ["speed"] = g.Speed,
                ["situation"] = v.Situation.ToString(),
                ["phase"] = _director.Doing,
            };
        });
        await Handle(await PostAsync("v1/report", report));
    }

    private async Task<List<string>> SayAsync(string kind, string text)
    {
        _callSign ??= _link.Wingman<string>("PlayerName");
        return await Handle(await PostAsync("v1/say", new JsonObject { ["callSign"] = _callSign, ["kind"] = kind, ["text"] = text }));
    }

    private async Task<JsonArray> PostAsync(string path, JsonObject body)
    {
        using var response = await _http.PostAsJsonAsync(new Uri(_tower, path), body);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        return json?["messages"] as JsonArray ?? [];
    }

    /// <summary>Acts on the tower's messages and returns their texts.</summary>
    private async Task<List<string>> Handle(JsonArray messages)
    {
        var texts = new List<string>();
        foreach (var m in messages.OfType<JsonObject>())
        {
            var kind = m["kind"] is JsonValue k && k.TryGetValue<string>(out var ks) ? ks : "";
            var text = m["text"] is JsonValue t && t.TryGetValue<string>(out var ts) ? ts : "";
            texts.Add(text);
            lock (_heard)
            {
                _heard.Add($"{DateTime.Now:HH:mm:ss} {text}");
                if (_heard.Count > 20)
                    _heard.RemoveAt(0);
            }
            switch (kind)
            {
                case "ask-intent":
                    await SayAsync("intent", Intent());
                    break;
                case "clearance" when Route(m["route"]) is { } approach:
                    _director.Land(Runway.Ksc, approach);
                    break;
                case "hold" when m["hold"] is JsonObject h && Number(h, "latitude") is { } lat && Number(h, "longitude") is { } lon:
                    _director.HoldAt(new Geo(lat, lon, Number(h, "altitude") ?? 1200), Number(h, "radius") ?? 2000,
                        h["fix"] is JsonValue fx && fx.TryGetValue<string>(out var fix) ? fix : "the hold");
                    break;
                case "vacate":
                    // A plane can't taxi: it's recovered once stopped (back to the Space Center), which clears the runway.
                    _link.Run(c =>
                    {
                        var v = c.SpaceCenter().ActiveVessel;
                        if (v.Situation == VesselSituation.Landed && v.Flight(v.Orbit.Body.ReferenceFrame).Speed < 2 && v.Recoverable)
                            v.Recover();
                        return 0;
                    });
                    _wantsToLand = false;
                    break;
            }
        }
        return texts;
    }

    private string Intent()
    {
        var doing = _director.Doing;
        if (_wantsToLand || doing.StartsWith("Landing", StringComparison.Ordinal) || doing.StartsWith("Holding", StringComparison.Ordinal))
            return "land at KSC";
        return _director.Active ? "local flight: " + doing : "on the ground";
    }

    /// <summary>The tower's route as an approach Wingman flies (fixes placed by KSC's runway).</summary>
    internal static Approach? Route(JsonNode? node)
    {
        if (node is not JsonObject route || route["fixes"] is not JsonArray fixes || fixes.Count == 0)
            return null;
        var landing = new Landing(Runway.Ksc);
        var list = new List<Fix>();
        foreach (var f in fixes.OfType<JsonObject>())
        {
            if (Number(f, "latitude") is not { } lat || Number(f, "longitude") is not { } lon)
                return null;
            var (along, cross) = landing.RunwayFrame(600_000, new Geo(lat, lon, 0));
            var name = f["name"] is JsonValue n && n.TryGetValue<string>(out var ns) ? ns : "fix";
            list.Add(new Fix(name, along, cross, Number(f, "altitude") ?? 900, Number(f, "speed") ?? 110));
        }
        var title = route["name"] is JsonValue rn && rn.TryGetValue<string>(out var rs) ? rs : "the tower's route";
        return new Approach(title, Runway.Ksc, "cleared by KSC tower", list);
    }

    /// <summary>A number field (a whole number reads as one too), or null.</summary>
    private static double? Number(JsonObject o, string name)
    {
        // Any number, however it was made (parsed, or built from an int): its JSON text.
        if (o[name] is not JsonValue v || v.GetValueKind() != System.Text.Json.JsonValueKind.Number)
            return null;
        return double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _http.Dispose();
        _link.Dispose();
    }
}

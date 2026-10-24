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

    private readonly FlightEvents? _events;

    public TowerClient(Uri tower, string host, int port, Director director, FlightEvents? events = null)
    {
        _events = events;
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
            _events?.Add("tower", text, new { kind });
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
                case "hold" when Hold(m["hold"]) is { } hold:
                    _director.HoldAt(hold.Center, hold.Radius, hold.Fix);
                    break;
                case "clearance" or "hold":
                    lock (_heard)
                        _heard.Add($"{DateTime.Now:HH:mm:ss} (refused: the tower's {kind} wasn't a safe one to fly)");
                    break;
                case "vacate":
                    // Stopped on the runway: taxi off it and park beside it (the tower sees it clear by its position).
                    if (_link.Run(c => c.SpaceCenter().ActiveVessel is var v && v.Situation == VesselSituation.Landed &&
                                       v.Flight(v.Orbit.Body.ReferenceFrame).Speed < 2))
                        _director.Vacate(Runway.Ksc);
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

    /// <summary>
    /// A hold the tower sent, or null when it isn't one to fly (audit 13, T3: anything on this PC can talk on the
    /// tower's channel, so the agent checks what it's told: near KSC, a sane height and circle).
    /// </summary>
    internal static (Geo Center, double Radius, string Fix)? Hold(JsonNode? node)
    {
        if (node is not JsonObject h || Number(h, "latitude") is not { } lat || Number(h, "longitude") is not { } lon)
            return null;
        var altitude = Number(h, "altitude") ?? 1200;
        var radius = Number(h, "radius") ?? 2000;
        if (!NearKsc(lat, lon) || altitude is < MinAltitude or > MaxAltitude || radius is < 1000 or > 5000)
            return null;
        var fix = h["fix"] is JsonValue fx && fx.TryGetValue<string>(out var f) ? f[..Math.Min(f.Length, 20)] : "the hold";
        return (new Geo(lat, lon, altitude), radius, fix);
    }

    /// <summary>Fixes the agent flies for a tower: within this of KSC (m), these heights (m) and speeds (m/s), this many.</summary>
    public const double MaxDistance = 60_000, MinAltitude = 400, MaxAltitude = 5000, MinSpeed = 60, MaxSpeed = 250;
    public const int MaxFixes = 12;

    private static bool NearKsc(double lat, double lon)
    {
        var (north, east) = Formation.Offset(600_000, Runway.Ksc.Threshold, new Geo(lat, lon, 0));
        return Math.Sqrt(north * north + east * east) <= MaxDistance;
    }

    /// <summary>The tower's route as an approach Wingman flies (fixes placed by KSC's runway), or null when it isn't safe.</summary>
    internal static Approach? Route(JsonNode? node)
    {
        if (node is not JsonObject route || route["fixes"] is not JsonArray fixes || fixes.Count is 0 or > MaxFixes)
            return null;
        var landing = new Landing(Runway.Ksc);
        var list = new List<Fix>();
        foreach (var f in fixes.OfType<JsonObject>())
        {
            if (Number(f, "latitude") is not { } lat || Number(f, "longitude") is not { } lon || !NearKsc(lat, lon))
                return null;
            var altitude = Number(f, "altitude") ?? 900;
            var speed = Number(f, "speed") ?? 110;
            if (altitude is < MinAltitude or > MaxAltitude || speed is < MinSpeed or > MaxSpeed)
                return null;
            var (along, cross) = landing.RunwayFrame(600_000, new Geo(lat, lon, 0));
            var name = f["name"] is JsonValue n && n.TryGetValue<string>(out var ns) ? ns : "fix";
            list.Add(new Fix(name[..Math.Min(name.Length, 20)], along, cross, altitude, speed));
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

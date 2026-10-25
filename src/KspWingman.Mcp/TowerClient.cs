// KSP Wingman · src/KspWingman.Mcp/TowerClient.cs
// The agent's side of LUNA EVO Mission Control's KSC tower (WINGMAN_TOWER_URL): reports where the plane is every few
// seconds, answers the tower's "say intentions", and flies what it's told: a cleared route, a hold, vacating the
// runway after landing. The tower's messages are kept for status. Each finished flight goes to the tower's knowledge
// store (Mission Control 2.13+), and the knowledge and learn tools read and add to it.
// Guide:   Meat and Potatoes/05-Tower-Client.md

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
    private bool _calledSight;
    private bool _calledGoAround;
    private Approach? _pending; // a clearance that came in during a go-around's climb-out
    private (Geo Center, double Radius, string Fix)? _pendingHold; // a hold, likewise

    /// <summary>Going around (from the air, or from the runway) and still under 300 m: new orders wait.</summary>
    private bool ClimbingOut => _director.Doing.Contains("oing around", StringComparison.Ordinal);

    private readonly FlightEvents? _events;

    public TowerClient(Uri tower, string host, int port, Director director, FlightEvents? events = null)
    {
        _events = events;
        if (events != null)
            events.Added += e =>
            {
                if (e.Kind == "outcome" && e.Data is FlightOutcome o)
                    _ = RecordFlightAsync(o);
                else if (e.Kind == "place" && e.Data is Place p)
                    _ = RecordPlaceAsync(p);
            };
        _tower = tower;
        _link = new KrpcLink(host, port);
        _director = director;
        _ = Task.Run(Loop);
    }

    /// <summary>The tower's last messages for this plane, oldest first (status shows the last).</summary>
    public IReadOnlyList<string> Heard { get { lock (_heard) return _heard.ToList(); } }

    /// <summary>
    /// Asks to depart (before a launch onto the runway, Mission Control 2.18): null when cleared (or an older tower that
    /// doesn't answer), else the tower's "hold for departure: ..." words.
    /// </summary>
    public async Task<string?> DepartureAsync()
    {
        var answers = await SayAsync("departure", "ready for departure, runway 09");
        return answers.FirstOrDefault(a => a.Contains("hold for departure", StringComparison.Ordinal));
    }

    /// <summary>Asks for an approach. The answer (a clearance or a hold) comes back and is acted on.</summary>
    /// <param name="approach">One to ask for ("RNAV 09 NORTH", "visual"): granted when nobody else is waiting.</param>
    public async Task<string> RequestApproachAsync(string? approach = null)
    {
        _wantsToLand = true;
        var answers = await SayAsync("request-approach", approach is { Length: > 0 } a ? "request " + a : "request approach, KSC runway 09");
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
            // Out of the LMP server (its player left): out of the tower's sequence too.
            if (!_link.Wingman<bool>("InGame"))
                return new JsonObject { ["callSign"] = _callSign, ["latitude"] = 0, ["longitude"] = 0, ["situation"] = "Offline" };
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
                ["approachSpeed"] = _director.Setup.ApproachSpeed, // a fast plane gets the long approaches (MC 2.17)
            };
        });
        // A plane that's gone (crashed, in the sea, recovered, out of the server) or a fresh one on the runway hasn't asked
        // to land: in the rig a new Albatross was put in the hold before it took off, from the last plane's request.
        if (report["situation"] is JsonValue sv && sv.TryGetValue<string>(out var situation) &&
            situation is "SpaceCenter" or "Destroyed" or "Splashed" or "Offline" or "PreLaunch")
            (_wantsToLand, _calledSight, _calledGoAround, _pending, _pendingHold) = (false, false, false, null, null);
        await Handle(await PostAsync("v1/report", report));
        // Going around: tell the tower, once; it resequences (a hold with traffic, or the short visual at once). A new
        // route waits until the climb-out is through 300 m, so it never turns onto it just off the ground.
        if (!_calledGoAround && _director.Doing.Contains("oing around", StringComparison.Ordinal))
        {
            _calledGoAround = true;
            await SayAsync("go-around", "going around");
        }
        if ((_pending != null || _pendingHold != null) && _link.Run(c => c.SpaceCenter().ActiveVessel is var v && v.Flight(v.Orbit.Body.ReferenceFrame).SurfaceAltitude > 300))
        {
            _calledGoAround = false;
            if (_pending is { } waiting)
                _director.Land(Runway.Ksc, waiting);
            else if (_pendingHold is { } h)
                _director.HoldAt(h.Center, h.Radius, h.Fix);
            (_pending, _pendingHold) = (null, null);
        }
        // On final and lined up: "runway in sight", once an approach; the tower answers with the landing clearance.
        if (!_calledSight && _director.Doing.StartsWith("Landing, on final", StringComparison.Ordinal) && LinedUp(_director.Doing))
        {
            _calledSight = true;
            await SayAsync("runway-in-sight", "runway in sight");
        }
    }

    private async Task<List<string>> SayAsync(string kind, string text)
    {
        _callSign ??= _link.Wingman<string>("PlayerName");
        return await Handle(await PostAsync("v1/say", new JsonObject { ["callSign"] = _callSign, ["kind"] = kind, ["text"] = text }));
    }

    private static readonly System.Text.Json.JsonSerializerOptions Web = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>A finished flight to the knowledge store; its grades come back as a "knowledge" event.</summary>
    private async Task RecordFlightAsync(FlightOutcome outcome)
    {
        try
        {
            _callSign ??= _link.Wingman<string>("PlayerName");
            var body = (JsonObject)System.Text.Json.JsonSerializer.SerializeToNode(outcome, Web)!;
            body["callSign"] = _callSign;
            var answer = await PostRawAsync("v1/flight", body);
            if (answer?["flight"] is not JsonObject flight || flight["id"] is not JsonValue idValue || !idValue.TryGetValue<long>(out var id))
                return; // an older tower without knowledge
            var grades = flight["skills"] as JsonObject ?? [];
            var text = $"Flight {id} recorded: " + (grades.Count == 0 ? "no skills used." :
                string.Join(", ", grades.Select(g => $"{g.Key} {(g.Value is JsonValue v && v.TryGetValue<bool>(out var good) && good ? "good" : "not good yet")}")) + ".");
            _events?.Add("knowledge", text, new { flightId = id, skills = grades.ToDictionary(g => g.Key, g => g.Value is JsonValue v && v.TryGetValue<bool>(out var b) && b) });
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or WingmanException or System.Text.Json.JsonException or IOException or System.InvalidOperationException)
        {
            // No tower: the flight stays in the events only.
        }
    }

    /// <summary>The tower's published table (fixes, legs, final fixes), for a plan of the agent's own.</summary>
    public async Task<JsonNode?> ProceduresAsync()
    {
        _callSign ??= _link.Wingman<string>("PlayerName");
        return await PostRawAsync("v1/procedures", new JsonObject { ["callSign"] = _callSign });
    }

    /// <summary>Files a flight plan (fix names): the tower clears it as filed, holds the plane, or says why it can't.</summary>
    public async Task<string> FilePlanAsync(IReadOnlyList<string> fixes)
    {
        _wantsToLand = true;
        _callSign ??= _link.Wingman<string>("PlayerName");
        var answers = await Handle(await PostAsync("v1/plan", new JsonObject { ["callSign"] = _callSign, ["fixes"] = new JsonArray(fixes.Select(f => (JsonNode)f).ToArray()) }));
        return answers.Count == 0 ? "Filed with KSC tower; its answer will show in status." : string.Join(" ", answers);
    }

    /// <summary>The knowledge store's skills (with this agent's and everyone's numbers), lessons and last flights.</summary>
    public async Task<JsonNode?> KnowledgeAsync()
    {
        _callSign ??= _link.Wingman<string>("PlayerName");
        return await PostRawAsync("v1/knowledge", new JsonObject { ["callSign"] = _callSign });
    }

    /// <summary>Adds a lesson citing one of this agent's flights. Returns what the tower said.</summary>
    public async Task<string> LearnAsync(string skill, string text, long flightId, bool aboutPlane = false)
    {
        _callSign ??= _link.Wingman<string>("PlayerName");
        var answer = await PostRawAsync("v1/lesson", new JsonObject
        {
            ["callSign"] = _callSign, ["skill"] = skill, ["text"] = text, ["evidence"] = new JsonArray(flightId), ["aboutCraft"] = aboutPlane,
        });
        if (answer?["lesson"] is JsonObject lesson)
        {
            var confirmations = lesson["confirmations"] is JsonValue c && c.TryGetValue<int>(out var n) ? n : 0;
            _events?.Add("lesson", $"Learned ({skill}): {text}", new { skill, text, flightId });
            return confirmations > 0 ? $"That confirms a lesson the server already had ({confirmations} confirmation(s))." : "Lesson saved on the server.";
        }
        return "The tower refused the lesson: " + (answer?["error"] is JsonValue e && e.TryGetValue<string>(out var why) ? why : "no answer");
    }

    /// <summary>Sends a flight test's numbers to the tower (Mission Control 2.17+), for every agent's setup of that plane.</summary>
    public async Task RecordTestAsync(PlaneTest test)
    {
        _callSign ??= _link.Wingman<string>("PlayerName");
        var body = (JsonObject)System.Text.Json.JsonSerializer.SerializeToNode(test, Web)!;
        body["callSign"] = _callSign;
        // Its knowledge card's final approach limits, worked out from the test (Mission Control 2.18 shows them).
        var setup = PlaneSetup.From(test);
        body["finalMin"] = setup.MinFinalSpeed;
        body["finalMax"] = setup.MaxFinalSpeed;
        body["touchdown"] = setup.LandingSpeed;
        body["maxSlope"] = setup.MaxGlideSlope;
        await PostRawAsync("v1/test", body);
    }

    /// <summary>Sends an explored place to the tower (Mission Control 2.18+), for every agent's map.</summary>
    private async Task RecordPlaceAsync(Place place)
    {
        try
        {
            _callSign ??= _link.Wingman<string>("PlayerName");
            await PostRawAsync("v1/place", new JsonObject
            {
                ["callSign"] = _callSign, ["latitude"] = place.Latitude, ["longitude"] = place.Longitude, ["ground"] = place.Ground, ["biome"] = place.Biome,
            });
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or WingmanException or System.Text.Json.JsonException or IOException or System.InvalidOperationException)
        {
            // No tower: the place stays in the events only.
        }
    }

    /// <summary>What the tower knows about a plane: its latest flight test (null: none, or an older tower) and its last finals.</summary>
    public async Task<(PlaneTest? Test, IReadOnlyList<FinalReport> Finals)> CraftAsync(string craft)
    {
        _callSign ??= _link.Wingman<string>("PlayerName");
        var answer = await PostRawAsync("v1/craft", new JsonObject { ["callSign"] = _callSign, ["craft"] = craft });
        return (ParseTest(answer?["test"], craft), ParseFinals(answer?["finals"]));
    }

    /// <summary>A flight test in the tower's answer, checked number by number (anything odd: no test).</summary>
    public static PlaneTest? ParseTest(JsonNode? node, string craft)
    {
        if (node is not JsonObject o)
            return null;
        double? N(string name, double max) => o[name] is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) && d >= 0 && d <= max ? d : null;
        if (N("pitchRate", 2000) is not { } pitch || N("rollRate", 5000) is not { } roll || N("minSpeed", 1000) is not { } min)
            return null;
        var drift = o["drift"] is JsonValue dv && dv.TryGetValue<double>(out var dd) && double.IsFinite(dd) ? Math.Clamp(dd, -100, 100) : 0;
        return new PlaneTest(craft, pitch, roll, N("turnRate", 100) ?? 0, min, N("deceleration", 100) ?? 1, N("acceleration", 100) ?? 1, N("cruiseThrottle", 1) ?? 0.2,
            N("steerRate", 1000) ?? 0, drift, N("braking", 100) ?? 0);
    }

    public static IReadOnlyList<FinalReport> ParseFinals(JsonNode? node) => (node as JsonArray ?? []).OfType<JsonObject>().Take(20)
        .Select(f => new FinalReport(
            f["weaves"] is JsonValue w && w.TryGetValue<int>(out var wi) ? Math.Clamp(wi, 0, 100) : 0,
            f["high"] is JsonValue h && h.TryGetValue<double>(out var hd) && double.IsFinite(hd) ? Math.Clamp(hd, -10_000, 10_000) : 0,
            f["slope"] is JsonValue s && s.TryGetValue<double>(out var sd) && sd is >= 1 and <= 15 ? sd : null)).ToList();

    private async Task<JsonNode?> PostRawAsync(string path, JsonObject body)
    {
        using var response = await _http.PostAsJsonAsync(new Uri(_tower, path), body);
        var text = await response.Content.ReadAsStringAsync();
        return text.Length is 0 or > 1_000_000 ? null : JsonNode.Parse(text);
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
                    _calledSight = false;
                    // Held while it climbs out, and the go-around stays reported until it's flown: resetting it here
                    // made the rig's Aeris 4A report the same go-around every 2-4 s until 300 m (and the tower answer each).
                    if (ClimbingOut)
                        (_pending, _pendingHold) = (approach, null); // flown once it's through 300 m
                    else
                    {
                        _calledGoAround = false;
                        _director.Land(Runway.Ksc, approach);
                    }
                    break;
                case "hold" when Hold(m["hold"]) is { } hold:
                    // Held during the climb-out too: the rig's Aeris 4A, off the runway at 69 m, turned for the hold and crashed.
                    if (ClimbingOut)
                        (_pendingHold, _pending) = ((hold.Center, hold.Radius, hold.Fix), null);
                    else
                    {
                        _calledGoAround = false;
                        _director.HoldAt(hold.Center, hold.Radius, hold.Fix);
                    }
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
        // "Holding at" a tower's hold (not the pilot's "Holding heading ...", which is just cruising).
        if (_wantsToLand || doing.StartsWith("Landing", StringComparison.Ordinal) || doing.StartsWith("Holding at", StringComparison.Ordinal))
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
    // 300 m: the short visual's final fix is at 384 m (4.5 km out on the 4° slope). Audit 15.
    public const double MaxDistance = 60_000, MinAltitude = 300, MaxAltitude = 5000, MinSpeed = 60, MaxSpeed = 250;
    public const int MaxFixes = 12;

    private static bool NearKsc(double lat, double lon)
    {
        var (north, east) = Formation.Offset(600_000, Runway.Ksc.Threshold, new Geo(lat, lon, 0));
        return Math.Sqrt(north * north + east * east) <= MaxDistance;
    }

    /// <summary>The pilot's "Landing, on final: 6.4 km before the touchdown point, 193 m left of ..." within 10 km and 300 m.</summary>
    internal static bool LinedUp(string doing)
    {
        var m = System.Text.RegularExpressions.Regex.Match(doing, @"on final: ([\d.]+) km before the touchdown point, (\d+) m");
        return m.Success && double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) <= 10 &&
               double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) <= 300;
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
        // The agent is stopping: the tower takes it out of the sequence now, not after its 60 s timeout.
        if (_callSign != null)
        {
            try
            {
                PostAsync("v1/say", new JsonObject { ["callSign"] = _callSign, ["kind"] = "leaving", ["text"] = "leaving the channel" }).Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception e) when (e is AggregateException or HttpRequestException or TaskCanceledException or ObjectDisposedException)
            {
                // No tower: nothing to tell.
            }
        }
        _http.Dispose();
        _link.Dispose();
    }
}

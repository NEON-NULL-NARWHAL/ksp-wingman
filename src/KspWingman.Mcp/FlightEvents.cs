// KSP Wingman · src/KspWingman.Mcp/FlightEvents.cs
// The agent's events as data: flight phases and outcomes (FlightWatch), the tower's messages (TowerClient). Kept in
// memory for the events tool (the console's --ipc mode and Mission Control read them) and, with WINGMAN_EVENTS_LOG, as
// JSON lines in a file.

using System.Globalization;
using System.Text.Json;
using KRPC.Client.Services.KRPC;
using KRPC.Client.Services.SpaceCenter;

namespace KspWingman.Mcp;

/// <summary>One event: an id (growing), the time, a kind (phase, outcome, tower...), a line of text, and its data.</summary>
public sealed record WingmanEvent(long Id, DateTime Time, string Kind, string Text, object? Data = null);

public sealed class FlightEvents
{
    /// <summary>Events kept in memory; the log file (when set) keeps them all, up to MaxLogBytes.</summary>
    public const int Keep = 1000;

    /// <summary>The log stops growing here (LUNA EVO's audit 14, W2): a runaway flight can't fill the disk.</summary>
    public const long MaxLogBytes = 20L * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly LinkedList<WingmanEvent> _events = new();
    private readonly string? _logPath;
    private long _nextId = 1;

    public FlightEvents(string? logPath = null) => _logPath = logPath;

    /// <summary>Raised after each event (the tower client records outcomes with the knowledge store).</summary>
    public event Action<WingmanEvent>? Added;

    public WingmanEvent Add(string kind, string text, object? data = null)
    {
        WingmanEvent e;
        lock (_events)
        {
            e = new WingmanEvent(_nextId++, DateTime.UtcNow, kind, text, data);
            _events.AddLast(e);
            if (_events.Count > Keep)
                _events.RemoveFirst();
        }
        if (_logPath != null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_logPath))!);
                if (!File.Exists(_logPath) || new FileInfo(_logPath).Length < MaxLogBytes)
                    File.AppendAllText(_logPath, JsonSerializer.Serialize(e, Json) + "\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A full disk loses the log, not the flight.
            }
        }
        Added?.Invoke(e);
        return e;
    }

    /// <summary>Events after an id, oldest first (at most <paramref name="max"/>).</summary>
    public IReadOnlyList<WingmanEvent> After(long id, int max = 100)
    {
        lock (_events)
            return _events.Where(e => e.Id > id).Take(max).ToList();
    }

    public static string ToJson(WingmanEvent e) => JsonSerializer.Serialize(e, Json);
}

/// <summary>
/// Watches the agent's plane once a second (its own kRPC connection) and turns it into phase changes and flight
/// outcomes, whether the pilot, the agent or a person is flying.
/// </summary>
public sealed class FlightWatch : IDisposable
{
    private readonly KrpcLink _link;
    private readonly Director _director;
    private readonly FlightEvents _events;
    private readonly PhaseTracker _tracker = new();
    private string? _craft;
    private (FlightOutcome Outcome, DateTime Since)? _waiting;
    private readonly CancellationTokenSource _stop = new();

    public FlightWatch(string host, int port, Director director, FlightEvents events)
    {
        _link = new KrpcLink(host, port);
        _director = director;
        _events = events;
        _ = Task.Run(Loop);
    }

    public Phase Phase => _tracker.Phase;

    private async Task Loop()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var input = _link.Run(c =>
                {
                    if (c.KRPC().GameScene != GameScene.Flight)
                        return new PhaseInput(false, "", false, 0, 0, 0, _director.Doing);
                    var v = c.SpaceCenter().ActiveVessel;
                    if (FlightTools.Wrecked(v))
                        return new PhaseInput(true, "Flying", true, 0, 0, 0, _director.Doing);
                    var g = v.Flight(v.Orbit.Body.ReferenceFrame);
                    _craft = _director.CraftOf(FlightTools.Localized(_link, v.Name));
                    return new PhaseInput(true, v.Situation.ToString(), false, g.Speed, g.VerticalSpeed, g.SurfaceAltitude, _director.Doing);
                });
                if (_tracker.Update(input, DateTime.UtcNow, _director.Approach, _director.LastLanding) is { } change)
                {
                    _events.Add("phase", $"{change.From} → {change.To}", new { from = change.From.ToString(), to = change.To.ToString() });
                    if (change.Outcome is { } o)
                        _waiting = (o with { Craft = _craft }, DateTime.UtcNow);
                }
                // A landing's numbers come from the pilot's rollout, which can finish a moment after this watch sees the
                // plane stopped (the rig: a 10 m landing recorded without them, so graded as not good). Wait for them, 5 s.
                if (_waiting is { } w)
                {
                    var landing = w.Outcome.Landing ?? (w.Outcome.Result == "landed" ? _director.LastLanding : null);
                    if (landing != null || w.Outcome.Result != "landed" || DateTime.UtcNow - w.Since > TimeSpan.FromSeconds(5))
                    {
                        var o = w.Outcome with { Landing = landing, Finals = _director.Finals };
                        _events.Add("outcome", Describe(o), o);
                        _waiting = null;
                    }
                }
            }
            catch (Exception e) when (e is WingmanException or IOException or System.InvalidOperationException)
            {
                // KSP between scenes or closed: try again next second.
            }
            await Task.Delay(1000, _stop.Token).ContinueWith(_ => { });
        }
    }

    internal static string Describe(FlightOutcome o)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"Flight {o.Result} after {o.Minutes:0.0} min");
        if (o.Approach != null)
            text += $", {o.Approach}";
        if (o.GoArounds > 0)
            text += $", {o.GoArounds} go-around{(o.GoArounds == 1 ? "" : "s")}";
        if (o.Landing is { } l)
            text += string.Create(CultureInfo.InvariantCulture,
                $": stopped {l.DownRunway:0} m down {l.Runway}, {l.OffCenterline:0} m off the centerline, {l.HeadingError:0}° off the heading");
        if (o.Finals is { Count: > 0 } finals)
            text += "; finals: " + string.Join(", ", finals.Select(f => string.Create(CultureInfo.InvariantCulture,
                $"crossed the centerline {f.Weaves} time{(f.Weaves == 1 ? "" : "s")}, {Math.Abs(f.High):0} m {(f.High >= 0 ? "above" : "below")} the slope 1 km out")));
        return text + ".";
    }

    public void Dispose()
    {
        _stop.Cancel();
        _link.Dispose();
    }
}

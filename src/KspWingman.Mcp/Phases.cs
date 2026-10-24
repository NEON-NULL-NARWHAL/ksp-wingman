// KSP Wingman · src/KspWingman.Mcp/Phases.cs
// What the agent's plane is doing, as a flight phase, from its own numbers (not from status text), and the outcome of
// each flight. Pure: FlightWatch feeds it once a second; tests feed it recorded flights.

namespace KspWingman.Mcp;

public enum Phase
{
    NotFlying, // not in the flight scene (Space Center, menus), or no vessel
    Parked, // on the ground, stopped, not after a landing
    TakingOff, // on the ground, rolling, before a flight
    Climbing,
    Cruising,
    Descending,
    Holding, // the pilot circling a hold point
    OnRoute, // flying an approach's fixes
    OnFinal,
    Flaring,
    RollingOut,
    Stopped, // stopped on the ground after a landing
    Taxiing,
    Crashed,
    Splashed,
}

/// <summary>One reading: the scene, KSP's situation, the plane's numbers, and what the pilot says it's doing.</summary>
public readonly record struct PhaseInput(bool InFlight, string Situation, bool Wrecked, double Speed, double VerticalSpeed,
    double HeightAboveGround, string Pilot);

/// <summary>A change of phase (From → To), and the flight's outcome when a flight just ended.</summary>
public readonly record struct PhaseChange(Phase From, Phase To, FlightOutcome? Outcome);

/// <summary>
/// How a flight ended: landed (and where), crashed, splashed down, or ended otherwise (recovered or the scene left in the
/// air). The numbers come from the pilot's landing when there was one.
/// </summary>
/// <param name="Phases">The phases it went through, in order (the knowledge store grades skills from them).</param>
/// <param name="Craft">The vessel's name.</param>
/// <param name="MaxSpeed">The fastest it flew (m/s), and <paramref name="MaxHeight"/> the highest above the ground (m).</param>
/// <param name="Finals">How each final went (weaving, height 1 km out), from the pilot.</param>
public sealed record FlightOutcome(string Result, double Minutes, int GoArounds, string? Approach, LandingResult? Landing,
    IReadOnlyList<string>? Phases = null, string? Craft = null, double MaxSpeed = 0, double MaxHeight = 0,
    IReadOnlyList<FinalReport>? Finals = null);

/// <summary>Where a landing stopped: meters down the runway from the threshold, off the centerline, and the heading error.</summary>
public sealed record LandingResult(string Runway, double DownRunway, double OffCenterline, double HeadingError);

public sealed class PhaseTracker
{
    private Phase _phase = Phase.NotFlying;
    private DateTime? _flightStarted;
    private bool _landedThisFlight;
    private int _goArounds;
    private bool _inGoAround;
    private readonly List<Phase> _visited = [];
    private double _maxSpeed, _maxHeight;

    public Phase Phase => _phase;

    /// <summary>
    /// One reading at <paramref name="now"/>. <paramref name="approach"/> and <paramref name="landing"/> come from the
    /// pilot (what it's flying, how its last landing ended), for the outcome. Returns the change, or null when none.
    /// </summary>
    public PhaseChange? Update(PhaseInput s, DateTime now, string? approach = null, LandingResult? landing = null)
    {
        var next = Classify(s);
        if (Airborne(_phase) || Airborne(next))
        {
            _maxSpeed = Math.Max(_maxSpeed, s.Speed);
            _maxHeight = Math.Max(_maxHeight, s.HeightAboveGround);
        }
        // Go-arounds counted on the pilot's own word, once each.
        var goingAround = s.Pilot.Contains("going around", StringComparison.OrdinalIgnoreCase) ||
                          s.Pilot.StartsWith("Going around", StringComparison.Ordinal);
        if (goingAround && !_inGoAround)
            _goArounds++;
        _inGoAround = goingAround;
        if (next == _phase)
            return null;

        var from = _phase;
        _phase = next;
        if (_visited.Count == 0 || _visited[^1] != next)
            _visited.Add(next);
        if (Airborne(next) && _flightStarted == null)
        {
            _flightStarted = now;
            _goArounds = goingAround ? 1 : 0;
            _landedThisFlight = false;
        }
        if (next is Phase.RollingOut)
            _landedThisFlight = true;

        FlightOutcome? outcome = null;
        if (_flightStarted is { } started)
        {
            var minutes = (now - started).TotalMinutes;
            string? result = next switch
            {
                Phase.Crashed => "crashed",
                Phase.Splashed => "splashed",
                Phase.Stopped => "landed",
                Phase.NotFlying => _landedThisFlight ? null : "ended in the air", // recovered or left in flight
                Phase.Parked when !_landedThisFlight => "landed off a runway",
                _ => null,
            };
            if (result != null)
            {
                outcome = new FlightOutcome(result, Math.Round(minutes, 1), _goArounds, approach, result == "landed" ? landing : null,
                    _visited.Select(p => p.ToString()).ToList(), MaxSpeed: Math.Round(_maxSpeed), MaxHeight: Math.Round(_maxHeight));
                _visited.Clear();
                _maxSpeed = _maxHeight = 0;
                _flightStarted = null;
                _landedThisFlight = false; // a takeoff from here is a new flight
            }
            else if (next == Phase.NotFlying)
                _flightStarted = null; // after a landing (recovered from the runway): already reported
        }
        if (next == Phase.NotFlying)
            _visited.Clear(); // the next flight starts afresh
        return new PhaseChange(from, next, outcome);
    }

    private static bool Airborne(Phase p) => p is Phase.Climbing or Phase.Cruising or Phase.Descending or Phase.Holding or
        Phase.OnRoute or Phase.OnFinal or Phase.Flaring;

    private Phase Classify(PhaseInput s)
    {
        if (!s.InFlight)
            return Phase.NotFlying;
        if (s.Wrecked)
            return Phase.Crashed;
        var situation = s.Situation.ToLowerInvariant();
        if (situation == "splashed")
            return Phase.Splashed;
        if (situation is "landed" or "prelaunch")
        {
            if (s.Pilot.StartsWith("Taxiing", StringComparison.Ordinal))
                return Phase.Taxiing;
            // Parked after taxiing, even while the brakes still bite (the rig: 8.7 m/s to 0 in two seconds). Only right
            // after the taxi: the words stay in the pilot's line after a recovery, and held a new plane's whole takeoff
            // roll at Stopped in the rig.
            if (_phase == Phase.Taxiing)
                return Phase.Stopped;
            // Stopped, unless rolling: more than 3 m/s to start (a plane settling after it spawns wobbles), under 1 to stop.
            if (s.Speed < (_phase is Phase.Parked or Phase.Stopped ? 3 : 1))
                return _phase is Phase.RollingOut or Phase.Stopped or Phase.Taxiing || _landedThisFlight ? Phase.Stopped : Phase.Parked;
            // Rolling: out of a landing, or into a takeoff.
            return _phase is Phase.Flaring or Phase.OnFinal or Phase.RollingOut || _landedThisFlight ? Phase.RollingOut : Phase.TakingOff;
        }
        // In the air: the pilot's mode first, then the numbers.
        var p = s.Pilot;
        if (p.StartsWith("Landing, flaring", StringComparison.Ordinal))
            return Phase.Flaring;
        if (p.StartsWith("Landing, on final", StringComparison.Ordinal))
            return Phase.OnFinal;
        if (p.StartsWith("Holding at", StringComparison.Ordinal))
            return Phase.Holding;
        // Any of the tower's routes ("Flying RNAV 09 SOUTH: ...", "Flying VISUAL 09: ..."; the rig's visual was graded without its route).
        if (p.StartsWith("Flying RNAV", StringComparison.Ordinal) || p.StartsWith("Flying VISUAL", StringComparison.Ordinal) ||
            p.StartsWith("Flying FILED", StringComparison.Ordinal) || p.StartsWith("Flying DIRECT", StringComparison.Ordinal) ||
            p.StartsWith("Flying the tower", StringComparison.Ordinal) || p.StartsWith("Flying to the final", StringComparison.Ordinal) ||
            p.StartsWith("Going around", StringComparison.Ordinal) || p.StartsWith("Landing, going around", StringComparison.Ordinal))
            return Phase.OnRoute;
        if (s.VerticalSpeed > 3)
            return Phase.Climbing;
        if (s.VerticalSpeed < -3)
            return Phase.Descending;
        return Phase.Cruising;
    }
}

// KSP Wingman · src/KspWingman.Mcp/Landing.cs
// Landing a plane on a runway: to a point on the extended centerline, down a 4° glide slope, flare, roll out. Pure
// arithmetic like Formation, so it's tested without KSP.

namespace KspWingman.Mcp;

/// <summary>A runway: its landing end (threshold, ground altitude in m), the heading you land on, its length (m).</summary>
public readonly record struct Runway(string Name, Geo Threshold, double Heading, double Length)
{
    /// <summary>KSC's runway, landing east (09) from the sea side. Threshold measured in the rig: where launch puts a plane.</summary>
    public static readonly Runway Ksc = new("KSC runway 09", new Geo(-0.0486, -74.7244, 69), 90, 2400);

    public static Runway? Find(string name) =>
        name.Trim().ToUpperInvariant() is "KSC" or "RUNWAY" or "KSC RUNWAY" or "09" or "KSC 09" ? Ksc : null;
}

public enum LandingPhase { ToFinal, Final, Flare, Rollout, GoAround }

/// <summary>One step's answer: the phase, what to fly (null on the ground) and where the plane is from the touchdown point.</summary>
public readonly record struct LandingStep(LandingPhase Phase, PlaneTarget? Target, double Along, double Cross, string Doing);

/// <summary>
/// Lands on <paramref name="runway"/>. <paramref name="approach"/>: null for the tower's pick of the published approaches
/// (the default), "visual" for the old hand-made circuit, or an approach's name. <paramref name="setup"/>: the plane's
/// own speeds (Handling.cs); null: the Aeris 3A's.
/// </summary>
public sealed class Landing(Runway runway, string? approach = null, Approach? given = null, PlaneSetup? setup = null)
{
    public const double GlideSlope = 4; // degrees
    public const double FinalDistance = 8000; // m before touchdown where the final approach starts
    public const double TouchdownAfterThreshold = 300; // m (400 plus the flare's float stopped the third 0.4 landing 77 m before the end)
    public const double ApproachSpeed = 90, LandingSpeed = 75; // m/s
    public const double FlareHeight = 8; // m above the ground (12 floated for 600 m)

    /// <summary>Meters before touchdown where it decides to land or go around (lined up within 60 m, at most 40 m high).</summary>
    public const double DecisionPoint = 600;

    private LandingPhase _phase = LandingPhase.ToFinal;
    private bool _outside;
    private readonly PlaneSetup _setup = setup ?? PlaneSetup.Default;
    private int _weaves, _side;
    private double? _high;
    private bool _onFinal;

    /// <summary>Called as each final ends (flare or go-around): how it went, for the plane's setup (Handling.cs).</summary>
    public Action<FinalReport>? FinalEnded { get; set; }

    /// <summary>The climb-out speed: never slower than the plane's approach speed allows.</summary>
    private double ClimbSpeed => Math.Max(120, _setup.ApproachSpeed + 10);
    private Route? _route;
    private readonly bool _visual = approach?.Trim().Equals("visual", StringComparison.OrdinalIgnoreCase) == true;
    private readonly Approach? _asked = given ?? (approach is { Length: > 0 } a && !a.Trim().Equals("visual", StringComparison.OrdinalIgnoreCase) ? Approach.Find(a) : null);

    public Runway Runway => runway;
    public LandingPhase Phase => _phase;

    /// <summary>The approach being flown (null: visual), once the first step has picked it.</summary>
    public Approach? Approach => _route?.Approach;

    /// <summary>
    /// What the steering, rudder and brakes should be on the runway: steering eases off as the plane slows (at walking
    /// speed it swung both 0.3 landings 15-50 degrees off the runway heading). (Brakes only within 4 degrees kept them off
    /// so long that the first 0.4 landing rolled 475 m off the end of the runway.)
    /// The second 0.4 landing ground-looped: at 57 m/s the nose wheel swung it 111° → 63° → 197°. So the steering also
    /// fades at high speed (a nose wheel bites hard at 50 m/s), every axis is damped by the yaw rate (degrees a second,
    /// positive turning right), the rudder helps while it's fast, and the brakes wait for a steady nose below 50 m/s.
    /// </summary>
    /// <param name="remaining">Runway left ahead (m); with <paramref name="braking"/> (m/s², the ground test's) it brakes
    /// at any speed when it otherwise couldn't stop in time.</param>
    public static (double Steer, double Rudder, bool Brakes) Rollout(double speed, double headingError, double cross, double yawRate = 0,
        double remaining = double.MaxValue, double braking = PlaneSetup.ReferenceBraking)
    {
        // Back to the centerline harder (2.14): a 1-2° heading error on a 1 km rollout is 17-35 m; at 1° per 10 m off,
        // the short visual's landings touched down ~11 m off and stopped 16-23 m off in the rig.
        var aim = headingError + Math.Clamp(-cross / 5, -6, 6); // degrees to turn (positive: right)
        var fade = Math.Clamp((speed - 5) / 15, 0, 1) * Math.Clamp(20 / Math.Max(speed, 1), 0.15, 1);
        var steer = Math.Clamp((-aim / 10 + 0.08 * yawRate) * fade, -0.3, 0.3);
        // Below 60 m/s with a steady nose, as before; faster too when the runway left is short for this plane's brakes
        // (the rig's Aeris 4A touched down at 93 m/s, was still at 81 m/s after 2 km and ran off the end).
        var tooShort = remaining < StopDistance(speed, braking) * 1.3 + 150;
        // The wheel's sign is KSP's (positive steers left); the rudder's is the pilot's (positive yaws right). Braking
        // fast, the rudder pushes harder against a swing (the wheels' grip is uneven).
        var rudderGain = speed >= 60 && tooShort ? 1.7 : 1;
        var rudder = speed > 25 ? Math.Clamp(rudderGain * (0.03 * aim - 0.02 * yawRate), -0.4, 0.4) : 0;
        // Fast, only while the nose is steady, let go as soon as it swings (like anti-skid): braking hard at 90 m/s swung
        // the Aeris 4A to 15° off and 71 m off the centerline in the rig; 4° and 3 °/s let the brakes on so rarely that it
        // only slowed 96 → 82 m/s in 13 s.
        var steady = speed < 60 ? Math.Abs(headingError) < 8 && Math.Abs(yawRate) < 5 : Math.Abs(headingError) < 6 && Math.Abs(yawRate) < 6;
        var brakes = speed < 8 || ((speed < 60 || tooShort) && steady);
        return (steer, rudder, brakes);
    }

    /// <summary>Meters to stop from <paramref name="speed"/> braking at half the measured rate (wheels skid, brakes fade).</summary>
    public static double StopDistance(double speed, double braking) => speed * speed / (2 * Math.Max(0.5 * braking, 0.5));

    /// <summary>
    /// Rolling out too fast to stop in the runway left, still fast enough to fly (above <paramref name="rotateSpeed"/>):
    /// go around from the runway (a touch-and-go, the owner's "risk the landing, then go around").
    /// </summary>
    public static bool TouchAndGo(double speed, double remaining, double braking, double rotateSpeed) =>
        speed > rotateSpeed + 5 && remaining < StopDistance(speed, braking) + 100 && remaining > 300;

    /// <summary>
    /// One step. <paramref name="heightAboveGround"/> from the radar altimeter; <paramref name="onGround"/> once the wheels
    /// are down (KSP's Landed). Along is negative before the touchdown point; cross is positive right of the centerline.
    /// </summary>
    public LandingStep Step(double bodyRadius, Geo plane, double heading, double speed, double heightAboveGround, bool onGround)
    {
        var (along, cross) = RunwayFrame(bodyRadius, plane);
        var tan = Math.Tan(GlideSlope * Math.PI / 180);
        var touchdownAltitude = runway.Threshold.Altitude;

        if (onGround)
        {
            _phase = LandingPhase.Rollout;
            return new LandingStep(_phase, null, along, cross, Describe("rolling out", along, cross, speed));
        }

        if (!_visual && _phase is LandingPhase.ToFinal or LandingPhase.GoAround)
        {
            // A published approach: the tower's pick (again after a go-around, from wherever the plane is), or the one asked.
            if (_phase == LandingPhase.GoAround && heightAboveGround < 300)
            {
                var climbOut = new PlaneTarget(runway.Heading, touchdownAltitude + 900, ClimbSpeed, ClimbRate: 8, Landing: true);
                return new LandingStep(_phase, climbOut, along, cross, "Going around: climbing out straight ahead.");
            }
            // Already lined up on the slope: the final now, no route (like the visual circuit).
            var linedUp = Math.Abs(Pilot.Wrap(heading - runway.Heading)) < 30 && Math.Abs(cross) < 600 && along < -300 &&
                along > -FinalDistance - 3000 && Math.Abs(plane.Altitude - (touchdownAltitude + Math.Max(0, -along) * tan)) < 150;
            if (!linedUp && (_route == null || _phase == LandingPhase.GoAround))
            {
                _route = new Route(_asked ?? Tower.Pick(Approach.Ksc, along, cross, heading, runway.Heading));
                _phase = LandingPhase.ToFinal;
            }
            if (!linedUp && _route!.Step(along, cross, speed) is { } fixLeg)
            {
                // The fix's speed, within what this plane flies: no slower than its approach speed, and no faster than
                // its circuit speed when it's hard to slow down.
                var leg = fixLeg with { Speed = Math.Max(_setup.ApproachSpeed, _setup.CircuitSpeed < PlaneSetup.Default.CircuitSpeed ? Math.Min(fixLeg.Speed, _setup.CircuitSpeed) : fixLeg.Speed) };
                var next = _route.Next;
                var (nN, nE) = Point(next.Along, next.Cross);
                var (mN, mE) = Point(along, cross);
                var distance = Math.Sqrt(Math.Pow(nN - mN, 2) + Math.Pow(nE - mE, 2));
                return new LandingStep(_phase, leg, along, cross, string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"Flying {_route.Approach.Name}: to {next.Name}, {distance / 1000:0.0} km, at {next.Altitude:0} m and {next.Speed:0} m/s."));
            }
            _phase = LandingPhase.Final;
        }

        if (_visual && _phase is LandingPhase.ToFinal or LandingPhase.GoAround)
        {
            // Climb out (go-around) or head for the start of the final approach.
            var fixAltitude = touchdownAltitude + FinalDistance * tan;
            // Like a circuit: from inside the start of the final, first to a point 2 km further out and a turn's width
            // (2.5 km at 120 m/s) to the side, then back in toward the start. Flown straight at it, the plane got there
            // heading the wrong way and its 180° turn onto final ended 4 km off the centerline.
            var side = cross >= 0 ? 1 : -1;
            // Latched: once out there, it heads back in even when that takes it inside the line again (without the latch
            // it turned back and forth across it in the rig).
            _outside |= along < -FinalDistance - 500;
            var outside = _outside;
            var (aimAlong, aimCross) = outside ? (-FinalDistance, 0.0) : (-FinalDistance - 2000, side * 2500.0);
            var (aimNorth, aimEast) = Point(aimAlong, aimCross);
            var (meNorth, meEast) = Point(along, cross);
            var toAim = Math.Sqrt(Math.Pow(aimNorth - meNorth, 2) + Math.Pow(aimEast - meEast, 2));
            var aligned = Math.Abs(Pilot.Wrap(heading - runway.Heading)) < 30 && Math.Abs(cross) < 600;
            // Join the final wherever it's lined up and near the slope (a plane 2 km out needn't go back 8 km).
            var nearSlope = Math.Abs(plane.Altitude - (touchdownAltitude + Math.Max(0, -along) * tan)) < 150;
            if ((aligned && nearSlope && along < -300 && along > -FinalDistance - 3000) ||
                // Out past the start and roughly facing the runway: join the final where it is, and let the final's
                // centerline steering (up to 45°) line it up. Waiting to reach the exact point made it miss the point
                // and circle back to chase it (seen by the owner).
                (outside && Math.Abs(Pilot.Wrap(heading - runway.Heading)) < 60 && along < -3000 && Math.Abs(cross) < 4000))
                _phase = LandingPhase.Final;
            else
            {
                var toFix = toAim + (outside ? 0 : 2000);
                var bearing = (Math.Atan2(aimEast - meEast, aimNorth - meNorth) * 180 / Math.PI + 360) % 360;
                if (_phase == LandingPhase.GoAround && heightAboveGround < 300)
                    bearing = runway.Heading; // straight ahead until there's room to turn
                var target = new PlaneTarget(bearing, Math.Max(fixAltitude, touchdownAltitude + 300), _setup.CircuitSpeed);
                return new LandingStep(_phase, target, along, cross,
                    $"{(_phase == LandingPhase.GoAround ? "Going around" : "Flying to the final approach")}: {toFix / 1000:0.0} km to go.");
            }
        }

        // Final, then flare: track the centerline and the glide slope. The aim point is 3 s ahead up to the Aeris 3A's
        // 90 m/s and grows with the speed cubed above it (1.6 km at 140 m/s), on the scale of the turn's width: aiming
        // 3 s ahead, the rig's Aeris 4A (140 m/s, turns 4-5 km wide) snaked ±400 m on every final and went around three
        // times; with the speed squared a laggy simulated plane still crossed twice. Each centerline crossing on this
        // final stretches it once more.
        var calm = 1 + 0.5 * Math.Min(_weaves, 4);
        var lookahead = Math.Max(3 * speed * Math.Pow(Math.Max(1, speed / 90), 3), 250) * calm;
        // Far out, join the centerline at up to 45° (the turn onto final in the rig ended 3.6 km wide); close in, 25° at most.
        var intercept = along < -3000 ? 45 : 25;
        var correction = Math.Clamp(Math.Atan2(-cross, lookahead) * 180 / Math.PI, -intercept, intercept);
        var course = (runway.Heading + correction + 360) % 360;
        var glideAltitude = touchdownAltitude + Math.Max(0, -along) * tan;
        var altitude = plane.Altitude;

        // Go around if it can't make it: off the centerline close in, or still flying well past the touchdown point.
        // ...or too close to the ground still far out: the hills west of KSC rise to a few hundred meters under the slope.
        if (_phase != LandingPhase.Flare)
            Track(along, cross, altitude - glideAltitude);
        // Risk the landing (the owner's idea): far out only a plane badly off goes around; the decision is made at
        // DecisionPoint, where it must be nearly lined up and on the slope. A go-around from there is still safe.
        var above = altitude - glideAltitude;
        if ((along > -1500 && Math.Abs(cross) > 250) || (along > -DecisionPoint && Math.Abs(cross) > 60) ||
            along > runway.Length - TouchdownAfterThreshold - 900 ||
            (along > -1000 && above > 120) || (along > -DecisionPoint && above > 40) || (along < -1200 && heightAboveGround < 80))
        {
            _phase = LandingPhase.GoAround;
            _outside = false; // the circuit again from the start
            EndFinal();
            var climb = new PlaneTarget(runway.Heading, touchdownAltitude + 600, ClimbSpeed, ClimbRate: 8, Landing: true);
            return new LandingStep(_phase, climb, along, cross, Describe("going around (not lined up)", along, cross, speed));
        }

        if (heightAboveGround < FlareHeight && along > -400 && _phase != LandingPhase.Flare)
        {
            _phase = LandingPhase.Flare;
            EndFinal();
        }
        if (_phase == LandingPhase.Flare)
        {
            var flare = new PlaneTarget(runway.Heading + correction / 2, touchdownAltitude, 0, ClimbRate: -1, Landing: true);
            return new LandingStep(_phase, flare, along, cross, Describe("flaring", along, cross, speed));
        }

        _phase = LandingPhase.Final;
        var sink = -speed * tan + Math.Clamp(0.15 * (glideAltitude - altitude), -_setup.SinkCorrection, 6);
        // Faster far out, slowing down the slope to the landing speed 1.5 km out, at what this plane can lose (Handling.cs).
        var approachSpeed = _setup.FinalSpeed(-along);
        // 30 degrees of bank far out (20 turned onto final too wide in the rig), 20 near the ground.
        var bank = along < -3000 && heightAboveGround > 300 ? 30 : 20;
        var final = new PlaneTarget(course, glideAltitude, approachSpeed, ClimbRate: Math.Clamp(sink, -15, 4), Landing: true, MaxBank: bank);
        return new LandingStep(_phase, final, along, cross, Describe("on final", along, cross, speed));
    }

    /// <summary>
    /// On final: counts the centerline crossings (more than 25 m each side, between 6 km and 300 m out: weaving) and
    /// notes how high above the slope it was 1 km out.
    /// </summary>
    private void Track(double along, double cross, double aboveSlope)
    {
        _onFinal = true;
        if (along is > -6000 and < -300)
        {
            var side = cross > 25 ? 1 : cross < -25 ? -1 : 0;
            if (side != 0 && _side != 0 && side != _side)
                _weaves++;
            if (side != 0)
                _side = side;
        }
        if (along >= -1000 && _high == null)
            _high = aboveSlope;
    }

    private void EndFinal()
    {
        if (!_onFinal)
            return;
        FinalEnded?.Invoke(new FinalReport(_weaves, Math.Round(_high ?? 0)));
        (_onFinal, _weaves, _side, _high) = (false, 0, 0, null);
    }

    /// <summary>Meters along the runway from the touchdown point (negative: before it) and right of the centerline.</summary>
    public (double Along, double Cross) RunwayFrame(double bodyRadius, Geo plane)
    {
        var (north, east) = Formation.Offset(bodyRadius, runway.Threshold, plane);
        var h = runway.Heading * Math.PI / 180;
        var (fN, fE) = (Math.Cos(h), Math.Sin(h));
        var (rN, rE) = (-fE, fN);
        return (north * fN + east * fE - TouchdownAfterThreshold, north * rN + east * rE);
    }

    private (double North, double East) Point(double along, double cross)
    {
        var h = runway.Heading * Math.PI / 180;
        var a = along + TouchdownAfterThreshold;
        return (a * Math.Cos(h) - cross * Math.Sin(h), a * Math.Sin(h) + cross * Math.Cos(h));
    }

    private static string Describe(string what, double along, double cross, double speed) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"Landing, {what}: {(along < 0 ? $"{-along / 1000:0.0} km before" : $"{along:0} m past")} the touchdown point, " +
            $"{Math.Abs(cross):0} m {(cross >= 0 ? "right" : "left")} of the centerline, {speed:0} m/s.");
}

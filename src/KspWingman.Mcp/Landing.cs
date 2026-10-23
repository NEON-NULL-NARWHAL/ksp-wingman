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

public sealed class Landing(Runway runway)
{
    public const double GlideSlope = 4; // degrees
    public const double FinalDistance = 8000; // m before touchdown where the final approach starts
    public const double TouchdownAfterThreshold = 400; // m
    public const double ApproachSpeed = 90, LandingSpeed = 75; // m/s
    public const double FlareHeight = 12; // m above the ground

    private LandingPhase _phase = LandingPhase.ToFinal;
    private bool _outside;

    public Runway Runway => runway;
    public LandingPhase Phase => _phase;

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

        if (_phase is LandingPhase.ToFinal or LandingPhase.GoAround)
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
                (outside && toAim < 800 && Math.Abs(Pilot.Wrap(heading - runway.Heading)) < 70))
                _phase = LandingPhase.Final;
            else
            {
                var toFix = toAim + (outside ? 0 : 2000);
                var bearing = (Math.Atan2(aimEast - meEast, aimNorth - meNorth) * 180 / Math.PI + 360) % 360;
                if (_phase == LandingPhase.GoAround && heightAboveGround < 300)
                    bearing = runway.Heading; // straight ahead until there's room to turn
                var target = new PlaneTarget(bearing, Math.Max(fixAltitude, touchdownAltitude + 300), 120);
                return new LandingStep(_phase, target, along, cross,
                    $"{(_phase == LandingPhase.GoAround ? "Going around" : "Flying to the final approach")}: {toFix / 1000:0.0} km to go.");
            }
        }

        // Final, then flare: track the centerline (aim a few seconds ahead) and the glide slope.
        var lookahead = Math.Max(3 * speed, 250);
        // Far out, join the centerline at up to 45° (the turn onto final in the rig ended 3.6 km wide); close in, 25° at most.
        var intercept = along < -3000 ? 45 : 25;
        var correction = Math.Clamp(Math.Atan2(-cross, lookahead) * 180 / Math.PI, -intercept, intercept);
        var course = (runway.Heading + correction + 360) % 360;
        var glideAltitude = touchdownAltitude + Math.Max(0, -along) * tan;
        var altitude = plane.Altitude;

        // Go around if it can't make it: off the centerline close in, or still flying well past the touchdown point.
        // ...or too close to the ground still far out: the hills west of KSC rise to a few hundred meters under the slope.
        if ((along > -1500 && Math.Abs(cross) > 120) || along > runway.Length - TouchdownAfterThreshold - 900 ||
            (along > -1000 && altitude - glideAltitude > 60) || (along < -1200 && heightAboveGround < 80))
        {
            _phase = LandingPhase.GoAround;
            _outside = false; // the circuit again from the start
            var climb = new PlaneTarget(runway.Heading, touchdownAltitude + 600, 120, ClimbRate: 8, Landing: true);
            return new LandingStep(_phase, climb, along, cross, Describe("going around (not lined up)", along, cross, speed));
        }

        if (heightAboveGround < FlareHeight && along > -400)
            _phase = LandingPhase.Flare;
        if (_phase == LandingPhase.Flare)
        {
            var flare = new PlaneTarget(runway.Heading + correction / 2, touchdownAltitude, 0, ClimbRate: -1, Landing: true);
            return new LandingStep(_phase, flare, along, cross, Describe("flaring", along, cross, speed));
        }

        _phase = LandingPhase.Final;
        var sink = -speed * tan + Math.Clamp(0.15 * (glideAltitude - altitude), -6, 6);
        var approachSpeed = along < -3000 ? ApproachSpeed : LandingSpeed;
        var final = new PlaneTarget(course, glideAltitude, approachSpeed, ClimbRate: Math.Clamp(sink, -15, 4), Landing: true);
        return new LandingStep(_phase, final, along, cross, Describe("on final", along, cross, speed));
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

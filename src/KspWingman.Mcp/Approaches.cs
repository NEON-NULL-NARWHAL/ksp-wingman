// KSP Wingman · src/KspWingman.Mcp/Approaches.cs
// Published approaches (the owner's idea: RNAV routes like real airports), the tower that picks one, and the route
// follower that flies it. A route ends at the final approach fix; Landing flies the final from there. Pure arithmetic.

using System.Globalization;

namespace KspWingman.Mcp;

/// <summary>
/// A named point of an approach, placed by the runway: meters along it from the touchdown point (negative: before) and
/// to the right of the centerline (negative: left). Altitude (m above sea level) and speed (m/s) to cross it at.
/// </summary>
public readonly record struct Fix(string Name, double Along, double Cross, double Altitude, double Speed);

/// <summary>An approach to a runway: its fixes in order, the last one the final approach fix, and when to fly it.</summary>
public sealed record Approach(string Name, Runway Runway, string WhenToUse, IReadOnlyList<Fix> Fixes)
{
    /// <summary>The final approach fix, on the centerline where the 4° glide slope starts (8 km out, 628 m at KSC).</summary>
    public static Fix FinalFix(Runway runway) => new($"F{runway.Name[^2..]}", -Landing.FinalDistance, 0,
        runway.Threshold.Altitude + Landing.FinalDistance * Math.Tan(Landing.GlideSlope * Math.PI / 180), Landing.ApproachSpeed);

    /// <summary>
    /// KSC runway 09 (landing east). KSC sits on a coast: the sea is east, the hills west under the final reach a few
    /// hundred meters, so every fix is at least 750 m up except the final fix. North and south mirror each other, a
    /// traffic pattern: a downwind leg west 4 km beside the runway, a 90° turn onto a base leg toward the centerline,
    /// a 90° turn onto the extended centerline 11 km out, then the final fix. The turns are flown at 90 m/s: a turn's
    /// lead is its radius (1.8 km at 25° of bank), so two of them fit the 4 km base leg. (A single 135° turn from a 3 km
    /// downwind swung the plane across the centerline in the tests.)
    /// </summary>
    public static IReadOnlyList<Approach> Ksc { get; } =
    [
        new("RNAV 09 STRAIGHT-IN", Runway.Ksc, "from the west, already roughly lined up with the runway",
            [new("KSC-W12", -12_000, 0, 900, 120), FinalFix(Runway.Ksc)]),
        new("RNAV 09 NORTH", Runway.Ksc, "from the north or north-east, or over KSC north of the runway",
            [new("KSC-N1", -1000, -4000, 900, 110), new("KSC-N2", -11_000, -4000, 800, 90), new("KSC-N3", -11_000, 0, 750, 90), FinalFix(Runway.Ksc)]),
        new("RNAV 09 SOUTH", Runway.Ksc, "from the south or south-east, or over KSC south of the runway",
            [new("KSC-S1", -1000, 4000, 900, 110), new("KSC-S2", -11_000, 4000, 800, 90), new("KSC-S3", -11_000, 0, 750, 90), FinalFix(Runway.Ksc)]),
    ];

    public static Approach? Find(string name) =>
        Ksc.FirstOrDefault(a => a.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase) ||
                                a.Name.EndsWith(" " + name.Trim(), StringComparison.OrdinalIgnoreCase));

    public string Describe() => $"{Name} ({WhenToUse}): " + string.Join(" → ", Fixes.Select(f =>
        string.Create(CultureInfo.InvariantCulture, $"{f.Name} {f.Altitude:0} m {f.Speed:0} m/s"))) + $" → land {Runway.Name}.";
}

/// <summary>The tower: picks the approach for where the plane is, and says the clearance.</summary>
public static class Tower
{
    /// <summary>
    /// Plain rules (an AI can do the talking later): far out west and facing the runway, straight in; otherwise the
    /// side of the runway the plane is on, so it never crosses the final to get to its downwind leg.
    /// </summary>
    public static Approach Pick(IReadOnlyList<Approach> approaches, double along, double cross, double heading, double runwayHeading)
    {
        if (along < -9000 && Math.Abs(cross) < 3000 && Math.Abs(Pilot.Wrap(heading - runwayHeading)) < 60)
            return approaches.First(a => a.Name.EndsWith("STRAIGHT-IN", StringComparison.Ordinal));
        return approaches.First(a => a.Name.EndsWith(cross < 0 ? "NORTH" : "SOUTH", StringComparison.Ordinal));
    }

    public static string Clearance(string callSign, Approach approach)
    {
        var first = approach.Fixes[0];
        return string.Create(CultureInfo.InvariantCulture,
            $"KSC tower: {callSign}, cleared {approach.Name}. Proceed to {first.Name} at {first.Altitude:0} m, {first.Speed:0} m/s, " +
            $"then {string.Join(", ", approach.Fixes.Skip(1).Select(f => f.Name))}, land {approach.Runway.Name}.");
    }
}

/// <summary>A hold: right-hand circles around a point, at its altitude (a tower holds the second plane like this).</summary>
public static class Holding
{
    public const double Speed = 100;

    /// <summary>
    /// What to fly to circle <paramref name="center"/> clockwise at <paramref name="radius"/> meters: toward it while
    /// far, then along the circle with the center on the right, turning in or out to stay on it.
    /// </summary>
    public static PlaneTarget Target(double bodyRadius, Geo me, Geo center, double radius)
    {
        var (north, east) = Formation.Offset(bodyRadius, me, center);
        var distance = Math.Sqrt(north * north + east * east);
        var toCenter = Math.Atan2(east, north) * 180 / Math.PI;
        var heading = distance > radius * 1.5
            ? toCenter
            : toCenter - 90 + Math.Clamp((distance - radius) / radius * 60, -40, 40);
        return new PlaneTarget((heading + 360) % 360, center.Altitude, Speed);
    }
}

/// <summary>Flies an approach's fixes in order: stays on the line between them, turns early, meets heights and speeds.</summary>
public sealed class Route(Approach approach)
{
    /// <summary>The bank the route plans its turns for (the pilot banks up to 30° outside the final).</summary>
    public const double TurnBank = 25;

    private int _leg;

    public Approach Approach => approach;
    public Fix Next => approach.Fixes[Math.Min(_leg, approach.Fixes.Count - 1)];
    public bool Done => _leg >= approach.Fixes.Count;

    /// <summary>
    /// One step from where the plane is (runway frame, like Landing): the target to fly, or null once past the last
    /// fix (the final approach starts). Legs after the first follow the line from the previous fix.
    /// </summary>
    public PlaneTarget? Step(double along, double cross, double speed)
    {
        while (!Done)
        {
            var fix = approach.Fixes[_leg];
            var (fromAlong, fromCross) = _leg == 0 ? (along, cross) : (approach.Fixes[_leg - 1].Along, approach.Fixes[_leg - 1].Cross);
            var (dA, dC) = (fix.Along - fromAlong, fix.Cross - fromCross);
            var legLength = Math.Max(Math.Sqrt(dA * dA + dC * dC), 1);
            var (uA, uC) = (dA / legLength, dC / legLength);
            // Where the plane is along the leg, and how far off its line (positive: right of the line, flying it).
            var toGo = (fix.Along - along) * uA + (fix.Cross - cross) * uC;
            var offLine = -(along - fromAlong) * uC + (cross - fromCross) * uA;
            var legCourse = Math.Atan2(uC, uA) * 180 / Math.PI; // relative to the runway heading, positive: to the right
            if (toGo < TurnLead(speed, _leg, legCourse) || (_leg > 0 && toGo < 0))
            {
                _leg++;
                continue;
            }
            var correction = _leg == 0 ? 0 : Math.Clamp(Math.Atan2(-offLine, Math.Max(3 * speed, 300)) * 180 / Math.PI, -40, 40);
            var heading = (approach.Runway.Heading + legCourse + correction + 360) % 360;
            return new PlaneTarget(heading, fix.Altitude, fix.Speed);
        }
        return null;
    }

    /// <summary>How far before a fix to start turning onto the next leg: the turn's radius times tan(half the turn).</summary>
    private double TurnLead(double speed, int leg, double course)
    {
        if (leg + 1 >= approach.Fixes.Count)
            return 600; // the final fix: the final's own steering takes over from here
        var (from, to) = (approach.Fixes[leg], approach.Fixes[leg + 1]);
        var nextCourse = Math.Atan2(to.Cross - from.Cross, to.Along - from.Along) * 180 / Math.PI;
        var radius = speed * speed / (9.81 * Math.Tan(TurnBank * Math.PI / 180));
        var turn = Math.Abs(Pilot.Wrap(nextCourse - course));
        return Math.Min(radius * Math.Tan(turn / 2 * Math.PI / 180), 3000);
    }
}

// KSP Wingman · src/KspWingman.Tui/FlightPlan.cs
// Flight plans the agent files itself (the owner's idea): LUNA EVO's tower publishes its table (fixes, the legs allowed
// between them, the final fixes); the planner reads it as text, picks fixes like a pilot reading the charts, and code
// checks the plan with the tower's own rules before filing it.

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KspWingman.Tui;

public static partial class FlightPlan
{
    private const double ThresholdLatitude = -0.0486, ThresholdLongitude = -74.7244, TouchdownAfterThreshold = 300;
    private const double MetersPerDegree = 600_000 * Math.PI / 180;

    /// <summary>Meters along KSC's runway 09 from the touchdown point (negative: before it) and right of the centerline (south).</summary>
    public static (double Along, double Cross) Frame(double latitude, double longitude)
    {
        var north = (latitude - ThresholdLatitude) * MetersPerDegree;
        var east = (longitude - ThresholdLongitude) * MetersPerDegree * Math.Cos(latitude * Math.PI / 180);
        return (east - TouchdownAfterThreshold, -north);
    }

    /// <summary>Where a point is, in words a small model reads: "1.0 km before the touchdown point, 4.0 km south".</summary>
    public static string Where(double along, double cross) => string.Create(CultureInfo.InvariantCulture,
        $"{Math.Abs(along) / 1000:0.0} km {(along < 0 ? "before" : "past")} the touchdown point, {Math.Abs(cross) / 1000:0.0} km {(cross >= 0 ? "south" : "north")}");

    private static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static double Num(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;

    /// <summary>The tower's table as text: each fix, which fix may follow which, the final fixes.</summary>
    public static string Describe(JsonNode table)
    {
        var text = new StringBuilder("KSC runway 09 (landing east). Fixes:\n");
        foreach (var f in (table["fixes"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var (a, c) = Frame(Num(f["latitude"]), Num(f["longitude"]));
            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- {Str(f["name"])}: {Where(a, c)}, {Num(f["altitude"]):0} m, {Num(f["speed"]):0} m/s"));
        }
        text.AppendLine("Legs allowed (from → to):");
        foreach (var l in (table["legs"] as JsonArray ?? []).OfType<JsonArray>())
            text.AppendLine($"- {Str(l[0])} → {Str(l[1])}");
        text.Append("A plan ends on a final fix: " + string.Join(" or ", (table["finals"] as JsonArray ?? []).Select(Str)) + ".");
        return text.ToString();
    }

    /// <summary>The tower's rules, checked before filing: known fixes, published legs, a final fix last, the first within 25 km.</summary>
    public static string? Check(JsonNode table, IReadOnlyList<string> names, double along, double cross)
    {
        if (names.Count is 0 or > 12)
            return "a plan is 1 to 12 fixes";
        var fixes = (table["fixes"] as JsonArray ?? []).OfType<JsonObject>().ToDictionary(f => Str(f["name"]), StringComparer.OrdinalIgnoreCase);
        var legs = (table["legs"] as JsonArray ?? []).OfType<JsonArray>().Select(l => (Str(l[0]), Str(l[1]))).ToHashSet();
        var finals = (table["finals"] as JsonArray ?? []).Select(Str).ToHashSet();
        foreach (var n in names)
            if (!fixes.ContainsKey(n))
                return $"no fix called {n}";
        for (var i = 1; i < names.Count; i++)
            if (!legs.Contains((Str(fixes[names[i - 1]]["name"]), Str(fixes[names[i]]["name"]))))
                return $"{names[i - 1]} to {names[i]} isn't a published leg";
        if (!finals.Contains(Str(fixes[names[^1]]["name"])))
            return "a plan ends on a final fix";
        var (fa, fc) = Frame(Num(fixes[names[0]]["latitude"]), Num(fixes[names[0]]["longitude"]));
        return Math.Sqrt(Math.Pow(fa - along, 2) + Math.Pow(fc - cross, 2)) > 25_000 ? $"{names[0]} is more than 25 km away" : null;
    }

    public const string System = "You are an AI pilot's flight planner. You build flight plans from the published fixes, like a pilot reading the charts. Answer briefly.";

    public static string Prompt(string table, string where, string? problem = null) =>
        $"{table}\n\nThe plane is {where}. Build the shortest sensible flight plan from here to land: the first fix " +
        "should be ahead of the plane or close to it, then only published legs, ending on a final fix." +
        (problem != null ? $" Your last plan was refused: {problem}. Fix it." : "") +
        "\nAnswer exactly: PLAN: <fix> <fix> ... (fix names only, in order)";

    /// <summary>The planner's fix list ("PLAN: KSC-DS KSC-S1 KSC-S2 KSC-S3 F09"), or empty.</summary>
    public static IReadOnlyList<string> Parse(string answer)
    {
        var m = PlanLine().Match(answer);
        return m.Success ? FixName().Matches(m.Groups[1].Value).Select(x => x.Value.ToUpperInvariant()).Take(12).ToList() : [];
    }

    [GeneratedRegex(@"PLAN:\s*([^\r\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex PlanLine();

    [GeneratedRegex(@"[A-Za-z0-9]+(?:-[A-Za-z0-9]+)*")]
    private static partial Regex FixName();

    /// <summary>Latitude and longitude from status ("lat -0.0486 lon -74.7000"), or null.</summary>
    public static (double Latitude, double Longitude)? Position(string status)
    {
        var m = LatLon().Match(status);
        return m.Success ? (double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)) : null;
    }

    [GeneratedRegex(@"lat (-?[\d.]+) lon (-?[\d.]+)")]
    private static partial Regex LatLon();
}

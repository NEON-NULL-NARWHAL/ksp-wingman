// KSP Wingman · src/KspWingman.Tui/Drills.cs
// The free-will drills (the owner's ideas): Explore (the planner picks where to fly, leg by leg, and what it finds goes
// to every agent's map) and Approach variety (the planner picks a riskier final of its own: steep, shallow, fast, slow
// or long, within the plane's limits). The planner decides; code checks and flies. Prompts and parsing here, pure.
// Guide:   Meat and Potatoes/07-Explore-And-Fuel.md

using System.Globalization;
using System.Text.RegularExpressions;

namespace KspWingman.Tui;

public static partial class Drills
{
    public const string ExploreSystem =
        "You plan exploration flights for an AI pilot in Kerbal Space Program, around the Kerbal Space Center (KSC). You " +
        "choose where to go next; the pilot flies it and checks your choice for range and fuel. Answer briefly.";

    /// <summary>The MCP server's explore range (km) and fuel reserve (its Exploring and FuelGauge), for the box and going home.</summary>
    public const double ExploreRangeKm = 60, FuelReserve = 0.25;

    /// <summary>Legs in one explore flight at most (it heads home earlier on HOME, low fuel or a refused leg twice).</summary>
    public const int MaxLegs = 4;

    public static string ExplorePrompt(string status, string knowledge, int leg, string? refused) =>
        $"Leg {leg} of up to {MaxLegs}. The plane now:\n{Lines(status, "Altitude", "Fuel", "Pilot")}\n" +
        $"What the server's agents have explored:\n{Section(knowledge, "Explored")}\n" +
        (refused != null ? $"Your last leg was refused: {refused}. Choose a shorter or different one.\n" : "") +
        "Choose the next leg: a bearing from where the plane is (degrees, 0 north, 90 east), a distance (1-30 km) and an " +
        "altitude above sea level (at least 500 m above the ground; hills west of KSC, mountains further). Prefer " +
        "directions and biomes nobody explored yet; stay within 60 km of KSC and keep fuel to come back. Answer exactly:\n" +
        "GO <bearing> <km> <altitude>\nor HOME to come back and land.";

    /// <summary>The planner's leg (bearing, km, altitude), or null for HOME or an answer that isn't one.</summary>
    public static (double Bearing, double Km, double Altitude)? ParseLeg(string answer)
    {
        var m = Go().Match(answer);
        if (!m.Success)
            return null;
        double N(int i) => double.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture);
        var (bearing, km, altitude) = (N(1), N(2), m.Groups[3].Success ? N(3) : 1500);
        return double.IsFinite(bearing) && km is > 0 and <= 100 && altitude is > 0 and < 20_000 ? ((bearing % 360 + 360) % 360, km, altitude) : null;
    }

    [GeneratedRegex(@"\bGO\s+(-?\d+(?:\.\d+)?)\s*°?\s+(\d+(?:\.\d+)?)(?:\s*km)?(?:\s+(\d+(?:\.\d+)?)\s*m?)?", RegexOptions.IgnoreCase)]
    private static partial Regex Go();

    public static bool Home(string answer) => Regex.IsMatch(answer, @"^\s*HOME\b", RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>Riskier finals for Approach variety: what each asks the landing for (the plane's limits fit it).</summary>
    public static readonly IReadOnlyList<(string Name, string What)> Styles =
    [
        ("steep", "a steep final (6° glide slope)"),
        ("shallow", "a shallow final (3°)"),
        ("fast", "a fast final (its fastest final speed, slowing late)"),
        ("slow", "a slow final (its landing speed all the way)"),
        ("long", "a long final (the 12 km straight-in)"),
    ];

    public const string StyleSystem =
        "You plan practice landings for an AI pilot in Kerbal Space Program. You choose a riskier final to try, within " +
        "the plane's limits; the pilot flies it and may go around. Answer briefly.";

    public static string StylePrompt(string status, IReadOnlyList<string> tried) =>
        $"The plane:\n{Lines(status, "Vessel", "Limits", "Setup")}\n" +
        (tried.Count > 0 ? $"Tried this session: {string.Join(", ", tried)}.\n" : "") +
        "Choose the final to try next, one you haven't landed well yet (a plane that slows well can take a fast or steep " +
        "one):\n" + string.Join("\n", Styles.Select(s => $"- {s.Name}: {s.What}")) + "\nAnswer exactly: STYLE <name>";

    /// <summary>The style the planner picked, or the first one not tried yet (then the first).</summary>
    public static string ParseStyle(string answer, IReadOnlyList<string> tried)
    {
        var m = Regex.Match(answer, @"STYLE\s*:?\s*([a-z]+)", RegexOptions.IgnoreCase);
        var picked = m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
        return Styles.Any(s => s.Name == picked) ? picked! : Styles.FirstOrDefault(s => !tried.Contains(s.Name)).Name ?? Styles[0].Name;
    }

    /// <summary>What land gets for a style: the approach (long: the straight-in), a final speed, a glide slope.</summary>
    public static Dictionary<string, object?> LandArguments(string style, string status)
    {
        var limits = Limits(status);
        return style switch
        {
            "steep" => new() { ["approach"] = "visual", ["glideSlope"] = 6.0 },
            "shallow" => new() { ["approach"] = "visual", ["glideSlope"] = 3.0 },
            "fast" => new() { ["approach"] = "visual", ["finalSpeed"] = limits?.Max ?? 130.0 },
            "slow" => new() { ["approach"] = "visual", ["finalSpeed"] = limits?.Min ?? 60.0 },
            _ => new() { ["approach"] = "RNAV 09 STRAIGHT-IN", ["glideSlope"] = 4.0 },
        };
    }

    /// <summary>The plane's final speed limits from status ("Limits for the X: final approach 73-128 m/s, ...").</summary>
    public static (double Min, double Max)? Limits(string status)
    {
        var m = Regex.Match(status, @"final approach (\d+)-(\d+) m/s");
        return m.Success ? (double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)) : null;
    }

    private static string Lines(string text, params string[] starts) => string.Join("\n",
        text.Split('\n').Select(l => l.Trim()).Where(l => starts.Any(s => l.StartsWith(s, StringComparison.Ordinal))));

    /// <summary>A section of the knowledge tool's answer, from its heading line to the next blank or heading line.</summary>
    private static string Section(string text, string heading)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd()).ToList();
        var start = lines.FindIndex(l => l.StartsWith(heading, StringComparison.Ordinal));
        if (start < 0)
            return "nothing yet";
        return string.Join("\n", lines.Skip(start).TakeWhile((l, i) => i == 0 || l.StartsWith(' ') || l.StartsWith('-')).Take(12));
    }
}

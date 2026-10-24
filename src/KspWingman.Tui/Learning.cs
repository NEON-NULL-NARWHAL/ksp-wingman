// KSP Wingman · src/KspWingman.Tui/Learning.cs
// Learning and free flight: the planner (a model that thinks, never touches the controls), the debrief after each
// flight (one lesson, checked, saved on the server with the flight as evidence), and free flight (the planner picks
// what to practise from a menu; code flies it inside a box around KSC).

using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KspWingman.Tui;

/// <summary>A model asked for a decision or a sentence (no tools).</summary>
public interface IPlanner
{
    Task<string> AskAsync(string system, string prompt, CancellationToken cancel);
}

/// <summary>Any chat model (the pilot's, or another) as a planner, through the OpenAI-compatible API.</summary>
public sealed class ChatPlanner(ILlm llm) : IPlanner
{
    public async Task<string> AskAsync(string system, string prompt, CancellationToken cancel)
    {
        var reply = await llm.ChatAsync([new JsonObject { ["role"] = "system", ["content"] = system }, new JsonObject { ["role"] = "user", ["content"] = prompt }], [], cancel);
        return reply.Text;
    }
}

/// <summary>
/// A thinking model through Ollama's own API (/api/chat with think: its OpenAI endpoint ignores every thinking switch).
/// The thinking stays in the model; only the answer comes back.
/// </summary>
public sealed class OllamaThinkingPlanner(HttpClient http, Uri ollama, string model) : IPlanner
{
    public async Task<string> AskAsync(string system, string prompt, CancellationToken cancel)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["stream"] = false,
            ["think"] = true,
            ["options"] = new JsonObject { ["temperature"] = 0.3 },
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = system }, new JsonObject { ["role"] = "user", ["content"] = prompt }),
        };
        using var response = await http.PostAsJsonAsync(new Uri(ollama, "api/chat"), body, cancel);
        var text = await response.Content.ReadAsStringAsync(cancel);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"The planner's server said {(int)response.StatusCode}.");
        return Answer(text);
    }

    internal static string Answer(string json) =>
        JsonNode.Parse(json)?["message"]?["content"] is JsonValue v && v.TryGetValue<string>(out var s) ? OpenAiCompatibleLlm.StripThinking(s) : "";

    /// <summary>Ollama's own address from its OpenAI one (http://host:11434/v1/ → http://host:11434/).</summary>
    public static Uri? OllamaBase(Uri openAi) =>
        openAi.AbsolutePath.TrimEnd('/').EndsWith("/v1", StringComparison.Ordinal) ? new Uri(openAi, "../") : null;
}

/// <summary>The debrief: a finished flight's record in, one lesson (skill and sentence) out, or none.</summary>
public static partial class Debrief
{
    public const string System =
        "You are the debrief officer for AI pilots in Kerbal Space Program. You read a flight's record and write what a " +
        "pilot should do differently next time. Facts only, from the record's numbers; never invent. Keep it short.";

    public static string Prompt(string outcome, IReadOnlyDictionary<string, bool> skills, string knowledge) =>
        $"A flight just ended. Its record: {outcome}\n" +
        "Grades: " + string.Join(", ", skills.Select(s => $"{s.Key} {(s.Value ? "good" : "not good yet")}")) +
        " (a good landing stops within 15 m of the centerline with no go-around).\n" +
        $"What the server already knows:\n{knowledge}\n\n" +
        "Write ONE new lesson for next time, for the skill that went worst: what to do differently, as advice starting " +
        "with a verb (e.g. \"Slow to 85 m/s before F09 to stop closer to the centerline.\"), 10 to 200 characters, with " +
        "numbers from the record when you can. Don't just repeat what happened, and don't repeat a lesson that's already " +
        "there. SKILL is one of these ids, exactly: " + string.Join(", ", skills.Keys) + ". Answer exactly:\n" +
        "SKILL: <skill id> | LESSON: <sentence>\nIf the flight taught nothing new, answer NONE.";

    /// <summary>The planner's answer as (skill, lesson), when it's one of the flight's skills and a sane sentence.</summary>
    public static (string Skill, string Lesson)? Parse(string answer, IReadOnlyCollection<string> skills)
    {
        var m = Line().Match(answer);
        if (!m.Success)
            return null;
        var skill = Skill(m.Groups[1].Value, skills);
        var lesson = m.Groups[2].Value.Trim().Trim('"');
        if (skill == null || lesson.Length is < 10 or > 200 || lesson.Contains('\n'))
            return null;
        // Advice, not a report: the rig's first "lesson" was "Landed 25m off centerline at 1021m down runway 09".
        var first = lesson.Split(' ', 2)[0].Trim(',', '.', ':').ToLowerInvariant();
        if (first is "landed" or "stopped" or "flight" or "flew" or "it" or "i" or "we" or "this" or "landing" or "touched")
            return null;
        return (skill, lesson);
    }

    /// <summary>
    /// The flight's skill a model meant: its id, or a name close to one ("Landing", "RNAV Landing", "landing-visual":
    /// how qwen3:8b and gemma4:e4b wrote them in PlannerEval), when exactly one of the flight's skills fits.
    /// </summary>
    internal static string? Skill(string written, IReadOnlyCollection<string> skills)
    {
        var w = written.Trim().ToLowerInvariant().Replace(' ', '-');
        if (skills.Contains(w))
            return w;
        bool Fits(string id) =>
            (w.Contains("visual") && id == "land-visual") || (w.Contains("rnav") && id == "land-rnav") ||
            (w.StartsWith("land", StringComparison.Ordinal) && !w.Contains("visual") && !w.Contains("rnav") && id.StartsWith("land-", StringComparison.Ordinal)) ||
            (w.Contains("take") && id == "takeoff") || (w.Contains("hold") && id == "hold") || (w.Contains("sequenc") && id == "sequencing") ||
            (w.Contains("route") && id == "route");
        var fits = skills.Where(Fits).ToList();
        return fits.Count == 1 ? fits[0] : null;
    }

    // The "|" is optional: Qwen3 4B left it out in the rig ("SKILL: land-visual   LESSON: Correct heading 2°...").
    [GeneratedRegex(@"SKILL:\s*([a-z][a-z \-]*?)\s*[|,;]?\s*LESSON:\s*([^\r\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex Line();
}

/// <summary>One thing free flight can practise, and the land call that does it.</summary>
public sealed record Goal(string Skill, string Text, string Approach);

/// <summary>Free flight's limits, enforced by code (not by the prompt).</summary>
public sealed record FreeFlightLimits(double RadiusKm = 25, double CeilingM = 3000, int MaxCrashesInARow = 2);

public static partial class FreeFlight
{
    /// <summary>What it can practise on its own around KSC (holding and sequencing need other traffic).</summary>
    public static readonly IReadOnlyList<Goal> Goals =
    [
        new("land-rnav", "land on RNAV 09 NORTH", "RNAV 09 NORTH"),
        new("land-rnav", "land on RNAV 09 SOUTH", "RNAV 09 SOUTH"),
        new("land-rnav", "land on RNAV 09 STRAIGHT-IN", "RNAV 09 STRAIGHT-IN"),
        new("land-visual", "land on the short visual", "visual"),
    ];

    /// <summary>A skill's numbers from the knowledge tool's menu ("1. land-rnav (Land, RNAV): 2 of 5 / 3 of 9").</summary>
    public static Dictionary<string, (int Good, int Attempts)> Proficiency(string knowledge)
    {
        var result = new Dictionary<string, (int, int)>();
        foreach (Match m in SkillLine().Matches(knowledge))
            result[m.Groups[1].Value] = (int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
        return result;
    }

    [GeneratedRegex(@"^\d+\. ([a-z\-]+) \([^)]*\): (\d+) of (\d+)", RegexOptions.Multiline)]
    private static partial Regex SkillLine();

    /// <summary>
    /// The menu for the planner: each goal with its skill's numbers. A goal failed twice in a row without a lesson in
    /// between is left out (practise something else first).
    /// </summary>
    public static IReadOnlyList<Goal> Menu(IReadOnlyList<(Goal Goal, bool Good, bool Lesson)> history) =>
        Goals.Where(g =>
        {
            var last = history.Where(h => h.Goal == g).TakeLast(2).ToList();
            return !(last.Count == 2 && last.All(h => !h.Good) && !last[^1].Lesson);
        }).ToList();

    public static string Prompt(IReadOnlyList<Goal> menu, IReadOnlyDictionary<string, (int Good, int Attempts)> proficiency, double minutesLeft) =>
        $"Free flight: you choose what to practise next ({minutesLeft:0} minutes left). Prefer the skills you're weakest at " +
        "(few good flights for their attempts), and try each approach now and then.\n" +
        string.Join("\n", menu.Select((g, i) =>
        {
            var p = proficiency.TryGetValue(g.Skill, out var x) ? x : (0, 0);
            return $"{i + 1}. {g.Text} ({g.Skill}: {p.Item1} of {p.Item2} good)";
        })) +
        "\nAnswer with the number and a short reason, e.g. \"2 - fewest good landings\".";

    /// <summary>The planner's pick, or the weakest goal when it didn't answer with a number on the menu.</summary>
    public static (Goal Goal, string Why) Choose(string answer, IReadOnlyList<Goal> menu, IReadOnlyDictionary<string, (int Good, int Attempts)> proficiency)
    {
        var m = Number().Match(answer);
        if (m.Success && int.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= menu.Count)
        {
            var why = answer[(m.Index + m.Length)..].Trim(' ', '-', ':', '.', '\n', '\r');
            return (menu[n - 1], why.Length > 0 ? why[..Math.Min(why.Length, 160)] : "its pick");
        }
        var weakest = menu.OrderBy(g => proficiency.TryGetValue(g.Skill, out var p) && p.Attempts > 0 ? (double)p.Good / p.Attempts : 0)
            .ThenBy(g => proficiency.TryGetValue(g.Skill, out var p) ? p.Attempts : 0).First();
        return (weakest, "the weakest skill (the planner's answer had no number on the menu)");
    }

    [GeneratedRegex(@"\b(\d{1,2})\b")]
    private static partial Regex Number();

    /// <summary>Where the plane is from status: (km from KSC's runway, meters up), or null in no flight.</summary>
    public static (double Km, double Altitude)? Where(string status)
    {
        var at = Position().Match(status);
        var up = Altitude().Match(status);
        if (!at.Success || !up.Success)
            return null;
        var lat = double.Parse(at.Groups[1].Value, CultureInfo.InvariantCulture);
        var lon = double.Parse(at.Groups[2].Value, CultureInfo.InvariantCulture);
        const double metersPerDegree = 600_000 * Math.PI / 180;
        var north = (lat - -0.0486) * metersPerDegree;
        var east = (lon - -74.7244) * metersPerDegree;
        return (Math.Sqrt(north * north + east * east) / 1000, double.Parse(up.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    [GeneratedRegex(@"lat (-?[\d.]+) lon (-?[\d.]+)")]
    private static partial Regex Position();

    [GeneratedRegex(@"Altitude (-?\d+) m")]
    private static partial Regex Altitude();

    /// <summary>The craft to launch again, from status ("Vessel: Aeris 3A (Landed) at Kerbin").</summary>
    public static string? Craft(string status) => CraftLine().Match(status) is { Success: true } m ? m.Groups[1].Value.Trim() : null;

    [GeneratedRegex(@"Vessel: (.+?) \((?:Landed|Flying|PreLaunch|Splashed|SubOrbital)\)")]
    private static partial Regex CraftLine();
}

/// <summary>The MCP server's events as the console sees them, for whoever waits on one (free flight, the debrief).</summary>
public sealed class EventHub
{
    private readonly List<(long Id, string Kind, string Text, JsonNode? Data)> _events = [];
    private long _id;

    public void Add(string kind, string text, JsonNode? data)
    {
        lock (_events)
        {
            _events.Add((++_id, kind, text, data?.DeepClone()));
            if (_events.Count > 500)
                _events.RemoveAt(0);
        }
    }

    public long Last { get { lock (_events) return _id; } }

    public (long Id, string Kind, string Text, JsonNode? Data)? After(long id, string kind)
    {
        lock (_events)
            return _events.FirstOrDefault(e => e.Id > id && e.Kind == kind) is { Kind: not null } e ? e : null;
    }
}

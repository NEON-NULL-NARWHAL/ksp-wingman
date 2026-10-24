// KSP Wingman · tests/PlannerEval/Program.cs
// Development tool: which model plans best, without KSP. Gives each planner model real flight records (the rig's, or a
// knowledge.json from LUNA EVO's tower) as debriefs, and free-flight menus as goal choices, through the console's own
// code (Debrief, FreeFlight, the Ollama thinking planner), and scores what the console would accept, and how fast.
// Usage: PlannerEval --models qwen3:4b,qwen3:8b,gemma4:e4b [--llm http://127.0.0.1:11434/v1/] [--runs 1]
//                    [--flights knowledge.json] [--report report.md] [--no-think]
// New kinds of planning (routes, missions) get a scenario list here first: bigger models can be tried on them long
// before they fly.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using KspWingman.Tui;

string? Get(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
var models = (Get("--models") ?? "qwen3:4b").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var llm = new Uri(Get("--llm") ?? "http://127.0.0.1:11434/v1/");
var runs = int.Parse(Get("--runs") ?? "1", CultureInfo.InvariantCulture);
var think = !args.Contains("--no-think");
var flights = Flights(Get("--flights") ?? Path.Combine(AppContext.BaseDirectory, "flights.json"));
// The tower's published table (LUNA EVO's /v1/procedures), for flight plans the planner builds itself.
var table = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Get("--procedures") ?? Path.Combine(AppContext.BaseDirectory, "procedures.json")))!;
var tableText = FlightPlan.Describe(table);
(string Where, double Along, double Cross)[] starts =
[
    ("just after takeoff, south side", 4000, 2000), ("just after takeoff, north side", 4000, -2500), ("far out west", -15_000, 1000),
    ("far south", 1000, 9000), ("in the overhead hold", 900, 0),
];
using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

var report = new StringBuilder($"# Planner eval ({DateTime.Now:yyyy-MM-dd HH:mm})\n\n{flights.Count} debriefs and {Menus.Count} goal choices per run, {runs} run(s), thinking {(think ? "on" : "off")}.\n\n" +
    "| Model | Lessons accepted | Goal picks valid | Picked the weakest | Plans valid | Fixes a plan | Mean s | Slowest s |\n|---|---|---|---|---|---|---|---|\n");
var lessons = new StringBuilder();
foreach (var model in models)
{
    IPlanner planner = think && OllamaThinkingPlanner.OllamaBase(llm) is { } ollama
        ? new OllamaThinkingPlanner(http, ollama, model)
        : new ChatPlanner(new OpenAiCompatibleLlm(http, llm, model, null));
    await planner.AskAsync("Answer OK.", "OK?", CancellationToken.None); // loaded before timing
    var times = new List<double>();
    int accepted = 0, valid = 0, weakest = 0, debriefs = 0, goals = 0, plans = 0, goodPlans = 0, planFixes = 0;
    lessons.AppendLine($"\n## {model}\n");
    for (var run = 0; run < runs; run++)
    {
        foreach (var f in flights)
        {
            var (answer, seconds) = await Timed(planner, Debrief.System, Debrief.Prompt(f.Record, f.Skills, "(nothing yet)"));
            times.Add(seconds);
            debriefs++;
            var lesson = Debrief.Parse(answer, f.Skills.Keys);
            accepted += lesson != null ? 1 : 0;
            lessons.AppendLine($"- {(lesson != null ? $"**{lesson.Value.Skill}**: {lesson.Value.Lesson}" : $"refused: `{OneLine(answer)}`")} ({seconds:0} s)");
        }
        foreach (var (menu, proficiency, weakestGoal) in Menus)
        {
            var (answer, seconds) = await Timed(planner, "You plan practice flights for an AI pilot. Answer briefly.", FreeFlight.Prompt(menu, proficiency, 30));
            times.Add(seconds);
            goals++;
            var picked = System.Text.RegularExpressions.Regex.Match(answer, @"\b(\d{1,2})\b") is { Success: true } m && int.Parse(m.Value, CultureInfo.InvariantCulture) is var n && n >= 1 && n <= menu.Count;
            valid += picked ? 1 : 0;
            weakest += picked && FreeFlight.Choose(answer, menu, proficiency).Goal.Skill == weakestGoal ? 1 : 0;
        }
        foreach (var (where, along, cross) in starts)
        {
            var (answer, seconds) = await Timed(planner, FlightPlan.System, FlightPlan.Prompt(tableText, $"{where}: {FlightPlan.Where(along, cross)}"));
            times.Add(seconds);
            plans++;
            var plan = FlightPlan.Parse(answer);
            var problem = plan.Count == 0 ? "no PLAN line" : FlightPlan.Check(table, plan, along, cross);
            goodPlans += problem == null ? 1 : 0;
            planFixes += problem == null ? plan.Count : 0;
            lessons.AppendLine($"- plan from {where}: {(problem == null ? string.Join(" ", plan) : $"refused ({problem}): `{OneLine(answer)}`")} ({seconds:0} s)");
        }
    }
    var line = string.Create(CultureInfo.InvariantCulture,
        $"| {model} | {accepted}/{debriefs} | {valid}/{goals} | {weakest}/{goals} | {goodPlans}/{plans} | {(goodPlans > 0 ? (double)planFixes / goodPlans : 0):0.0} | {times.Average():0.0} | {times.Max():0} |");
    Console.WriteLine(line);
    report.AppendLine(line);
}
report.AppendLine("\nThe lessons each model wrote (a good one is advice with numbers, for the skill that went worst):").Append(lessons);
if (Get("--report") is { } path)
{
    File.WriteAllText(path, report.ToString());
    Console.WriteLine($"Report: {path}");
}
else
    Console.WriteLine(lessons);
return 0;

static async Task<(string Answer, double Seconds)> Timed(IPlanner planner, string system, string prompt)
{
    var clock = Stopwatch.StartNew();
    try
    {
        return (await planner.AskAsync(system, prompt, CancellationToken.None), clock.Elapsed.TotalSeconds);
    }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
    {
        return ("(error: " + e.Message + ")", clock.Elapsed.TotalSeconds);
    }
}

static string OneLine(string text)
{
    var line = string.Join(" ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
    return line.Length <= 120 ? line : line[..120] + "…";
}

// Flight records as debriefs: LUNA EVO's knowledge.json (flights with skills) or this tool's own flights.json (the same shape).
static List<(string Record, Dictionary<string, bool> Skills)> Flights(string path)
{
    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    var list = new List<(string, Dictionary<string, bool>)>();
    foreach (var f in doc.RootElement.GetProperty("flights").EnumerateArray())
    {
        double N(string name) => f.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        var record = FormattableString.Invariant(
            $"Flight {f.GetProperty("result").GetString()} after {N("minutes"):0.0} min, {f.GetProperty("approach").GetString()}") +
            (N("goArounds") > 0 ? FormattableString.Invariant($", {N("goArounds"):0} go-around(s)") : "") +
            FormattableString.Invariant($": stopped {N("downRunway"):0} m down KSC runway 09, {N("offCenterline"):0} m off the centerline, {N("headingError"):0}° off the heading.");
        var skills = f.GetProperty("skills").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetBoolean());
        list.Add((record, skills));
    }
    return list;
}

// Goal menus with a clear weakest choice: how well each skill went so far, and the goal a good planner should pick.
static partial class Program
{
    internal static readonly List<(IReadOnlyList<Goal> Menu, Dictionary<string, (int Good, int Attempts)> Proficiency, string Weakest)> Menus =
    [
        (FreeFlight.Goals, new() { ["land-rnav"] = (2, 3), ["land-visual"] = (0, 3) }, "land-visual"),
        (FreeFlight.Goals, new() { ["land-rnav"] = (0, 4), ["land-visual"] = (3, 3) }, "land-rnav"),
        (FreeFlight.Goals, new() { ["land-rnav"] = (5, 5), ["land-visual"] = (1, 4) }, "land-visual"),
    ];
}

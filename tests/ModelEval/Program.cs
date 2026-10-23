// KSP Wingman · tests/ModelEval/Program.cs
// Development tool: gives a model real orders with KSP Wingman's real tool list (from KspWingman.Mcp) and scripted
// game answers, and scores whether it calls the right tools with sensible arguments. No KSP needed.
// Usage: ModelEval <KspWingman.Mcp.exe> <model> [OpenAI-compatible URL, default Ollama] [runs per scenario, default 1]

using System.Diagnostics;
using System.Globalization;
using KspWingman.Tui;

var mcp = args[0];
var model = args[1];
var url = new Uri(args.Length > 2 ? args[2] : "http://127.0.0.1:11434/v1/");
var runs = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 1;

await using var real = await McpTools.StartAsync(mcp, "127.0.0.1", 1, CancellationToken.None); // tool list only: no kRPC needed
using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
var llm = new OpenAiCompatibleLlm(http, url, model, null);

const string OnRunway = "Player: Wingman · in a server\nScene: Flight\nVessel: Aeris 3A (PreLaunch) at Kerbin\nAltitude 71 m (above ground 2 m) · speed 0.0 m/s · heading 90° · pitch 0°\nPilot: Not flying anything.";
const string Flying = "Player: Wingman · in a server\nScene: Flight\nVessel: Aeris 3A (Flying) at Kerbin\nAltitude 1500 m (above ground 1500 m) · speed 120.0 m/s · heading 90° · pitch 2°\nPilot: Holding heading 90°, 1500 m, 120 m/s.";
const string Crashed = "Player: Wingman · in a server\nScene: Flight\nVessel: Aeris 3A, destroyed (it crashed). Launch a new one.\nPilot: Stopped: the vessel was destroyed (it crashed).";

double Num(ToolCall c, string key) => c.Arguments.TryGetValue(key, out var v) && v is IConvertible x ? Convert.ToDouble(x, CultureInfo.InvariantCulture) : double.NaN;
bool Near(double value, double target, double tolerance) => Math.Abs(value - target) <= tolerance;

var scenarios = new (string Name, string State, string From, string Order, Func<IReadOnlyList<ToolCall>, string, string?> Check)[]
{
    ("where am I", OnRunway, "Ann", "where are you?", (calls, answer) =>
        calls.Any(c => c.Name == "status") ? (answer.Contains("runway", StringComparison.OrdinalIgnoreCase) || answer.Contains("ground", StringComparison.OrdinalIgnoreCase) || answer.Contains("71", StringComparison.Ordinal) || answer.Contains("ready", StringComparison.OrdinalIgnoreCase) ? null : "the answer doesn't say where") : "no status call"),
    ("take off, fly east", OnRunway, "Ann", "take off and fly east at 1000 meters", (calls, _) =>
        calls.LastOrDefault(c => c.Name == "take_off") is not { } t ? "no take_off" :
        calls.LastOrDefault(c => c.Name == "fly") is { } f
            ? (Near(Num(f, "heading"), 90, 5) && Near(Num(f, "altitude"), 1000, 100) ? null : $"fly({f.RawArguments})")
            : Near(double.IsNaN(Num(t, "heading")) ? 90 : Num(t, "heading"), 90, 5) && Near(Num(t, "altitude"), 1000, 100) ? null : $"take_off({t.RawArguments}) and no fly"),
    ("land", Flying, "Ann", "come back and land at KSC", (calls, _) =>
        calls.Any(c => c.Name == "land") ? null : "no land"),
    ("land north approach", Flying, "Ann", "land at KSC using the north approach", (calls, _) =>
        calls.LastOrDefault(c => c.Name == "land") is { } l
            ? (l.Arguments.TryGetValue("approach", out var a) && a is string s && s.Contains("NORTH", StringComparison.OrdinalIgnoreCase) ? null : $"land({l.RawArguments})")
            : "no land"),
    ("follow me", Flying, "Ann", "follow me", (calls, _) =>
        calls.LastOrDefault(c => c.Name == "follow") is { } f ? (f.Arguments.TryGetValue("player", out var p) && "Ann".Equals(p as string, StringComparison.OrdinalIgnoreCase) ? null : $"follow({f.RawArguments})") : "no follow"),
    ("climb north", Flying, "Bob", "turn north and climb to 3000 m", (calls, _) =>
        calls.LastOrDefault(c => c.Name == "fly") is { } f ? (Near(Pilot(Num(f, "heading")), 0, 5) && Near(Num(f, "altitude"), 3000, 150) ? null : $"fly({f.RawArguments})") : "no fly"),
    ("new plane", Crashed, "Ann", "you crashed, get a new plane in the air", (calls, _) =>
        !calls.Any(c => c.Name == "launch") ? "no launch" : !calls.Any(c => c.Name == "take_off") ? "no take_off after launch" : null),
    ("can't warp", Flying, "Bob", "time warp to the Mun", (calls, answer) =>
        calls.Any(c => c.Name is "fly" or "launch" or "stage") ? "flew instead of saying it can't" : null),
};

static double Pilot(double heading) => heading > 180 ? heading - 360 : heading; // 360 is north too

var passed = 0;
var total = 0;
var watch = Stopwatch.StartNew();
foreach (var s in scenarios)
    for (var run = 0; run < runs; run++)
    {
        var tools = new ScriptedTools(real.Tools, s.State);
        var agent = new Agent(llm, tools, Agent.Prompt("Wingman", noThink: true));
        if (Environment.GetEnvironmentVariable("EVAL_VERBOSE") == "1")
            agent.Event += e => Console.WriteLine($"       {e.Kind}: {Ui.ForChat(e.Text)}");
        var started = watch.Elapsed;
        string answer;
        try
        {
            answer = await agent.RunAsync(s.From, s.Order, CancellationToken.None);
        }
        catch (Exception e)
        {
            answer = "(error) " + e.Message;
        }
        var problem = s.Check(tools.Calls, answer);
        total++;
        if (problem == null)
            passed++;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{(problem == null ? "PASS" : "FAIL")} {s.Name,-20} {(watch.Elapsed - started).TotalSeconds,5:0.0} s  calls: {string.Join(" ", tools.Calls.Select(c => $"{c.Name}{c.RawArguments}"))}"));
        Console.WriteLine($"     answer: {Ui.ForChat(answer)}{(problem == null ? "" : $"\n     why: {problem}")}");
    }
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{model}: {passed}/{total} in {watch.Elapsed.TotalSeconds:0} s"));

/// <summary>The real tool list, with answers from a script instead of KSP.</summary>
internal sealed class ScriptedTools(IReadOnlyList<ToolSpec> tools, string state) : IToolHost
{
    public List<ToolCall> Calls { get; } = [];
    public IReadOnlyList<ToolSpec> Tools => tools;

    public Task<string> CallAsync(string name, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancel)
    {
        Calls.Add(new ToolCall("", name, arguments, System.Text.Json.JsonSerializer.Serialize(arguments)));
        return Task.FromResult(name switch
        {
            "status" => state,
            "players" => "Ann|Aeris 3A|Flying above Kerbin\nBob|Kerbal X|Landed at KSC",
            "launchable_craft" => "SPH (runway): Aeris 3A, Stearwing A300\nVAB (launch pad): Kerbal X",
            "launch" => "Launching Aeris 3A on the runway. It takes a few seconds; then status shows it.",
            "take_off" => "Airborne: 101 m above the ground at 130 m/s on heading 90°, gear up. Now flying on: heading 90°, " +
                $"{(arguments.TryGetValue("altitude", out var a) && a != null ? a : 570)} m, 150 m/s. Use fly, follow or land to change that.",
            "fly" => "Holding the new heading, altitude and speed.",
            "follow" => "Following them: looking for them.",
            "land" => "KSC tower: Wingman, cleared RNAV 09 NORTH. Proceed to KSC-N1 at 900 m, 110 m/s, then KSC-N2, KSC-N3, F09, land KSC runway 09.",
            "approaches" => "RNAV 09 STRAIGHT-IN (from the west): KSC-W12 900 m 120 m/s → F09 628 m 90 m/s → land KSC runway 09.\n" +
                "RNAV 09 NORTH (from the north): KSC-N1 → KSC-N2 → KSC-N3 → F09.\nRNAV 09 SOUTH (from the south): KSC-S1 → KSC-S2 → KSC-S3 → F09.",
            "stop_flying" => "Not flying it any more: throttle zero, SAS on.",
            _ => "Done.",
        });
    }
}

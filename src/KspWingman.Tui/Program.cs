// KSP Wingman · src/KspWingman.Tui/Program.cs
// The Wingman console. Orders come from this terminal (you are always in charge) or from LMP's chat (players on the
// allow list, starting with the call sign); a model flies the agent's KSP and its answer goes back the same way.
//
//   KspWingman --model qwen3:4b [--llm http://127.0.0.1:11434/v1/] [--krpc-port 50000] [--allow "Ann,Bob"]
//              [--call-sign Wingman] [--mcp path\to\KspWingman.Mcp.exe] [--api-key ...] [--think] [--ipc]
//              [--planner-model qwen3:4b] [--planner-llm <URL>]
// --ipc: JSON lines in and out, for a host app (Mission Control's Wingman tab); see Ipc.cs.
// The planner (Learning.cs) picks free-flight goals and writes debriefs; it never flies. Without --planner-model the
// pilot's model does it. /free [minutes] starts free flight; any order stops it.
// Guide:   Meat and Potatoes/06-Console-And-Learning.md

using System.Globalization;
using System.Threading.Channels;
using KspWingman.Tui;

Console.OutputEncoding = System.Text.Encoding.UTF8; // degree signs, the models' emoji
var options = Options.Parse(args);
if (options.Problem is { } problem)
{
    Console.Error.WriteLine(problem);
    Console.Error.WriteLine(Options.Usage);
    return 2;
}

Ui.Ipc = options.Ipc;
using var quit = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Cancel(); };
Ui.Line("wingman", $"KSP Wingman console. Model {options.Model} at {options.LlmUrl}; agent's KSP on kRPC port {options.KrpcPort}.");

await using var tools = await McpTools.StartAsync(options.McpExe, options.KrpcHost, options.KrpcPort, quit.Token);
using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) }; // a small model on a busy GPU can take a while
var orders = Channel.CreateUnbounded<(string From, string Text, bool FromChat)>();
// The pilot model gets one tool of the console's own: train (it queues a training run after the order).
var withTrain = new TrainTool(tools, (focus, minutes) => orders.Writer.TryWrite(("(train)", $"/train {focus} {minutes.ToString(CultureInfo.InvariantCulture)}", false)));
var agent = new Agent(new OpenAiCompatibleLlm(http, options.LlmUrl, options.Model, options.ApiKey), withTrain,
    Agent.Prompt(options.CallSign, !options.Think));
agent.Event += e => Ui.Line(e.Kind, e.Text);
IPlanner planner = options.PlannerModel is { } plannerModel
    ? OllamaThinkingPlanner.OllamaBase(options.PlannerUrl) is { } ollama
        ? new OllamaThinkingPlanner(http, ollama, plannerModel)
        : new ChatPlanner(new OpenAiCompatibleLlm(http, options.PlannerUrl, plannerModel, options.ApiKey))
    : new ChatPlanner(new OpenAiCompatibleLlm(http, options.LlmUrl, options.Model, options.ApiKey));
var hub = new EventHub();
var learner = new Learner(tools, planner, hub, (kind, text) => Ui.Line(kind, text));
var debriefs = new System.Collections.Concurrent.ConcurrentQueue<(System.Text.Json.Nodes.JsonNode? Data, string Outcome)>();
var freeFlying = false;
var stopFree = false;

var first = Ui.FirstLine(await tools.CallAsync("status", new Dictionary<string, object?>(), quit.Token));
if (!first.StartsWith("Player: ", StringComparison.Ordinal))
{
    Ui.Line("error", first);
    Ui.Line("wingman", "Start the agent's KSP (kRPC's server running, in a game), then start this again.");
    return 1;
}
var me = first["Player: ".Length..].Split(" · ")[0];
Ui.Line("wingman", $"Playing as {me}. {tools.Tools.Count} tools. Chat orders: " +
    (options.Allowed.Count == 0 ? "off (no --allow)." : $"\"{options.CallSign}, ...\" from {string.Join(", ", options.Allowed)}."));
Ui.Line("wingman", "Type an order (\"take off and fly east at 1000 m\"), /status, /free [minutes], /forget or /quit." +
    (options.PlannerModel != null ? $" Planner: {options.PlannerModel}." : ""));

_ = Task.Run(async () =>
{
    while (!quit.IsCancellationRequested && Console.ReadLine() is { } typed)
    {
        var input = options.Ipc ? Ipc.Read(typed) : typed.Trim().Length > 0 ? ("Commander", typed.Trim()) : null;
        if (input is not { } line)
            continue;
        // Answered here, not queued: a host app asks for status every few seconds, and that isn't an order (it would
        // stop free flight).
        if (line.Text.Equals("/status", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Ui.Line(options.Ipc ? "status" : "result", await tools.CallAsync("status", new Dictionary<string, object?>(), quit.Token));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Ui.Line("error", e.Message);
            }
            continue;
        }
        if (line.Text.Equals("/stop-free", StringComparison.OrdinalIgnoreCase))
        {
            stopFree = true;
            Ui.Line("free", freeFlying ? "Stopping free flight after this step." : "Free flight isn't on.");
            continue;
        }
        await orders.Writer.WriteAsync((line.Item1, line.Item2, false));
    }
    quit.Cancel();
});
_ = Task.Run(() => ForwardEvents(tools, hub, (data, outcome) =>
{
    // A flight the knowledge store recorded: debriefed between orders (free flight debriefs its own).
    if (!freeFlying)
    {
        debriefs.Enqueue((data, outcome));
        orders.Writer.TryWrite(("(debrief)", "/debrief", false));
    }
}, quit.Token));
if (options.Allowed.Count > 0)
    _ = Task.Run(() => ListenToChat(tools, options, me, orders.Writer, quit.Token));

var quitWarned = false;
try
{
    await foreach (var (from, text, fromChat) in orders.Reader.ReadAllAsync(quit.Token))
    {
        if (text == "/quit!")
            break;
        if (text.Equals("/quit", StringComparison.OrdinalIgnoreCase))
        {
            // Quitting stops the pilot (the MCP server goes with the console), and SAS alone held a landing plane in its
            // descent into the hills in the rig: ask once while it's flying.
            var pilot = (await tools.CallAsync("status", new Dictionary<string, object?>(), quit.Token))
                .Split('\n').FirstOrDefault(l => l.StartsWith("Pilot: ", StringComparison.Ordinal)) ?? "";
            if (!quitWarned && pilot.Length > 0 && !pilot.Contains("Not flying anything") && !pilot.Contains("Stopped") && !pilot.Contains("Landed"))
            {
                quitWarned = true;
                Ui.Line("error", $"Still flying ({pilot[7..]}). Quitting leaves the plane on SAS with nobody flying it. Type /quit again to leave anyway, or land first.");
                continue;
            }
            break;
        }
        quitWarned = false;
        if (text.Equals("/forget", StringComparison.OrdinalIgnoreCase))
        {
            agent.Forget();
            Ui.Line("wingman", "Forgot the conversation.");
            continue;
        }
        if (text == "/debrief" && from == "(debrief)")
        {
            if (debriefs.TryDequeue(out var flight))
                await learner.DebriefAsync(flight.Data, flight.Outcome, quit.Token);
            continue;
        }
        var training = text.StartsWith("/train", StringComparison.OrdinalIgnoreCase);
        if (training || (text.StartsWith("/free", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("/free-", StringComparison.OrdinalIgnoreCase)))
        {
            // "/free [minutes]", or "/train <what> [minutes]" (fly fast, fly high, landings, planes, plans, or a skill).
            var words = text[(training ? 6 : 5)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            var minutes = words.Count > 0 && double.TryParse(words[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? m : 30;
            if (words.Count > 0 && double.TryParse(words[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                words.RemoveAt(words.Count - 1);
            var focus = training ? FreeFlight.Focus(string.Join(' ', words)) : null;
            if (training && focus == null)
            {
                Ui.Line("free", "Train what? fast, high, landings, visual, rnav, planes or plans.");
                continue;
            }
            freeFlying = true;
            stopFree = false;
            Ui.Line("free", focus != null
                ? FormattableString.Invariant($"Free flight for {Math.Clamp(minutes, 5, 240):0} minutes: training {string.Join(", ", focus)} until it's learned ({FreeFlight.LearnedInARow} good in a row). Any order stops it.")
                : FormattableString.Invariant($"Free flight for {Math.Clamp(minutes, 5, 240):0} minutes: practising landings around KSC. Any order stops it."));
            string done;
            try
            {
                done = await learner.FreeFlyAsync(minutes, new FreeFlightLimits(), () => stopFree || orders.Reader.Count > 0, quit.Token, focus);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                done = "Free flight stopped: " + e.Message;
            }
            freeFlying = false;
            Ui.Line("free", done);
            continue;
        }
        Ui.Line(fromChat ? "chat" : "order", $"{from}: {text}");
        string answer;
        try
        {
            answer = await agent.RunAsync(from, text, quit.Token);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            answer = "Sorry, my model isn't answering right now.";
            Ui.Line("error", e.Message);
        }
        if (fromChat)
            await tools.CallAsync("send_chat", new Dictionary<string, object?> { ["text"] = Ui.ForChat(answer) }, quit.Token);
    }
}
catch (OperationCanceledException)
{
}
Ui.Line("wingman", "Bye. The plane keeps doing what it was last told; stop_flying hands it back.");
return 0;

// The MCP server's own events (phases, flight outcomes, the tower's messages), every 2 s: shown, or passed on in --ipc;
// kept in the hub for free flight; a flight the knowledge store recorded goes to the debrief.
static async Task ForwardEvents(McpTools tools, EventHub hub, Action<System.Text.Json.Nodes.JsonNode?, string> recorded, CancellationToken cancel)
{
    long last = 0;
    var lastOutcome = "";
    while (!cancel.IsCancellationRequested)
    {
        try
        {
            var text = await tools.CallAsync("events", new Dictionary<string, object?> { ["afterId"] = last }, cancel);
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (System.Text.Json.Nodes.JsonNode.Parse(line) is not System.Text.Json.Nodes.JsonObject e ||
                    e["id"] is not System.Text.Json.Nodes.JsonValue id || !id.TryGetValue<long>(out var n))
                    continue;
                last = Math.Max(last, n);
                var kind = e["kind"]?.GetValue<string>() ?? "event";
                var said = e["text"]?.GetValue<string>() ?? "";
                Ui.Line(kind == "outcome" ? "flight" : kind, said, e["data"]);
                hub.Add(kind, said, e["data"]);
                if (kind == "outcome")
                    lastOutcome = said + (e["data"]?["craft"] is System.Text.Json.Nodes.JsonValue c && c.TryGetValue<string>(out var craft) ? $" (in the {craft})" : "");
                else if (kind == "knowledge")
                    recorded(e["data"], lastOutcome);
            }
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or IOException or HttpRequestException)
        {
            // The MCP server busy or an old one without events: try again later.
        }
        await Task.Delay(2000, cancel).ContinueWith(_ => { });
    }
}

static async Task ListenToChat(McpTools tools, Options options, string me, ChannelWriter<(string, string, bool)> orders, CancellationToken cancel)
{
    // Start after what's already in the chat: old lines aren't orders.
    var last = ChatLine.ParseAll(await tools.CallAsync("read_chat", new Dictionary<string, object?> { ["afterId"] = 0 }, cancel))
        .Select(l => l.Id).DefaultIfEmpty(0).Max();
    while (!cancel.IsCancellationRequested)
    {
        try
        {
            var text = await tools.CallAsync("read_chat", new Dictionary<string, object?> { ["afterId"] = last }, cancel);
            foreach (var line in ChatLine.ParseAll(text))
            {
                last = Math.Max(last, line.Id);
                if (Orders.From(line, options.CallSign, me, options.Allowed) is { } order)
                    await orders.WriteAsync((line.From, order, true), cancel);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Ui.Line("error", "Reading chat: " + e.Message);
        }
        await Task.Delay(2000, cancel);
    }
}

internal sealed record Options(Uri LlmUrl, string Model, string? ApiKey, string KrpcHost, int KrpcPort, string McpExe,
    string CallSign, IReadOnlyCollection<string> Allowed, bool Think, string? Problem, bool Ipc = false, string? PlannerModel = null, Uri? PlannerLlm = null)
{
    /// <summary>The planner's server: its own, or the pilot's.</summary>
    public Uri PlannerUrl => PlannerLlm ?? LlmUrl;

    public const string Usage =
        "Usage: KspWingman --model <name> [--llm <OpenAI-compatible URL, default Ollama: http://127.0.0.1:11434/v1/>] " +
        "[--api-key <key>] [--krpc-host 127.0.0.1] [--krpc-port 50000] [--mcp <KspWingman.Mcp.exe>] " +
        "[--call-sign Wingman] [--allow \"Player1,Player2\"] [--think] [--ipc] [--planner-model <name>] [--planner-llm <URL>]";

    public static Options Parse(string[] args)
    {
        string? Get(string name, string? env = null)
        {
            var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : env == null ? null : Environment.GetEnvironmentVariable(env);
        }
        var url = Get("--llm", "WINGMAN_LLM_URL") ?? "http://127.0.0.1:11434/v1/";
        if (!url.EndsWith('/'))
            url += "/";
        var model = Get("--model", "WINGMAN_MODEL");
        var port = Get("--krpc-port", "WINGMAN_KRPC_PORT") ?? "50000";
        var mcp = Get("--mcp") ?? Path.Combine(AppContext.BaseDirectory, "KspWingman.Mcp.exe");
        string? problem = null;
        if (model == null)
            problem = "Which model? --model (for Ollama: a name from `ollama list`, e.g. qwen3:4b).";
        else if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            problem = $"Not a URL: {url}";
        else if (!int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            problem = $"Not a port: {port}";
        else if (!File.Exists(mcp))
            problem = $"No MCP server at {mcp}: put KspWingman.Mcp.exe next to this one, or pass --mcp.";
        var allowed = (Get("--allow") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var plannerUrl = Get("--planner-llm", "WINGMAN_PLANNER_URL");
        if (plannerUrl != null && !plannerUrl.EndsWith('/'))
            plannerUrl += "/";
        if (problem == null && plannerUrl != null && !Uri.TryCreate(plannerUrl, UriKind.Absolute, out _))
            problem = $"Not a URL: {plannerUrl}";
        return new Options(problem == null ? new Uri(url) : new Uri("http://invalid/"), model ?? "", Get("--api-key", "WINGMAN_LLM_KEY"),
            Get("--krpc-host", "WINGMAN_KRPC_HOST") ?? "127.0.0.1", problem == null ? int.Parse(port, CultureInfo.InvariantCulture) : 0,
            mcp, Get("--call-sign") ?? "Wingman", allowed, args.Any(a => a.Equals("--think", StringComparison.OrdinalIgnoreCase)), problem,
            args.Any(a => a.Equals("--ipc", StringComparison.OrdinalIgnoreCase)), Get("--planner-model", "WINGMAN_PLANNER_MODEL"),
            problem == null && plannerUrl != null ? new Uri(plannerUrl) : null);
    }
}

internal static class Ui
{
    private static readonly Lock Gate = new();

    /// <summary>--ipc: every line goes out as JSON (Ipc.Write) instead of colored text.</summary>
    public static bool Ipc { get; set; }

    public static void Line(string kind, string text, System.Text.Json.Nodes.JsonNode? data = null)
    {
        if (Ipc)
        {
            lock (Gate)
                Console.WriteLine(KspWingman.Tui.Ipc.Write(kind, text, data));
            return;
        }
        var (label, color) = kind switch
        {
            "order" => ("you ", ConsoleColor.White),
            "chat" => ("chat", ConsoleColor.Cyan),
            "call" => ("tool", ConsoleColor.DarkYellow),
            "result" => ("  ->", ConsoleColor.DarkGray),
            "answer" => ("ai  ", ConsoleColor.Green),
            "error" => ("err ", ConsoleColor.Red),
            "flight" => ("fly ", ConsoleColor.Magenta),
            "tower" => ("twr ", ConsoleColor.Cyan),
            "phase" => ("    ", ConsoleColor.DarkGray),
            "free" => ("free", ConsoleColor.Yellow),
            "debrief" or "lesson" or "knowledge" => ("lrn ", ConsoleColor.Blue),
            _ => ("··· ", ConsoleColor.Gray),
        };
        lock (Gate)
        {
            Console.ForegroundColor = color;
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} {label} {text.Replace("\n", "\n              ")}");
            Console.ResetColor();
        }
    }

    public static string FirstLine(string text) => text.Split('\n')[0].Trim();

    /// <summary>
    /// LMP's chat takes one line of at most 300 characters (the bridge refuses more), and KSP's font has no emoji
    /// (Qwen3 likes to end with 🚀✈️).
    /// </summary>
    public static string ForChat(string text)
    {
        var plain = string.Concat(text.EnumerateRunes().Where(r => r.Value < 0x2190 || r.Value is 0x2192 or 0x2264 or 0x2265).Select(r => r.ToString()));
        var line = string.Join(" ", plain.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return line.Length <= 290 ? line : line[..289] + "…";
    }
}

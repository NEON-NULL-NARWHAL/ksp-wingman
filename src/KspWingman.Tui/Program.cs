// KSP Wingman · src/KspWingman.Tui/Program.cs
// The Wingman console. Orders come from this terminal (you are always in charge) or from LMP's chat (players on the
// allow list, starting with the call sign); a model flies the agent's KSP and its answer goes back the same way.
//
//   KspWingman --model qwen3:4b [--llm http://127.0.0.1:11434/v1/] [--krpc-port 50000] [--allow "Ann,Bob"]
//              [--call-sign Wingman] [--mcp path\to\KspWingman.Mcp.exe] [--api-key ...] [--think] [--ipc]
// --ipc: JSON lines in and out, for a host app (Mission Control's Wingman tab); see Ipc.cs.

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
var agent = new Agent(new OpenAiCompatibleLlm(http, options.LlmUrl, options.Model, options.ApiKey), tools,
    Agent.Prompt(options.CallSign, !options.Think));
agent.Event += e => Ui.Line(e.Kind, e.Text);

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
Ui.Line("wingman", "Type an order (\"take off and fly east at 1000 m\"), /status, /forget or /quit.");

var orders = Channel.CreateUnbounded<(string From, string Text, bool FromChat)>();
_ = Task.Run(async () =>
{
    while (!quit.IsCancellationRequested && Console.ReadLine() is { } typed)
    {
        if (options.Ipc)
        {
            if (Ipc.Read(typed) is { } input)
                await orders.Writer.WriteAsync((input.From, input.Text, false));
        }
        else if (typed.Trim().Length > 0)
            await orders.Writer.WriteAsync(("Commander", typed.Trim(), false));
    }
    quit.Cancel();
});
_ = Task.Run(() => ForwardEvents(tools, quit.Token));
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
        if (text.Equals("/status", StringComparison.OrdinalIgnoreCase))
        {
            Ui.Line(options.Ipc ? "status" : "result", await tools.CallAsync("status", new Dictionary<string, object?>(), quit.Token)); // a host app tells it from tool results
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

// The MCP server's own events (phases, flight outcomes, the tower's messages), every 2 s: shown, or passed on in --ipc.
static async Task ForwardEvents(McpTools tools, CancellationToken cancel)
{
    long last = 0;
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
                Ui.Line(kind == "outcome" ? "flight" : kind, e["text"]?.GetValue<string>() ?? "", e["data"]);
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
    string CallSign, IReadOnlyCollection<string> Allowed, bool Think, string? Problem, bool Ipc = false)
{
    public const string Usage =
        "Usage: KspWingman --model <name> [--llm <OpenAI-compatible URL, default Ollama: http://127.0.0.1:11434/v1/>] " +
        "[--api-key <key>] [--krpc-host 127.0.0.1] [--krpc-port 50000] [--mcp <KspWingman.Mcp.exe>] " +
        "[--call-sign Wingman] [--allow \"Player1,Player2\"] [--think] [--ipc]";

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
        return new Options(problem == null ? new Uri(url) : new Uri("http://invalid/"), model ?? "", Get("--api-key", "WINGMAN_LLM_KEY"),
            Get("--krpc-host", "WINGMAN_KRPC_HOST") ?? "127.0.0.1", problem == null ? int.Parse(port, CultureInfo.InvariantCulture) : 0,
            mcp, Get("--call-sign") ?? "Wingman", allowed, args.Any(a => a.Equals("--think", StringComparison.OrdinalIgnoreCase)), problem,
            args.Any(a => a.Equals("--ipc", StringComparison.OrdinalIgnoreCase)));
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

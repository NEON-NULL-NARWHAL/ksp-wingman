// KSP Wingman · src/KspWingman.Tui/McpTools.cs
// The tools, from KspWingman.Mcp started as a child process (the same server Claude Code uses).

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace KspWingman.Tui;

public sealed class McpTools : IToolHost, IAsyncDisposable
{
    /// <summary>
    /// Kept from the model: the console listens to chat and says the answers itself, so a small model can't flood the
    /// chat or sit in wait_for_chat for two minutes.
    /// </summary>
    public static readonly string[] Hidden = ["wait_for_chat", "send_chat", "read_chat"];

    private readonly McpClient _client;

    private McpTools(McpClient client, IList<McpClientTool> all)
    {
        _client = client;
        Tools = all.Where(t => !Hidden.Contains(t.Name))
            .Select(t => new ToolSpec(t.Name, t.Description ?? "", t.JsonSchema)).ToList();
    }

    public IReadOnlyList<ToolSpec> Tools { get; }

    public static async Task<McpTools> StartAsync(string serverExe, string krpcHost, int krpcPort, CancellationToken cancel)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "ksp-wingman",
            Command = serverExe,
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["WINGMAN_KRPC_HOST"] = krpcHost,
                ["WINGMAN_KRPC_PORT"] = krpcPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
        });
        var client = await McpClient.CreateAsync(transport, cancellationToken: cancel);
        var tools = await client.ListToolsAsync(cancellationToken: cancel);
        return new McpTools(client, tools);
    }

    public async Task<string> CallAsync(string name, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancel)
    {
        var result = await _client.CallToolAsync(name, arguments, cancellationToken: cancel);
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        return result.IsError == true ? "Error: " + text : text;
    }

    public async ValueTask DisposeAsync() => await _client.DisposeAsync();
}

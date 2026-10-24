// KSP Wingman · src/KspWingman.Mcp/Program.cs
// Starts the MCP server on stdio. Settings (environment): WINGMAN_KRPC_HOST (127.0.0.1), WINGMAN_KRPC_PORT (50000),
// WINGMAN_TOWER_URL (LUNA EVO Mission Control's KSC tower, e.g. http://localhost:8920/; none: land flies on its own).

using KspWingman.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
// stdout is the MCP channel: every log goes to stderr.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
var host = Environment.GetEnvironmentVariable("WINGMAN_KRPC_HOST") ?? "127.0.0.1";
var port = int.TryParse(Environment.GetEnvironmentVariable("WINGMAN_KRPC_PORT"), out var p) ? p : 50000;
builder.Services.AddSingleton(_ => new KrpcLink(host, port));
builder.Services.AddSingleton(_ => new Director(host, port)); // its own connection: it flies between the tool calls
// The agent's events (phases, outcomes, the tower's messages): the events tool, and a log file with WINGMAN_EVENTS_LOG.
builder.Services.AddSingleton(_ => new FlightEvents(Environment.GetEnvironmentVariable("WINGMAN_EVENTS_LOG") is { Length: > 0 } log ? log : null));
builder.Services.AddSingleton(services => new FlightWatch(host, port, services.GetRequiredService<Director>(), services.GetRequiredService<FlightEvents>()));
builder.Services.AddSingleton(services =>
    new TowerLink(Uri.TryCreate(Environment.GetEnvironmentVariable("WINGMAN_TOWER_URL"), UriKind.Absolute, out var tower) && tower.Host is "localhost" or "127.0.0.1"
        // As localhost: the tower's listener only answers to that name (127.0.0.1 gets a 400 from Windows' HTTP.sys).
        ? new TowerClient(new UriBuilder(tower) { Host = "localhost" }.Uri, host, port, services.GetRequiredService<Director>(), services.GetRequiredService<FlightEvents>())
        : null));
builder.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();
var app = builder.Build();
app.Services.GetRequiredService<TowerLink>(); // the tower client reports from the start, not from the first tool call
app.Services.GetRequiredService<FlightWatch>(); // and the phases are watched from the start
await app.RunAsync();

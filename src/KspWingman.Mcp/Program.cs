// KSP Wingman · src/KspWingman.Mcp/Program.cs
// Starts the MCP server on stdio. Settings (environment): WINGMAN_KRPC_HOST (127.0.0.1), WINGMAN_KRPC_PORT (50000).

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
builder.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();
await builder.Build().RunAsync();

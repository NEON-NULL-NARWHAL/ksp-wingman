// KSP Wingman · tests/KspWingman.Tests/IpcTests.cs
// The console's --ipc mode: what a host app sends (orders, commands) and what it gets back (one JSON object a line).

using System.Text.Json.Nodes;
using KspWingman.Tui;

namespace KspWingman.Tests;

public sealed class IpcTests
{
    [Fact]
    public void Reads_orders_and_commands()
    {
        Assert.Equal(("Commander", "take off"), Ipc.Read("take off")); // plain text is an order
        Assert.Equal(("Ann", "land at KSC"), Ipc.Read("""{"type":"order","text":" land at KSC ","from":"Ann"}"""));
        Assert.Equal(("Commander", "/status"), Ipc.Read("""{"type":"status"}"""));
        Assert.Equal(("Commander", "/quit!"), Ipc.Read("""{"type":"quit"}"""));
        Assert.Null(Ipc.Read(""));
        Assert.Null(Ipc.Read("""{"type":"order","text":"   "}"""));
        Assert.Null(Ipc.Read("""{"type":"launch the nukes"}"""));
        Assert.Null(Ipc.Read("{not json"));
        Assert.Equal(1000, Ipc.Read(JsonSerializerOrder(new string('x', 5000)))!.Value.Text.Length);
    }

    [Fact]
    public void Writes_one_event_per_line()
    {
        var line = Ipc.Write("outcome", "Flight landed.\nNext line.", new JsonObject { ["result"] = "landed" });
        Assert.DoesNotContain('\n', line);
        var o = JsonNode.Parse(line)!.AsObject();
        Assert.Equal("event", o["type"]!.GetValue<string>());
        Assert.Equal("outcome", o["kind"]!.GetValue<string>());
        Assert.Equal("Flight landed.\nNext line.", o["text"]!.GetValue<string>());
        Assert.Equal("landed", o["data"]!["result"]!.GetValue<string>());
    }

    private static string JsonSerializerOrder(string text) => new JsonObject { ["type"] = "order", ["text"] = text }.ToJsonString();
}

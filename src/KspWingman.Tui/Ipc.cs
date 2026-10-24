// KSP Wingman · src/KspWingman.Tui/Ipc.cs
// The console's --ipc mode, for a host app (LUNA EVO Mission Control's Wingman tab): one JSON object a line in (orders
// and commands) and out (everything the console would print, plus the MCP server's events: phases, outcomes, tower).

using System.Text.Json;
using System.Text.Json.Nodes;

namespace KspWingman.Tui;

public static class Ipc
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// One input line: {"type":"order","text":"...","from":"..."}, {"type":"status"}, {"type":"forget"} or
    /// {"type":"quit"}; plain text is an order. Returns the console's own command text ("/status", "/quit!"...) or the
    /// order, and who it's from; null for an empty or unreadable line.
    /// </summary>
    public static (string From, string Text)? Read(string line)
    {
        line = line.Trim();
        if (line.Length == 0)
            return null;
        if (!line.StartsWith('{'))
            return ("Commander", line);
        try
        {
            if (JsonNode.Parse(line) is not JsonObject o || o["type"] is not JsonValue t || !t.TryGetValue<string>(out var type))
                return null;
            var from = o["from"] is JsonValue f && f.TryGetValue<string>(out var who) && who.Trim().Length > 0 ? who.Trim()[..Math.Min(who.Trim().Length, 40)] : "Commander";
            return type switch
            {
                "order" when o["text"] is JsonValue x && x.TryGetValue<string>(out var text) && text.Trim().Length > 0 => (from, text.Trim()[..Math.Min(text.Trim().Length, 1000)]),
                "status" => (from, "/status"),
                "forget" => (from, "/forget"),
                "quit" => (from, "/quit!"), // a host app means it: no "still flying" question
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>One output line: {"type":"event","time":...,"kind":...,"text":...} plus the event's data, if any.</summary>
    public static string Write(string kind, string text, JsonNode? data = null)
    {
        var o = new JsonObject
        {
            ["type"] = "event",
            ["time"] = DateTime.UtcNow,
            ["kind"] = kind,
            ["text"] = text,
        };
        if (data != null)
            o["data"] = data.DeepClone();
        return o.ToJsonString(Json);
    }
}

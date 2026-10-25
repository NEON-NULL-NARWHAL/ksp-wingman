// KSP Wingman · src/KspWingman.Tui/Llm.cs
// Talks to a model through the OpenAI-compatible chat API, which Ollama (/v1) and llama.cpp's server both speak.
// Guide:   Meat and Potatoes/06-Console-And-Learning.md

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KspWingman.Tui;

/// <summary>A tool the model may call: name, what it does, and its parameters as JSON Schema.</summary>
public sealed record ToolSpec(string Name, string Description, JsonElement Parameters);

/// <summary>One call the model asked for. Arguments are already parsed (models send them as a string or an object).</summary>
public sealed record ToolCall(string Id, string Name, IReadOnlyDictionary<string, object?> Arguments, string RawArguments);

/// <summary>The model's answer: text, tool calls, or both.</summary>
public sealed record LlmReply(string Text, IReadOnlyList<ToolCall> Calls);

public interface ILlm
{
    Task<LlmReply> ChatAsync(IReadOnlyList<JsonObject> messages, IReadOnlyList<ToolSpec> tools, CancellationToken cancel);
}

public sealed partial class OpenAiCompatibleLlm(HttpClient http, Uri endpoint, string model, string? apiKey) : ILlm
{
    public async Task<LlmReply> ChatAsync(IReadOnlyList<JsonObject> messages, IReadOnlyList<ToolSpec> tools, CancellationToken cancel)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)m.DeepClone()).ToArray()),
            ["temperature"] = 0.2, // orders, not stories
            ["stream"] = false,
        };
        if (tools.Count > 0)
            body["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = JsonNode.Parse(t.Parameters.GetRawText()),
                },
            }).ToArray());
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "chat/completions")) { Content = JsonContent.Create(body) };
        if (!string.IsNullOrEmpty(apiKey))
            request.Headers.Authorization = new("Bearer", apiKey);
        using var response = await http.SendAsync(request, cancel);
        var text = await response.Content.ReadAsStringAsync(cancel);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"The model server said {(int)response.StatusCode}: {Shorten(text)}");
        return Parse(text);
    }

    /// <summary>Reads a chat completion. Checks every node's kind: servers differ, and a bad answer must not crash the console.</summary>
    internal static LlmReply Parse(string json)
    {
        var root = JsonNode.Parse(json);
        var message = root?["choices"] is JsonArray { Count: > 0 } choices ? choices[0]?["message"] as JsonObject : null;
        if (message == null)
            throw new InvalidOperationException("The model server's answer has no message: " + Shorten(json));
        var content = message["content"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
        var calls = new List<ToolCall>();
        if (message["tool_calls"] is JsonArray list)
            foreach (var item in list.OfType<JsonObject>())
            {
                if (item["function"] is not JsonObject f || f["name"] is not JsonValue n || !n.TryGetValue<string>(out var name))
                    continue;
                var id = item["id"] is JsonValue i && i.TryGetValue<string>(out var idText) ? idText : "call_" + calls.Count;
                var raw = f["arguments"] switch
                {
                    JsonValue a when a.TryGetValue<string>(out var str) => str,
                    JsonObject o => o.ToJsonString(),
                    _ => "{}",
                };
                calls.Add(new ToolCall(id, name, Arguments(raw), raw));
            }
        var text = StripThinking(content);
        if (calls.Count == 0 && CallInText(text) is { } written)
            return new LlmReply("", [written]);
        return new LlmReply(text, calls);
    }

    /// <summary>
    /// Small models sometimes write the call out instead of making it: {"name": "take_off", "arguments": {...}}, maybe
    /// in a code block or &lt;tool_call&gt; tags (seen with Qwen3 4B). Only a reply that is nothing but such a call counts.
    /// </summary>
    internal static ToolCall? CallInText(string text)
    {
        var t = WrittenCall().Replace(text.Trim(), "").Trim();
        if (!t.StartsWith('{') || !t.EndsWith('}'))
            return null;
        try
        {
            if (JsonNode.Parse(t) is not JsonObject o)
                return null;
            var name = (o["name"] ?? o["function"] ?? o["tool"]) is JsonValue n && n.TryGetValue<string>(out var s) ? s : null;
            if (name == null)
                return null;
            var raw = (o["arguments"] ?? o["parameters"]) switch
            {
                JsonObject a => a.ToJsonString(),
                JsonValue a when a.TryGetValue<string>(out var str) => str,
                _ => "{}",
            };
            return new ToolCall("text_call", name, Arguments(raw), raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^```(json)?|```$|</?tool_call>")]
    private static partial Regex WrittenCall();

    /// <summary>Tool arguments as plain values. Unreadable arguments become none (the tool then says what it's missing).</summary>
    internal static IReadOnlyDictionary<string, object?> Arguments(string raw)
    {
        var result = new Dictionary<string, object?>();
        try
        {
            if (JsonNode.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw) is JsonObject o)
                foreach (var (key, value) in o)
                    result[key] = value switch
                    {
                        JsonValue jv when jv.TryGetValue<bool>(out var b) => b,
                        JsonValue jv when jv.TryGetValue<long>(out var l) => l,
                        JsonValue jv when jv.TryGetValue<double>(out var d) => d,
                        JsonValue jv when jv.TryGetValue<string>(out var str) => str,
                        null => null,
                        _ => value.ToJsonString(),
                    };
        }
        catch (JsonException)
        {
        }
        return result;
    }

    /// <summary>Thinking models (Qwen3, DeepSeek) may put their reasoning in the reply: only the answer goes to chat.</summary>
    internal static string StripThinking(string text) => Thinking().Replace(text, "").Trim();

    [GeneratedRegex(@"<think>.*?(</think>|$)", RegexOptions.Singleline)]
    private static partial Regex Thinking();

    private static string Shorten(string text) => text.Length <= 300 ? text : text[..300] + "…";
}

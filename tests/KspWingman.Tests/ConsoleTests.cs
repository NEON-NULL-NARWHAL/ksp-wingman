// KSP Wingman · tests/KspWingman.Tests/ConsoleTests.cs
// The Wingman console without a model or KSP: chat orders, reading model replies, the agent loop with a fake model.
// Guide:   Meat and Potatoes/09-Tests-And-Tools.md

using System.Text.Json;
using System.Text.Json.Nodes;
using KspWingman.Tui;

namespace KspWingman.Tests;

public sealed class ConsoleTests
{
    private static readonly string[] Crew = ["Ann", "Bob"];

    [Fact]
    public void Chat_orders_need_the_call_sign_and_an_allowed_player()
    {
        ChatLine Line(string from, string text) => new(1, from, text);
        Assert.Equal("follow me", Orders.From(Line("Ann", "Wingman, follow me"), "Wingman", "RigViewer", Crew));
        Assert.Equal("land", Orders.From(Line("bob", "@wingman land"), "Wingman", "RigViewer", Crew));
        Assert.Equal("status?", Orders.From(Line("Ann", "RigViewer: status?"), "Wingman", "RigViewer", Crew)); // its player name works too
        Assert.Null(Orders.From(Line("Eve", "Wingman, land"), "Wingman", "RigViewer", Crew)); // not on the list
        Assert.Null(Orders.From(Line("Ann", "nice landing, wingman"), "Wingman", "RigViewer", Crew)); // not an order
        Assert.Null(Orders.From(Line("Ann", "Wingmanship is hard"), "Wingman", "RigViewer", Crew));
        Assert.Null(Orders.From(Line("RigViewer", "Wingman, land"), "Wingman", "RigViewer", ["RigViewer"])); // never itself
        Assert.Null(Orders.From(Line("Ann", "Wingman, land"), "Wingman", "RigViewer", [])); // no list: no chat orders
    }

    [Fact]
    public void Reads_read_chat_lines()
    {
        var lines = ChatLine.ParseAll("#12 Ann: Wingman, follow me\n#13 Bob Smith: ok: go\nNo chat lines.");
        Assert.Equal(2, lines.Count);
        Assert.Equal(new ChatLine(12, "Ann", "Wingman, follow me"), lines[0]);
        Assert.Equal(new ChatLine(13, "Bob Smith", "ok: go"), lines[1]);
    }

    [Fact]
    public void Reads_tool_calls_with_arguments_as_text_or_as_an_object()
    {
        // OpenAI and llama.cpp send the arguments as a string; some servers send an object. Qwen3 may think out loud.
        var reply = OpenAiCompatibleLlm.Parse("""
            {"choices":[{"message":{"content":"<think>they want east</think>On it.","tool_calls":[
              {"id":"a","type":"function","function":{"name":"fly","arguments":"{\"heading\":90,\"altitude\":1000.5}"}},
              {"id":"b","type":"function","function":{"name":"land","arguments":{"runway":"KSC"}}}]}}]}
            """);
        Assert.Equal("On it.", reply.Text);
        Assert.Equal(2, reply.Calls.Count);
        Assert.Equal(90L, reply.Calls[0].Arguments["heading"]);
        Assert.Equal(1000.5, reply.Calls[0].Arguments["altitude"]);
        Assert.Equal("KSC", reply.Calls[1].Arguments["runway"]);
        Assert.Empty(OpenAiCompatibleLlm.Arguments("not json")); // a broken call: no arguments, not a crash
        Assert.Throws<InvalidOperationException>(() => OpenAiCompatibleLlm.Parse("""{"error":"model not found"}"""));
    }

    [Fact]
    public void Reads_a_tool_call_written_out_as_text()
    {
        // Qwen3 4B wrote this instead of calling the tool.
        var reply = OpenAiCompatibleLlm.Parse(
            "{\"choices\":[{\"message\":{\"content\":\"{\\\"name\\\": \\\"take_off\\\", \\\"arguments\\\": {\\\"heading\\\": 90}}\"}}]}");
        Assert.Equal("take_off", Assert.Single(reply.Calls).Name);
        Assert.Equal(90L, reply.Calls[0].Arguments["heading"]);
        Assert.Equal("fly", OpenAiCompatibleLlm.CallInText("<tool_call>\n{\"name\": \"fly\", \"arguments\": {}}\n</tool_call>")!.Name);
        Assert.Null(OpenAiCompatibleLlm.CallInText("I'll take off now: {\"name\": \"take_off\"}")); // words too: an answer
    }

    [Fact]
    public async Task The_agent_calls_tools_until_it_has_an_answer()
    {
        var llm = new FakeLlm(
            new LlmReply("", [new ToolCall("1", "status", new Dictionary<string, object?>(), "{}")]),
            new LlmReply("", [new ToolCall("2", "warp_to_mun", new Dictionary<string, object?>(), "{}")]),
            new LlmReply("I'm on the runway, ready.", []));
        var tools = new FakeTools();
        var agent = new Agent(llm, tools, Agent.Prompt("Wingman", noThink: true));
        var answer = await agent.RunAsync("Ann", "where are you?", CancellationToken.None);

        Assert.Equal("I'm on the runway, ready.", answer);
        Assert.Equal(["status"], tools.Called); // the made-up tool was never called
        var last = llm.Seen[^1];
        Assert.Equal("system", last[0]["role"]!.GetValue<string>());
        Assert.Contains(last, m => m["role"]!.GetValue<string>() == "tool" && m["content"]!.GetValue<string>().Contains("no tool called warp_to_mun"));
        Assert.Contains(last, m => m["role"]!.GetValue<string>() == "tool" && m["content"]!.GetValue<string>() == "On the runway.");
    }

    [Fact]
    public void Catches_answers_that_claim_what_wasnt_done()
    {
        Assert.Equal("follow", Agent.Unbacked("Following you, Ann!", new HashSet<string> { "status" }));
        Assert.Null(Agent.Unbacked("Following you, Ann!", new HashSet<string> { "follow" }));
        Assert.Equal("take_off", Agent.Unbacked("Taking off now.", new HashSet<string> { "launch" }));
        Assert.Null(Agent.Unbacked("I'm in the air heading east at 1500 m.", new HashSet<string> { "status" })); // just where it is
        Assert.Null(Agent.Unbacked("I can't time warp, sorry.", new HashSet<string>()));
        Assert.Equal("", Agent.Unbacked("Let me check where I am.", new HashSet<string>()));
        Assert.Null(Agent.Unbacked("Turning north, climbing to 3000 m.", new HashSet<string> { "take_off" })); // take_off flies on
    }

    [Fact]
    public async Task The_agent_is_asked_once_more_when_it_claims_without_acting()
    {
        var llm = new FakeLlm(
            new LlmReply("Following you!", []),
            new LlmReply("", [new ToolCall("1", "status", new Dictionary<string, object?>(), "{}")]),
            new LlmReply("Following you now.", []),
            new LlmReply("Following you now.", []));
        var agent = new Agent(llm, new FakeTools(), "test");
        var answer = await agent.RunAsync("Ann", "follow me", CancellationToken.None);
        Assert.Equal("Following you now.", answer); // asked twice at most, then the answer goes through
        Assert.Equal(4, llm.Seen.Count);
    }

    [Fact]
    public async Task The_agent_gives_up_after_too_many_steps()
    {
        var loop = Enumerable.Range(0, Agent.MaxSteps + 2)
            .Select(i => new LlmReply("", [new ToolCall($"{i}", "status", new Dictionary<string, object?>(), "{}")])).ToArray();
        var agent = new Agent(new FakeLlm(loop), new FakeTools(), "test");
        var answer = await agent.RunAsync("Ann", "status forever", CancellationToken.None);
        Assert.Contains("couldn't finish", answer);
    }

    [Fact]
    public void Chat_answers_are_one_short_line()
    {
        Assert.Equal("Taking off. Then east.", Ui.ForChat("Taking off.\n\nThen east."));
        Assert.Equal("Climbing to 1500 m, heading 90°!", Ui.ForChat("Climbing to 1500 m, heading 90°! 🚀✈️"));
        Assert.True(Ui.ForChat(new string('x', 500)).Length <= 290);
    }

    private sealed class FakeLlm(params LlmReply[] replies) : ILlm
    {
        private int _next;
        public List<List<JsonObject>> Seen { get; } = [];

        public Task<LlmReply> ChatAsync(IReadOnlyList<JsonObject> messages, IReadOnlyList<ToolSpec> tools, CancellationToken cancel)
        {
            Seen.Add(messages.Select(m => (JsonObject)m.DeepClone()).ToList());
            return Task.FromResult(replies[Math.Min(_next++, replies.Length - 1)]);
        }
    }

    private sealed class FakeTools : IToolHost
    {
        public List<string> Called { get; } = [];

        public IReadOnlyList<ToolSpec> Tools { get; } =
            [new ToolSpec("status", "Where the agent is.", JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement)];

        public Task<string> CallAsync(string name, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancel)
        {
            Called.Add(name);
            return Task.FromResult("On the runway.");
        }
    }
}

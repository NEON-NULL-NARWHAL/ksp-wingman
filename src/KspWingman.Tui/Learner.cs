// KSP Wingman · src/KspWingman.Tui/Learner.cs
// Runs the debrief and free flight (Learning.cs) with the console's tools, planner and events.

using System.Globalization;
using System.Text.Json.Nodes;

namespace KspWingman.Tui;

public sealed class Learner(IToolHost tools, IPlanner planner, EventHub hub, Action<string, string> say)
{
    private static readonly Dictionary<string, object?> None = [];

    /// <summary>How long a new plane settles on the runway before take_off; how often a flight is checked. Tests shorten them.</summary>
    public TimeSpan Settle { get; init; } = TimeSpan.FromSeconds(8);

    public TimeSpan Poll { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The debrief of the flight recorded in a "knowledge" event: the planner writes a lesson, code checks it, and learn
    /// saves it citing the flight. Returns whether a lesson was saved.
    /// </summary>
    public async Task<bool> DebriefAsync(JsonNode? recorded, string outcome, CancellationToken cancel)
    {
        if (recorded?["flightId"] is not JsonValue idValue || !idValue.TryGetValue<long>(out var flightId) ||
            recorded["skills"] is not JsonObject graded || graded.Count == 0)
            return false;
        var skills = graded.ToDictionary(g => g.Key, g => g.Value is JsonValue v && v.TryGetValue<bool>(out var b) && b);
        var knowledge = await tools.CallAsync("knowledge", None, cancel);
        string answer;
        try
        {
            answer = await planner.AskAsync(Debrief.System, Debrief.Prompt(outcome, skills, knowledge), cancel);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException)
        {
            say("error", "Debrief: the planner didn't answer: " + e.Message);
            return false;
        }
        if (Debrief.Parse(answer, skills.Keys) is not { } lesson)
        {
            say("debrief", answer.Trim().StartsWith("NONE", StringComparison.OrdinalIgnoreCase)
                ? "Debrief: nothing new to learn from that flight."
                : "Debrief: no usable lesson in the planner's answer (\"" + OneLine(answer) + "\").");
            return false;
        }
        var saved = await tools.CallAsync("learn", new Dictionary<string, object?> { ["skill"] = lesson.Skill, ["lesson"] = lesson.Lesson, ["flightId"] = flightId }, cancel);
        say("debrief", $"Debrief ({lesson.Skill}): {lesson.Lesson} → {saved}");
        return !saved.StartsWith("The tower refused", StringComparison.Ordinal) && !saved.StartsWith("No tower", StringComparison.Ordinal);
    }

    /// <summary>
    /// Free flight for some minutes: the planner picks a goal, code flies it (a new plane if needed, take off, land on
    /// that approach), the flight is debriefed, and again. Stops for an order (<paramref name="interrupted"/>), at the
    /// time limit, or after too many crashes in a row. Code keeps it in the box: too far or too high, it's sent to land.
    /// </summary>
    public async Task<string> FreeFlyAsync(double minutes, FreeFlightLimits limits, Func<bool> interrupted, CancellationToken cancel)
    {
        var until = DateTime.UtcNow.AddMinutes(Math.Clamp(minutes, 5, 240));
        var history = new List<(Goal Goal, bool Good, bool Lesson)>();
        var crashes = 0;
        var craft = "Aeris 3A";
        while (DateTime.UtcNow < until && !interrupted() && !cancel.IsCancellationRequested)
        {
            var knowledge = await tools.CallAsync("knowledge", None, cancel);
            var proficiency = FreeFlight.Proficiency(knowledge);
            var menu = FreeFlight.Menu(history);
            if (menu.Count == 0)
                return "Free flight stopped: every goal failed twice in a row without a lesson.";
            string answer;
            try
            {
                answer = await planner.AskAsync("You plan practice flights for an AI pilot. Answer briefly.",
                    FreeFlight.Prompt(menu, proficiency, (until - DateTime.UtcNow).TotalMinutes), cancel);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException)
            {
                answer = ""; // the weakest goal then
            }
            var (goal, why) = FreeFlight.Choose(answer, menu, proficiency);
            say("free", $"Next: {goal.Text} ({why}).");

            // Airborne first: a new plane on the runway when it isn't flying.
            var status = await tools.CallAsync("status", None, cancel);
            craft = FreeFlight.Craft(status) ?? craft;
            if (!status.Contains("(Flying)", StringComparison.Ordinal))
            {
                if (status.Contains("(Landed)", StringComparison.Ordinal) || status.Contains("(Splashed)", StringComparison.Ordinal) ||
                    status.Contains("destroyed", StringComparison.Ordinal))
                    await Call("recover", None, cancel);
                var launched = await Call("launch", new Dictionary<string, object?> { ["craft"] = craft, ["site"] = "Runway" }, cancel);
                if (launched.StartsWith("Not launched", StringComparison.Ordinal))
                    return "Free flight stopped: " + launched.Split('\n')[0].Trim();
                await Task.Delay(Settle, cancel);
                var off = await Call("take_off", new Dictionary<string, object?> { ["heading"] = 90.0, ["altitude"] = 900.0 }, cancel);
                if (!off.StartsWith("Airborne", StringComparison.Ordinal))
                    return "Free flight stopped: couldn't take off (" + off.Split('\n')[0].Trim() + ").";
            }
            var mark = hub.Last;
            await Call("land", new Dictionary<string, object?> { ["approach"] = goal.Approach }, cancel);

            // Wait for the flight's outcome, keeping the plane in the box.
            (long Id, string Kind, string Text, JsonNode? Data)? outcome = null;
            var sentBack = false;
            var deadline = DateTime.UtcNow.AddMinutes(25);
            while (outcome == null && DateTime.UtcNow < deadline && !cancel.IsCancellationRequested)
            {
                if (interrupted())
                    return "Free flight stopped for an order.";
                await Task.Delay(Poll, cancel);
                outcome = hub.After(mark, "outcome");
                if (!sentBack && FreeFlight.Where(await tools.CallAsync("status", None, cancel)) is { } where &&
                    (where.Km > limits.RadiusKm || where.Altitude > limits.CeilingM))
                {
                    sentBack = true;
                    say("free", string.Create(CultureInfo.InvariantCulture, $"Outside the box ({where.Km:0} km out, {where.Altitude:0} m): sent back to land."));
                    await Call("land", None, cancel);
                }
            }
            if (outcome is not { } o)
                return "Free flight stopped: no outcome after 25 minutes.";
            var result = o.Data?["result"] is JsonValue r && r.TryGetValue<string>(out var rs) ? rs : "";
            crashes = result is "crashed" or "splashed" ? crashes + 1 : 0;
            if (crashes >= limits.MaxCrashesInARow)
                return $"Free flight stopped: {crashes} crashes in a row.";

            // The knowledge store's grades (a moment later), then the debrief.
            (long Id, string Kind, string Text, JsonNode? Data)? recorded = null;
            for (var i = 0; i < 10 && recorded == null; i++)
            {
                await Task.Delay(Poll / 3, cancel);
                recorded = hub.After(o.Id, "knowledge");
            }
            var good = recorded?.Data?["skills"]?[goal.Skill] is JsonValue g && g.TryGetValue<bool>(out var gb) && gb;
            var learned = recorded != null && await DebriefAsync(recorded.Value.Data, o.Text, cancel);
            history.Add((goal, good, learned));
        }
        return interrupted() ? "Free flight stopped for an order." : "Free flight over.";
    }

    private static string OneLine(string text)
    {
        var line = string.Join(" ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length <= 160 ? line : line[..160] + "…";
    }

    private async Task<string> Call(string name, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancel)
    {
        say("call", $"{name}({System.Text.Json.JsonSerializer.Serialize(arguments)})");
        var result = await tools.CallAsync(name, arguments, cancel);
        say("result", result);
        return result;
    }
}

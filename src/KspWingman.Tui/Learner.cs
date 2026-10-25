// KSP Wingman · src/KspWingman.Tui/Learner.cs
// Runs the debrief and free flight (Learning.cs) with the console's tools, planner and events.
// Guide:   Meat and Potatoes/06-Console-And-Learning.md

using System.Globalization;
using System.Text.Json.Nodes;

namespace KspWingman.Tui;

public sealed class Learner(IToolHost tools, IPlanner planner, EventHub hub, Action<string, string> say)
{
    private static readonly Dictionary<string, object?> None = [];

    /// <summary>How long a new plane settles on the runway before take_off; how often a flight is checked. Tests shorten them.</summary>
    public TimeSpan Settle { get; init; } = TimeSpan.FromSeconds(8);

    public TimeSpan Poll { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How long to wait between tries when another plane is on the runway.</summary>
    public TimeSpan RunwayWait { get; init; } = TimeSpan.FromSeconds(20);

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
        var aboutPlane = Debrief.AboutPlane(answer);
        // A note about the plane quotes only what was measured (the flight and its flight test), never invented figures.
        if (aboutPlane && !Debrief.Grounded(lesson.Lesson, outcome))
        {
            say("debrief", "Debrief: a plane note with numbers the flight didn't measure, not saved (\"" + lesson.Lesson + "\").");
            return false;
        }
        var saved = await tools.CallAsync("learn", new Dictionary<string, object?>
        {
            ["skill"] = lesson.Skill, ["lesson"] = lesson.Lesson, ["flightId"] = flightId, ["aboutPlane"] = aboutPlane,
        }, cancel);
        say("debrief", $"Debrief ({lesson.Skill}{(aboutPlane ? ", about this plane" : "")}): {lesson.Lesson} → {saved}");
        return !saved.StartsWith("The tower refused", StringComparison.Ordinal) && !saved.StartsWith("No tower", StringComparison.Ordinal);
    }

    /// <summary>
    /// Free flight for some minutes: the planner picks a goal, code flies it (a new plane if needed, take off, land on
    /// that approach), the flight is debriefed, and again. Stops for an order (<paramref name="interrupted"/>), at the
    /// time limit, or after too many crashes in a row. Code keeps it in the box: too far or too high, it's sent to land.
    /// </summary>
    /// <param name="focus">Training (the owner's idea): only these skills, until learned (3 good in a row); null: any.</param>
    public async Task<string> FreeFlyAsync(double minutes, FreeFlightLimits limits, Func<bool> interrupted, CancellationToken cancel,
        IReadOnlyList<string>? focus = null)
    {
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rejected = 0; // rejected takeoffs in a row
        var exhausted = new HashSet<Goal>(); // goals that can't be flown this session (no new plane left)
        var idle = 0; // rounds in a row without a flight: never spin (the rig once looped on "no new plane" thousands of times)
        var until = DateTime.UtcNow.AddMinutes(Math.Clamp(minutes, 5, 240));
        var history = new List<(Goal Goal, bool Good, bool Lesson)>();
        var crashes = 0;
        var craft = "Aeris 3A";
        while (DateTime.UtcNow < until && !interrupted() && !cancel.IsCancellationRequested)
        {
            if (++idle > 5)
                return "Free flight stopped: five rounds in a row without a flight.";
            var knowledge = await tools.CallAsync("knowledge", None, cancel);
            var proficiency = FreeFlight.Proficiency(knowledge);
            var menu = FreeFlight.Menu(history, focus).Where(g => !exhausted.Contains(g)).ToList();
            if (menu.Count == 0)
                return "Free flight stopped: every goal failed twice in a row without a lesson.";
            string answer;
            try
            {
                // One goal left (training on one thing): nothing to choose.
                answer = menu.Count == 1 ? "1 - training" : await planner.AskAsync("You plan practice flights for an AI pilot. Answer briefly.",
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
            tried.Add(craft);
            var launchCraft = craft;
            string? groundTested = null; // a new plane's ground test, for its notes
            (bool Air, bool Ground) known = (false, false); // what the server's agents already tested
            if (goal.Approach == "craft")
            {
                // A plane it hasn't flown this session (the knowledge store decides if it's new to this agent).
                var next = FreeFlight.Planes(await tools.CallAsync("launchable_craft", None, cancel)).FirstOrDefault(p => !tried.Contains(p));
                if (next == null)
                {
                    var planes = FreeFlight.Planes(await tools.CallAsync("launchable_craft", None, cancel));
                    var noPlane = $"no plane in the save's SPH that it hasn't flown (it has: {string.Join(", ", planes)}). Put more planes in the save's Ships/SPH folder.";
                    if (focus != null)
                        return "Training stopped: " + noPlane;
                    say("free", "Skipping new planes: " + noPlane);
                    exhausted.Add(goal); // off the menu for the rest of the session
                    continue;
                }
                launchCraft = next;
                tried.Add(next);
                if (status.Contains("(Flying)", StringComparison.Ordinal))
                {
                    // A new plane means a new launch, and launching waits for this one to be down: land it first
                    // (switching to "try different planes" mid-flight in Mission Control stopped free flight at once).
                    say("free", "Landing this plane first, then a new one.");
                    var before = hub.Last;
                    await Call("land", None, cancel);
                    var landBy = DateTime.UtcNow.AddMinutes(25);
                    while (hub.After(before, "outcome") == null && DateTime.UtcNow < landBy && !interrupted() && !cancel.IsCancellationRequested)
                        await Task.Delay(Poll, cancel);
                    if (interrupted())
                        return "Free flight stopped for an order.";
                    status = await tools.CallAsync("status", None, cancel);
                    if (status.Contains("(Flying)", StringComparison.Ordinal))
                        return "Free flight stopped: the plane didn't land in 25 minutes, so no new one could be launched.";
                }
            }
            if (!status.Contains("(Flying)", StringComparison.Ordinal))
            {
                if (status.Contains("(Landed)", StringComparison.Ordinal) || status.Contains("(Splashed)", StringComparison.Ordinal) ||
                    status.Contains("destroyed", StringComparison.Ordinal))
                    await Call("recover", None, cancel);
                var launched = await Call("launch", new Dictionary<string, object?> { ["craft"] = launchCraft, ["site"] = "Runway" }, cancel);
                // Another plane on the runway: wait for it (up to three minutes), as a pilot holds short.
                for (var wait = 0; launched.StartsWith("Not launched yet", StringComparison.Ordinal) && wait < 9 && !interrupted(); wait++)
                {
                    await Task.Delay(RunwayWait, cancel);
                    launched = await Call("launch", new Dictionary<string, object?> { ["craft"] = launchCraft, ["site"] = "Runway" }, cancel);
                }
                if (launched.StartsWith("Not launched", StringComparison.Ordinal))
                    return "Free flight stopped: " + launched.Split('\n')[0].Trim();
                await Task.Delay(Settle, cancel);
                // A new plane: its ground handling first, on the runway (the owner's idea), unless the server's agents
                // already tested it (status shows where its setup came from).
                known = goal.Approach == "craft" ? Tested(await tools.CallAsync("status", None, cancel)) : (false, false);
                groundTested = goal.Approach == "craft" && !known.Ground ? await TestAsync(hub.Last, TimeSpan.FromMinutes(3), interrupted, cancel) : null;
                var off = await Call("take_off", new Dictionary<string, object?> { ["heading"] = 90.0, ["altitude"] = 900.0 }, cancel);
                if (off.StartsWith("Takeoff rejected", StringComparison.Ordinal) && ++rejected < 2)
                {
                    // A rejected takeoff is a failed attempt, not the end of practice: recover, and next time again.
                    say("free", off.Split('\n')[0].Trim());
                    history.Add((goal, false, false));
                    await Call("recover", None, cancel);
                    continue;
                }
                if (!off.StartsWith("Airborne", StringComparison.Ordinal))
                {
                    if (goal.Approach != "craft")
                        return "Free flight stopped: couldn't take off (" + off.Split('\n')[0].Trim() + ").";
                    say("free", $"{launchCraft} didn't get off the ground: {off.Split('\n')[0].Trim()}");
                    history.Add((goal, false, false));
                    continue; // another plane next time
                }
                rejected = 0;
            }
            idle = 0; // it's flying
            var mark = hub.Last;
            var ceiling = limits.CeilingM;
            string? tested = null; // the flight test's results (a new plane), for its notes
            if (goal.Approach == "craft")
                tested = string.Join(" ", new[] { groundTested, known.Air ? null : await TestAsync(mark, TimeSpan.FromMinutes(9), interrupted, cancel) }.OfType<string>()) is { Length: > 0 } both ? both : null;
            if (goal.Approach == "fast")
                await FastLegsAsync(interrupted, cancel);
            else if (goal.Approach == "high")
            {
                ceiling = 8000; // the box's ceiling, for this drill only
                await ClimbHighAsync(interrupted, cancel);
            }
            var radius = limits.RadiusKm;
            if (goal.Approach == "explore")
            {
                // The planner's own legs (free will), each checked by the explore tool; then home to land.
                radius = Math.Max(radius, Drills.ExploreRangeKm + 5);
                await ExploreAsync(interrupted, cancel);
            }
            if (goal.Approach is "fast" or "high" or "craft" or "explore")
                await Call("land", goal.Approach == "craft" ? new Dictionary<string, object?> { ["approach"] = "visual" } : None, cancel);
            else if (goal.Approach == "plan")
                await FileOwnPlanAsync(cancel);
            else if (goal.Approach == "variety")
                await Call("land", await ChooseStyleAsync(cancel), cancel);
            else
                await Call("land", new Dictionary<string, object?> { ["approach"] = goal.Approach }, cancel);

            // Wait for the flight's outcome, keeping the plane in the box (explore's is wider, and it may take longer).
            (long Id, string Kind, string Text, JsonNode? Data)? outcome = null;
            var sentBack = false;
            var deadline = DateTime.UtcNow.AddMinutes(goal.Approach == "explore" ? 40 : 25);
            while (outcome == null && DateTime.UtcNow < deadline && !cancel.IsCancellationRequested)
            {
                if (interrupted())
                    return "Free flight stopped for an order.";
                await Task.Delay(Poll, cancel);
                outcome = hub.After(mark, "outcome");
                if (!sentBack && FreeFlight.Where(await tools.CallAsync("status", None, cancel)) is { } where &&
                    (where.Km > radius || where.Altitude > ceiling))
                {
                    sentBack = true;
                    say("free", string.Create(CultureInfo.InvariantCulture, $"Outside the box ({where.Km:0} km out, {where.Altitude:0} m): sent back to land."));
                    await Call("land", None, cancel);
                }
            }
            if (outcome is not { } o)
                return $"Free flight stopped: no outcome after {(goal.Approach == "explore" ? 40 : 25)} minutes.";
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
            var flownIn = o.Data?["craft"] is JsonValue cv && cv.TryGetValue<string>(out var cs) ? cs : launchCraft;
            var record = $"{o.Text} (in the {flownIn})" + (tested != null ? " " + tested : "");
            var learned = recorded != null && await DebriefAsync(recorded.Value.Data, record, cancel);
            if (tested != null && recorded != null)
                learned |= await PlaneNoteAsync(recorded.Value.Data, flownIn, tested, record, cancel);
            history.Add((goal, good, learned));
            // Training: done once the focus has gone well enough times in a row.
            if (focus != null)
            {
                var streak = history.AsEnumerable().Reverse().TakeWhile(h => h.Good && focus.Contains(h.Goal.Skill)).Count();
                if (streak >= FreeFlight.LearnedInARow)
                    return $"Training done: {string.Join(", ", focus)}, {streak} good flights in a row.";
            }
        }
        return interrupted() ? "Free flight stopped for an order." : "Free flight over.";
    }

    private readonly List<string> _stylesTried = [];

    /// <summary>
    /// Explore (the owner's "free" free flight): the planner picks each leg (bearing, distance, altitude) from where the
    /// plane is, what's been explored and the fuel; the explore tool checks and flies it; on to the next once there.
    /// Home on HOME, low fuel, after <see cref="Drills.MaxLegs"/> legs, or after two refused legs in a row.
    /// </summary>
    private async Task ExploreAsync(Func<bool> interrupted, CancellationToken cancel)
    {
        string? refused = null;
        var refusals = 0;
        for (var leg = 1; leg <= Drills.MaxLegs && !interrupted() && !cancel.IsCancellationRequested; leg++)
        {
            var status = await tools.CallAsync("status", None, cancel);
            if (FuelLeft(status) is { } fuel && fuel < Drills.FuelReserve + 0.1)
            {
                say("free", $"Explore: fuel down to {fuel * 100:0}%, heading home.");
                return;
            }
            string answer;
            try
            {
                answer = await planner.AskAsync(Drills.ExploreSystem, Drills.ExplorePrompt(status, await tools.CallAsync("knowledge", None, cancel), leg, refused), cancel);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException)
            {
                return;
            }
            if (Drills.Home(answer) || Drills.ParseLeg(answer) is not { } next)
            {
                say("free", "Explore: heading home (" + OneLine(answer) + ").");
                return;
            }
            var mark = hub.Last;
            var flown = await Call("explore", new Dictionary<string, object?> { ["bearing"] = next.Bearing, ["distanceKm"] = next.Km, ["altitude"] = next.Altitude }, cancel);
            if (flown.StartsWith("Not", StringComparison.Ordinal))
            {
                refused = flown;
                if (++refusals >= 2)
                    return;
                leg--; // a refused leg isn't a leg
                continue;
            }
            (refused, refusals) = (null, 0);
            // There at 150 m/s, with room for the climb and the turn.
            var by = DateTime.UtcNow.AddSeconds(next.Km * 1000 / 150 * 1.5 + 90);
            while (hub.After(mark, "place") == null && hub.After(mark, "outcome") == null && DateTime.UtcNow < by && !interrupted())
                await Task.Delay(Poll, cancel);
            if (hub.After(mark, "outcome") != null)
                return; // the flight ended (a crash)
            if (hub.After(mark, "place") is { } found)
                say("free", found.Text);
        }
    }

    /// <summary>The fuel left (0-1) from status's "Fuel: 63% fuel..." line, or null.</summary>
    public static double? FuelLeft(string status)
    {
        var m = System.Text.RegularExpressions.Regex.Match(status, @"Fuel: (\d+)% fuel");
        return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 100 : null;
    }

    /// <summary>Approach variety: the planner picks a riskier final (free will); land gets it, fitted to the plane.</summary>
    private async Task<Dictionary<string, object?>> ChooseStyleAsync(CancellationToken cancel)
    {
        var status = await tools.CallAsync("status", None, cancel);
        string answer;
        try
        {
            answer = await planner.AskAsync(Drills.StyleSystem, Drills.StylePrompt(status, _stylesTried), cancel);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException)
        {
            answer = "";
        }
        var style = Drills.ParseStyle(answer, _stylesTried);
        _stylesTried.Add(style);
        say("free", $"Trying a {style} final.");
        return Drills.LandArguments(style, status);
    }

    /// <summary>Whether the plane's setup (status's "Setup for ...: ... (from ...)") came from a flight test, a ground test.</summary>
    public static (bool Air, bool Ground) Tested(string status)
    {
        var setup = status.Split('\n').FirstOrDefault(l => l.Trim().StartsWith("Setup for", StringComparison.Ordinal)) ?? "";
        return (setup.Contains("its flight test", StringComparison.Ordinal), setup.Contains("ground test", StringComparison.Ordinal));
    }

    /// <summary>
    /// A test of the new plane (test_plane): on the runway the ground test, in the air the flight test; then its
    /// results from the "test" event. Null when it couldn't run, stopped early, or the flight ended (a crash) first.
    /// </summary>
    private async Task<string?> TestAsync(long mark, TimeSpan longest, Func<bool> interrupted, CancellationToken cancel)
    {
        var started = await Call("test_plane", None, cancel);
        if (!started.StartsWith("Flight test", StringComparison.Ordinal) && !started.StartsWith("Ground test", StringComparison.Ordinal))
            return null;
        var deadline = DateTime.UtcNow + longest;
        while (DateTime.UtcNow < deadline && !interrupted() && !cancel.IsCancellationRequested)
        {
            await Task.Delay(Poll, cancel);
            if (hub.After(mark, "test") is { } t)
            {
                say("free", t.Text);
                return t.Text.Contains("stopped", StringComparison.Ordinal) ? null : t.Text;
            }
            if (hub.After(mark, "outcome") != null)
                return null;
        }
        return null;
    }

    /// <summary>
    /// After a flight test: the planner writes one note about how the plane differs, from the measured numbers; code
    /// checks the numbers and saves it as a note about that plane, citing the flight.
    /// </summary>
    private async Task<bool> PlaneNoteAsync(JsonNode? recorded, string craft, string test, string record, CancellationToken cancel)
    {
        if (recorded?["flightId"] is not JsonValue idValue || !idValue.TryGetValue<long>(out var flightId) || recorded["skills"] is not JsonObject graded || graded.Count == 0)
            return false;
        var skills = graded.Select(g => g.Key).ToList();
        string answer;
        try
        {
            answer = await planner.AskAsync(Debrief.System, Debrief.PlanePrompt(craft, test, record, skills), cancel);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return false;
        }
        if (Debrief.Parse(answer, skills) is not { } note)
            return false;
        if (!Debrief.Grounded(note.Lesson, record))
        {
            say("debrief", "Plane note with numbers the test didn't measure, not saved (\"" + note.Lesson + "\").");
            return false;
        }
        var saved = await tools.CallAsync("learn", new Dictionary<string, object?>
        {
            ["skill"] = note.Skill, ["lesson"] = note.Lesson, ["flightId"] = flightId, ["aboutPlane"] = true,
        }, cancel);
        say("debrief", $"Plane note ({craft}): {note.Lesson} → {saved}");
        return !saved.StartsWith("The tower refused", StringComparison.Ordinal) && !saved.StartsWith("No tower", StringComparison.Ordinal);
    }

    /// <summary>
    /// The planner builds a flight plan from the tower's table and where the plane is; code checks it with the tower's
    /// rules (once more with the reason if it's wrong) and files it. Without a good plan, a plain landing.
    /// </summary>
    private async Task FileOwnPlanAsync(CancellationToken cancel)
    {
        var raw = await tools.CallAsync("procedures", None, cancel);
        JsonNode? table = null;
        try
        {
            table = raw.TrimStart().StartsWith('{') ? JsonNode.Parse(raw) : null;
        }
        catch (System.Text.Json.JsonException)
        {
        }
        var at = FlightPlan.Position(await tools.CallAsync("status", None, cancel));
        if (table == null || at == null)
        {
            await Call("land", None, cancel);
            return;
        }
        var (along, cross) = FlightPlan.Frame(at.Value.Latitude, at.Value.Longitude);
        var text = FlightPlan.Describe(table);
        string? problem = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            string answer;
            try
            {
                answer = await planner.AskAsync(FlightPlan.System, FlightPlan.Prompt(text, FlightPlan.Where(along, cross), problem), cancel);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException)
            {
                break;
            }
            var plan = FlightPlan.Parse(answer);
            problem = plan.Count == 0 ? "no PLAN line" : FlightPlan.Check(table, plan, along, cross);
            if (problem == null)
            {
                say("free", "Filing: " + string.Join(" ", plan));
                var filed = await Call("file_plan", new Dictionary<string, object?> { ["fixes"] = string.Join(" ", plan) }, cancel);
                if (!filed.Contains("unable", StringComparison.OrdinalIgnoreCase))
                    return;
                problem = filed;
            }
            say("free", "The plan didn't check out: " + problem);
        }
        await Call("land", None, cancel); // no good plan: the tower's own pick
    }

    /// <summary>The fly-fast drill: 220 m/s legs west and east at 1,500 m, about a minute each, inside the box.</summary>
    private async Task FastLegsAsync(Func<bool> interrupted, CancellationToken cancel)
    {
        foreach (var heading in new[] { 270.0, 90.0 })
        {
            await Call("fly", new Dictionary<string, object?> { ["heading"] = heading, ["altitude"] = 1500.0, ["speed"] = 220.0 }, cancel);
            for (var i = 0; i < 20 && !interrupted(); i++)
                await Task.Delay(Poll, cancel);
        }
    }

    /// <summary>
    /// The fly-high drill: climb to 6,000 m in a square of right turns over KSC (it stays in the box), up to 6 minutes.
    /// Legs back and forth didn't: a 180° turn at 160 m/s takes 90 s, longer than a leg, so the rig's plane always turned
    /// the same way and spiralled 48 km south.
    /// </summary>
    private async Task ClimbHighAsync(Func<bool> interrupted, CancellationToken cancel)
    {
        double[] square = [270, 0, 90, 180];
        for (var leg = 0; leg < 8 && !interrupted(); leg++)
        {
            await Call("fly", new Dictionary<string, object?> { ["heading"] = square[leg % 4], ["altitude"] = 6000.0, ["speed"] = 170.0 }, cancel);
            for (var i = 0; i < 15 && !interrupted(); i++)
                await Task.Delay(Poll, cancel);
            if (FreeFlight.Where(await tools.CallAsync("status", None, cancel)) is { Altitude: > 5600 })
                return;
        }
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

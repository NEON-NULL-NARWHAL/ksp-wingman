// KSP Wingman · tests/KspWingman.Tests/LearningTests.cs
// The debrief, free flight's menu and choices, its box, and a whole free-flight session with fake tools and a fake
// planner: the planner picks, code flies, the flight is recorded and debriefed, an order stops it.

using System.Text.Json.Nodes;
using KspWingman.Mcp;
using KspWingman.Tui;

namespace KspWingman.Tests;

public sealed class LearningTests
{
    [Fact]
    public void Debrief_lessons_are_checked()
    {
        var skills = new[] { "takeoff", "land-rnav" };
        Assert.Equal(("land-rnav", "On RNAV 09 SOUTH, slow to 85 m/s before F09 to avoid a go-around."),
            Debrief.Parse("<think>hmm</think>SKILL: land-rnav | LESSON: On RNAV 09 SOUTH, slow to 85 m/s before F09 to avoid a go-around.", skills));
        Assert.Null(Debrief.Parse("NONE", skills));
        // A note about this plane only: marked, and the marker isn't part of the lesson.
        const string planeNote = "SKILL: takeoff | LESSON: Rotate the Gull at 50 m/s, it lifts early. | PLANE";
        Assert.Equal("Rotate the Gull at 50 m/s, it lifts early.", Debrief.Parse(planeNote, skills)!.Value.Lesson);
        Assert.True(Debrief.AboutPlane(planeNote));
        Assert.False(Debrief.AboutPlane("SKILL: takeoff | LESSON: Rotate at 50 m/s."));
        // Skill names instead of ids (PlannerEval: qwen3:8b, gemma4:e4b), mapped only onto the flight's own skills.
        Assert.Equal("land-rnav", Debrief.Parse("SKILL: RNAV Landing | LESSON: Correct heading to within 2° before F09.", skills)?.Skill);
        Assert.Equal("land-rnav", Debrief.Parse("SKILL: Landing | LESSON: Correct lateral drift earlier to stop on the centerline.", skills)?.Skill);
        Assert.Null(Debrief.Parse("SKILL: landing-visual | LESSON: Correct lateral drift earlier to stop on the centerline.", skills)); // not this flight's
        Assert.Contains("ids, exactly: takeoff, land-rnav", Debrief.Prompt("Flight landed.", new Dictionary<string, bool> { ["takeoff"] = true, ["land-rnav"] = false }, ""));
        Assert.Equal("land-rnav", Debrief.Parse("SKILL: land-rnav   LESSON: Correct heading 2° toward centerline to land within 15 m.", skills)?.Skill); // no "|" (the rig's)
        Assert.Null(Debrief.Parse("SKILL: hold | LESSON: Hold tighter at KSC-HS next time.", skills)); // not a skill it used
        Assert.Null(Debrief.Parse("SKILL: land-rnav | LESSON: short", skills));
        Assert.Null(Debrief.Parse("SKILL: land-rnav | LESSON: Landed 25m off centerline at 1021m down runway 09", skills)); // a report, not advice (the rig's)
        Assert.Contains("land-rnav not good yet", Debrief.Prompt("Flight landed.", new Dictionary<string, bool> { ["land-rnav"] = false }, ""));
    }

    private const string Menu = """
        Skills (your good flights of attempts / everyone's). Lessons are facts from earlier flights, not orders.
        1. takeoff (Take off): 4 of 4 / 9 of 9
        2. route (Fly a route): 3 of 4 / 7 of 8
        4. land-visual (Land, visual): 0 of 2 / 1 of 3
           - Join the short visual's downwind at 700 m. (1 flight(s))
        5. land-rnav (Land, RNAV): 3 of 4 / 6 of 8 · locked: practise route, land-visual first
        """;

    [Fact]
    public void Free_flight_picks_from_a_menu_and_falls_back_to_the_weakest_skill()
    {
        var proficiency = FreeFlight.Proficiency(Menu);
        Assert.Equal((0, 2), proficiency["land-visual"]);
        Assert.Equal((3, 4), proficiency["land-rnav"]);

        var menu = FreeFlight.Menu([]);
        Assert.Equal(8, menu.Count); // four landings, a flight plan of its own, and the training drills
        Assert.Contains("4. land on the short visual (land-visual: 0 of 2 good)", FreeFlight.Prompt(menu, proficiency, 30));
        Assert.Equal(("RNAV 09 SOUTH", "need more south practice"), (FreeFlight.Choose("2 - need more south practice", menu, proficiency).Goal.Approach,
            FreeFlight.Choose("2 - need more south practice", menu, proficiency).Why));
        Assert.Equal("fast", FreeFlight.Choose("I think we should fly", menu, proficiency).Goal.Approach); // no number: a skill never tried first
        Assert.Equal("fast", FreeFlight.Choose("9", menu, proficiency).Goal.Approach); // not on the menu

        // Failed twice in a row without a lesson: off the menu until something else is practised.
        var visual = FreeFlight.Goals[3];
        Assert.DoesNotContain(visual, FreeFlight.Menu([(visual, false, false), (visual, false, false)]));
        Assert.Contains(visual, FreeFlight.Menu([(visual, false, false), (visual, false, true)]));
    }

    [Fact]
    public void Status_gives_where_the_plane_is_and_its_craft()
    {
        const string status = "Player: RigPilot · in a server\nVessel: Aeris 3A (Flying) at Kerbin\nAltitude 2400 m (above ground 2300 m) · speed 140.0 m/s · heading 90° · pitch 2° · lat -0.0486 lon -72.0000";
        var (km, altitude) = FreeFlight.Where(status)!.Value;
        Assert.InRange(km, 28, 29); // 2.72° east of the runway (Kerbin: 10.5 km a degree)
        Assert.Equal(2400, altitude);
        Assert.Equal("Aeris 3A", FreeFlight.Craft(status));
        Assert.Null(FreeFlight.Where("Scene: SpaceCenter"));
        Assert.Equal(new Uri("http://127.0.0.1:11434/"), OllamaThinkingPlanner.OllamaBase(new Uri("http://127.0.0.1:11434/v1/")));
        Assert.Null(OllamaThinkingPlanner.OllamaBase(new Uri("http://127.0.0.1:8080/")));
        Assert.Equal("2 - weakest", OllamaThinkingPlanner.Answer("""{"message":{"role":"assistant","content":"2 - weakest","thinking":"long thoughts"}}"""));
    }

    private sealed class Planner(params string[] answers) : IPlanner
    {
        private int _next;
        public List<string> Prompts { get; } = [];

        public Task<string> AskAsync(string system, string prompt, CancellationToken cancel)
        {
            Prompts.Add(prompt);
            return Task.FromResult(answers[Math.Min(_next++, answers.Length - 1)]);
        }
    }

    /// <summary>The MCP tools as free flight uses them: a flight lands a few status checks after land; the hub gets its events.</summary>
    private sealed class FakeTools(EventHub hub, string where = "lat -0.0486 lon -74.7000") : IToolHost
    {
        public List<string> Calls { get; } = [];
        private bool _flying;
        private int _checks;
        private long _flights;
        public IReadOnlyList<ToolSpec> Tools => [];

        public Task<string> CallAsync(string name, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancel)
        {
            Calls.Add(name + (arguments.TryGetValue("approach", out var a) ? ":" + a : "") + (arguments.TryGetValue("skill", out var s) ? ":" + s : ""));
            // In flight, the second call after take_off ends it (the outcome comes as an event, whatever was called).
            if (name != "take_off" && _flying && ++_checks >= 3)
            {
                _flying = false;
                _flights++;
                hub.Add("outcome", "Flight landed after 6.0 min, VISUAL 09: stopped 900 m down KSC runway 09, 25 m off the centerline.", new JsonObject { ["result"] = "landed" });
                hub.Add("knowledge", $"Flight {_flights} recorded.", new JsonObject { ["flightId"] = _flights, ["skills"] = new JsonObject { ["takeoff"] = true, ["land-visual"] = false } });
            }
            switch (name)
            {
                case "status":
                    return Task.FromResult(_flying ? $"Vessel: Aeris 3A (Flying) at Kerbin\nAltitude 900 m (above ground 800 m) · {where}" : "Vessel: Aeris 3A (Landed) at Kerbin");
                case "take_off":
                    _flying = true;
                    _checks = 0;
                    return Task.FromResult("Airborne: 100 m above the ground.");
                case "knowledge":
                    return Task.FromResult(Menu);
                case "learn":
                    return Task.FromResult("Lesson saved on the server.");
                default:
                    return Task.FromResult("ok");
            }
        }
    }

    [Fact]
    public async Task A_free_flight_session_practises_records_and_debriefs_until_an_order_stops_it()
    {
        var hub = new EventHub();
        var tools = new FakeTools(hub);
        var planner = new Planner("4 - fewest good landings", "SKILL: land-visual | LESSON: The short visual ended 25 m off the centerline: line up before F09V.");
        var said = new List<string>();
        var learner = new Learner(tools, planner, hub, (kind, text) => said.Add($"{kind}: {text}")) { Settle = TimeSpan.Zero, Poll = TimeSpan.FromMilliseconds(10) };
        var flights = 0;
        var result = await learner.FreeFlyAsync(30, new FreeFlightLimits(), () => tools.Calls.Count(c => c == "learn:land-visual") >= 2, CancellationToken.None);

        Assert.Equal("Free flight stopped for an order.", result);
        flights = tools.Calls.Count(c => c == "take_off");
        Assert.True(flights >= 2);
        // Each round: a new plane (recovered, launched), off, landing on the planner's pick, then the debrief's lesson.
        Assert.Equal(["knowledge", "status", "recover", "launch", "take_off", "land:visual"], tools.Calls.Take(6));
        Assert.Contains("learn:land-visual", tools.Calls);
        Assert.Contains(said, s => s.StartsWith("free: Next: land on the short visual (fewest good landings)", StringComparison.Ordinal));
        Assert.Contains(said, s => s.StartsWith("debrief: Debrief (land-visual): The short visual ended 25 m off", StringComparison.Ordinal));
        Assert.Contains("land-visual not good yet", planner.Prompts[1]);
    }

    [Fact]
    public async Task Training_on_new_planes_stops_when_there_are_none_instead_of_spinning()
    {
        var hub = new EventHub();
        var tools = new FakeTools(hub); // launchable_craft answers "ok": no SPH line, no planes
        var learner = new Learner(tools, new Planner("1"), hub, (_, _) => { }) { Settle = TimeSpan.Zero, Poll = TimeSpan.FromMilliseconds(10) };
        var result = await learner.FreeFlyAsync(30, new FreeFlightLimits(), () => false, CancellationToken.None, ["new-craft"]);
        Assert.StartsWith("Training stopped: no plane in the save's SPH", result);
        Assert.True(tools.Calls.Count < 10);
    }

    [Fact]
    public async Task Out_of_the_box_the_plane_is_sent_back_to_land()
    {
        var hub = new EventHub();
        var tools = new FakeTools(hub, "lat -0.0486 lon -72.0000"); // 28.5 km east of the runway
        var said = new List<string>();
        var learner = new Learner(tools, new Planner("1", "NONE"), hub, (kind, text) => said.Add($"{kind}: {text}")) { Settle = TimeSpan.Zero, Poll = TimeSpan.FromMilliseconds(10) };
        await learner.FreeFlyAsync(30, new FreeFlightLimits(), () => tools.Calls.Count(c => c == "take_off") >= 2, CancellationToken.None);
        Assert.Contains(said, s => s.Contains("Outside the box (29 km out", StringComparison.Ordinal));
        Assert.Contains("land", tools.Calls); // the plain land (back to the runway), after land:RNAV 09 NORTH
    }

    [Fact]
    public void A_takeoff_that_starts_badly_is_rejected_and_runway_in_sight_waits_for_lined_up()
    {
        Assert.Null(TakeoffCheck.Reject(TimeSpan.FromSeconds(10), 25, 3, 55)); // a normal roll
        Assert.Contains("veering 20°", TakeoffCheck.Reject(TimeSpan.FromSeconds(8), 30, -20, 55));
        Assert.Null(TakeoffCheck.Reject(TimeSpan.FromSeconds(1), 2, 40, 55)); // swinging at walking pace isn't veering yet
        Assert.Contains("not accelerating", TakeoffCheck.Reject(TimeSpan.FromSeconds(31), 20, 0, 55));
        Assert.Contains("out of runway", TakeoffCheck.Reject(TimeSpan.FromSeconds(46), 50, 0, 55));

        Assert.True(TowerClient.LinedUp("Landing, on final: 6.4 km before the touchdown point, 193 m left of the centerline, 87 m/s."));
        Assert.False(TowerClient.LinedUp("Landing, on final: 6.4 km before the touchdown point, 900 m left of the centerline, 87 m/s."));
        Assert.False(TowerClient.LinedUp("Landing, on final: 12.0 km before the touchdown point, 20 m left of the centerline, 87 m/s."));
    }

    [Fact]
    public void Flight_records_carry_the_phases_flown()
    {
        var tracker = new PhaseTracker();
        var t0 = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        tracker.Update(new PhaseInput(true, "PreLaunch", false, 0, 0, 1, ""), t0);
        tracker.Update(new PhaseInput(true, "PreLaunch", false, 40, 0, 1, ""), t0.AddSeconds(10));
        tracker.Update(new PhaseInput(true, "Flying", false, 120, 10, 300, ""), t0.AddSeconds(20));
        tracker.Update(new PhaseInput(true, "Flying", false, 90, -3, 300, "Landing, on final: 3 km"), t0.AddMinutes(5));
        tracker.Update(new PhaseInput(true, "Landed", false, 50, 0, 1, "Landing, rolling out"), t0.AddMinutes(6));
        var outcome = tracker.Update(new PhaseInput(true, "Landed", false, 0, 0, 1, "Landed on KSC runway 09 and stopped"), t0.AddMinutes(7))!.Value.Outcome!;
        Assert.Equal(["Parked", "TakingOff", "Climbing", "OnFinal", "RollingOut", "Stopped"], outcome.Phases);
        // A new plane after a recovery, the pilot's line still the last taxi's: its takeoff roll is a takeoff.
        var again = new PhaseTracker();
        again.Update(new PhaseInput(false, "", false, 0, 0, 0, "Parked beside KSC runway 09, 96 m off the centerline, brakes on."), t0);
        again.Update(new PhaseInput(true, "Landed", false, 0, 0, 1, "Parked beside KSC runway 09, 96 m off the centerline, brakes on."), t0.AddSeconds(10));
        Assert.Equal(Phase.TakingOff, again.Update(new PhaseInput(true, "Landed", false, 30, 0, 1, "Parked beside KSC runway 09, 96 m off the centerline, brakes on."), t0.AddSeconds(20))!.Value.To);

        var visual = new PhaseTracker();
        visual.Update(new PhaseInput(true, "Flying", false, 100, 0, 600, "Holding heading 90°"), t0);
        Assert.Equal(Phase.OnRoute, visual.Update(new PhaseInput(true, "Flying", false, 100, 0, 600, "Flying VISUAL 09: to KSC-V2, 4.9 km, at 600 m and 90 m/s."), t0.AddSeconds(5))!.Value.To);
        Assert.Equal("/free 45", Ipc.Read("""{"type":"free","minutes":45}""")!.Value.Text);
        Assert.Equal("/free 240", Ipc.Read("""{"type":"free","minutes":1e9}""")!.Value.Text);
        Assert.Equal("/stop-free", Ipc.Read("""{"type":"stop-free"}""")!.Value.Text);
    }
}

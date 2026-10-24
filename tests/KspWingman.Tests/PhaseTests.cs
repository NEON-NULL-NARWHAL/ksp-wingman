// KSP Wingman · tests/KspWingman.Tests/PhaseTests.cs
// Flight phases and outcomes from readings like the rig's (a landing with a go-around, a taxi, a crash), and the taxi.

using KspWingman.Mcp;

namespace KspWingman.Tests;

public sealed class PhaseTests
{
    private static readonly DateTime T0 = new(2026, 10, 23, 15, 0, 0, DateTimeKind.Utc);

    private static PhaseInput Ground(double speed, string pilot = "Not flying anything.", string situation = "Landed") =>
        new(true, situation, false, speed, 0, 1, pilot);

    private static PhaseInput Air(double vs, string pilot = "Holding heading 90°, 900 m, 150 m/s.") => new(true, "Flying", false, 120, vs, 500, pilot);

    [Fact]
    public void A_whole_flight_gives_its_phases_and_one_outcome()
    {
        var landing = new LandingResult("KSC runway 09", 1105, 11, 2);
        var readings = new (PhaseInput Input, string? Approach)[]
        {
            (Ground(0, situation: "PreLaunch"), null),
            (Ground(1.5, situation: "PreLaunch"), null), // settling after it spawned: still parked
            (Ground(40, situation: "PreLaunch"), null),
            (Air(12), null),
            (Air(0), null),
            (Air(-2, "Flying RNAV 09 SOUTH: to KSC-S1, 2.2 km, at 900 m and 110 m/s."), "RNAV 09 SOUTH"),
            (Air(-6, "Landing, going around (not lined up): 1.3 km before the touchdown point."), "RNAV 09 SOUTH"),
            (Air(-6, "Landing, on final: 9.1 km before the touchdown point, 250 m left of the centerline, 88 m/s."), "RNAV 09 SOUTH"),
            (Air(-1, "Landing, flaring: 191 m past the touchdown point."), "RNAV 09 SOUTH"),
            (Ground(62, "Landing, rolling out: 472 m past the touchdown point."), "RNAV 09 SOUTH"),
            (Ground(0, "Landed on KSC runway 09 and stopped"), "RNAV 09 SOUTH"),
            (Ground(5, "Taxiing off KSC runway 09: 30 m off the centerline, 5.0 m/s."), null),
            (Ground(4, "Parked beside KSC runway 09, 90 m off the centerline, brakes on."), null), // still braking
            (Ground(0, "Parked beside KSC runway 09, 90 m off the centerline, brakes on."), null),
            (new PhaseInput(false, "", false, 0, 0, 0, ""), null),
        };
        var tracker = new PhaseTracker();
        var changes = readings.Select((r, i) => tracker.Update(r.Input, T0.AddMinutes(i), r.Approach, landing)).OfType<PhaseChange>().ToList();

        Assert.Equal([Phase.Parked, Phase.TakingOff, Phase.Climbing, Phase.Cruising, Phase.OnRoute, Phase.OnFinal, Phase.Flaring,
            Phase.RollingOut, Phase.Stopped, Phase.Taxiing, Phase.Stopped, Phase.NotFlying], changes.Select(c => c.To));
        var outcome = Assert.Single(changes, c => c.Outcome != null).Outcome!;
        Assert.Equal("landed", outcome.Result);
        Assert.Equal(1, outcome.GoArounds);
        Assert.Equal("RNAV 09 SOUTH", outcome.Approach);
        Assert.Equal(1105, outcome.Landing!.DownRunway);
        Assert.Equal(7, outcome.Minutes); // airborne at minute 3, stopped at minute 10
        Assert.Contains("stopped 1105 m down KSC runway 09, 11 m off the centerline, 2° off the heading", FlightWatch.Describe(outcome));
    }

    [Fact]
    public void A_crash_and_a_flight_left_in_the_air_are_outcomes_too()
    {
        var crash = new PhaseTracker();
        crash.Update(Air(12), T0);
        var crashed = crash.Update(new PhaseInput(true, "Flying", true, 0, 0, 0, ""), T0.AddMinutes(3))!.Value;
        Assert.Equal(Phase.Crashed, crashed.To);
        Assert.Equal("crashed", crashed.Outcome!.Result);
        Assert.Null(crashed.Outcome.Landing);

        var left = new PhaseTracker();
        left.Update(Air(0), T0);
        Assert.Equal("ended in the air", left.Update(new PhaseInput(false, "", false, 0, 0, 0, ""), T0.AddMinutes(1))!.Value.Outcome!.Result);

        // Taking off again after a landing is a new flight, not a rollout.
        var again = new PhaseTracker();
        again.Update(Air(0), T0);
        again.Update(Ground(50, "Landing, rolling out"), T0.AddMinutes(1));
        again.Update(Ground(0), T0.AddMinutes(2));
        Assert.Equal(Phase.TakingOff, again.Update(Ground(30), T0.AddMinutes(3))!.Value.To);
    }

    [Fact]
    public void Taxi_turns_right_off_the_runway_at_walking_pace_and_parks()
    {
        // On the centerline pointing down the runway (east): steer right (negative wheel steering), throttle on.
        var (steer, throttle, brakes, done) = Taxi.Step(runwayHeading: 90, heading: 90, cross: 0, speed: 2);
        Assert.True(steer < 0 && throttle > 0 && !brakes && !done);
        // Too fast: brakes.
        Assert.True(Taxi.Step(90, 180, 40, 12).Brakes);
        // Far enough beside it: stop.
        Assert.True(Taxi.Step(90, 180, Taxi.ParkOffset + 1, 3).Done);
    }
}

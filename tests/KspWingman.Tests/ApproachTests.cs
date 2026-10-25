// KSP Wingman · tests/KspWingman.Tests/ApproachTests.cs
// Published approaches: the tower's pick, the clearance, the route flown in a small simulation, and the rollout.
// Guide:   Meat and Potatoes/09-Tests-And-Tools.md

using KspWingman.Mcp;

namespace KspWingman.Tests;

public sealed class ApproachTests
{
    [Theory]
    [InlineData(-12_000, 500, 85, "RNAV 09 STRAIGHT-IN")] // far out west, facing the runway
    [InlineData(1000, -800, 90, "RNAV 09 NORTH")] // just took off, north of the centerline
    [InlineData(20_000, 4000, 270, "RNAV 09 SOUTH")] // coming home from the east, south side
    [InlineData(-12_000, 500, 270, "RNAV 09 SOUTH")] // far out west but flying away: not straight in
    public void The_tower_picks_by_where_the_plane_is(double along, double cross, double heading, string expected) =>
        Assert.Equal(expected, Tower.Pick(Approach.Ksc, along, cross, heading, Runway.Ksc.Heading).Name);

    [Fact]
    public void The_clearance_names_the_approach_and_its_fixes()
    {
        var clearance = Tower.Clearance("Wingman", Approach.Find("north")!);
        Assert.Equal("KSC tower: Wingman, cleared RNAV 09 NORTH. Proceed to KSC-N1 at 900 m, 110 m/s, then KSC-N2, KSC-N3, F09, land KSC runway 09.", clearance);
        Assert.Null(Approach.Find("RNAV 27"));
    }

    [Theory]
    [InlineData("RNAV 09 NORTH", 1000, -800, 90)]
    [InlineData("RNAV 09 SOUTH", 20_000, 4000, 270)]
    [InlineData("RNAV 09 STRAIGHT-IN", -15_000, 1500, 80)]
    public void Flying_an_approach_ends_lined_up_at_the_final_fix(string name, double along, double cross, double heading)
    {
        // A plane at each fix's speed that turns at most as fast as a 25 degree bank allows, steered by the route.
        var route = new Route(Approach.Find(name)!);
        const double dt = 0.5;
        var speed = 120.0;
        for (var t = 0; t < 4000 && route.Step(along, cross, speed) is { } target; t++)
        {
            speed = target.Speed;
            var maxTurn = 9.81 * Math.Tan(25 * Math.PI / 180) / speed * 180 / Math.PI * dt;
            var turn = Math.Clamp(Pilot.Wrap(target.Heading - heading), -maxTurn, maxTurn);
            heading = (heading + turn + 360) % 360;
            var rel = (heading - Runway.Ksc.Heading) * Math.PI / 180;
            along += speed * Math.Cos(rel) * dt;
            cross += speed * Math.Sin(rel) * dt;
        }
        Assert.True(route.Done, "flew all the fixes");
        Assert.InRange(along, -9000, -6500); // at the final fix, 8 km out (handed over 600 m before it)
        Assert.InRange(cross, -700, 700);
        Assert.InRange(Pilot.Wrap(heading - 90), -50, 50); // roughly facing the runway: the final's steering does the rest
    }

    [Fact]
    public void Lined_up_on_the_slope_it_skips_the_route()
    {
        var landing = new Landing(Runway.Ksc);
        var slope = 69 + 5000 * Math.Tan(4 * Math.PI / 180);
        var step = landing.Step(600_000, new Geo(-0.0486, -74.7244 - 4600 / (600_000 * Math.PI / 180), slope), 90, 90, slope, false);
        Assert.Equal(LandingPhase.Final, step.Phase);
        Assert.Null(landing.Approach);
    }

    [Fact]
    public void Rollout_steering_fades_out_and_brakes_wait_for_a_straight_nose()
    {
        Assert.Equal(0, Landing.Rollout(speed: 3, headingError: 20, cross: 10).Steer); // walking speed: no steering
        Assert.True(Landing.Rollout(3, 20, 10).Brakes); // ...and brakes to stop
        Assert.False(Landing.Rollout(40, 15, 0).Brakes); // swinging hard: no brakes yet
        Assert.True(Landing.Rollout(40, 6, 0).Brakes); // a little off: brake (or it runs off the end)
        Assert.False(Landing.Rollout(40, 2, 0, yawRate: 12).Brakes); // the nose swinging: not yet
        Assert.False(Landing.Rollout(60, 0, 0).Brakes); // too fast for brakes
        // ...unless the runway left is short for this plane's brakes (the rig's Aeris 4A: 89 m/s, 8.4 m/s², 1.4 km left).
        Assert.True(Landing.Rollout(89, 1, 0, remaining: 1300, braking: 8.4).Brakes);
        Assert.False(Landing.Rollout(89, 1, 0, remaining: 2500, braking: 8.4).Brakes);
        Assert.False(Landing.Rollout(89, 12, 0, remaining: 1300, braking: 8.4).Brakes); // swinging: still not
        Assert.False(Landing.Rollout(89, 7, 0, remaining: 1300, braking: 8.4).Brakes); // fast, steadier needed than slow
        Assert.False(Landing.Rollout(89, 1, 0, yawRate: 7, remaining: 1300, braking: 8.4).Brakes);
        // Can't stop in what's left and fast enough to fly: a touch-and-go; slow or with little runway left: brake.
        Assert.True(Landing.TouchAndGo(85, 900, 8.4, 74));
        Assert.False(Landing.TouchAndGo(85, 1500, 8.4, 74));
        Assert.False(Landing.TouchAndGo(70, 900, 8.4, 74));
        Assert.False(Landing.TouchAndGo(85, 250, 8.4, 74));
        var mid = Math.Abs(Landing.Rollout(20, 2, 0).Steer);
        Assert.True(Math.Abs(Landing.Rollout(8, 2, 0).Steer) < mid, "gentler when slow");
        Assert.True(Math.Abs(Landing.Rollout(60, 2, 0).Steer) < mid / 2, "and much gentler when fast (the ground loop)");
        // Swinging right fast, though pointing straight: against the swing, wheel left (positive) and rudder left (negative).
        var (steer, rudder, _) = Landing.Rollout(40, 0, 0, yawRate: 20);
        Assert.True(steer > 0, $"wheel against the swing: {steer}");
        Assert.True(rudder < 0, $"rudder against the swing: {rudder}");
        // Pointing left of the runway (needs a right turn): wheel right (negative), rudder right (positive).
        var (steerRight, rudderRight, _) = Landing.Rollout(40, 10, 0);
        Assert.True(steerRight < 0 && rudderRight > 0, $"turns right: {steerRight}, {rudderRight}");
    }
}

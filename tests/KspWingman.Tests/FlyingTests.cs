// KSP Wingman · tests/KspWingman.Tests/FlyingTests.cs
// The pilot's control law and the formation arithmetic, without KSP.
// Guide:   Meat and Potatoes/09-Tests-And-Tools.md

using KspWingman.Mcp;

namespace KspWingman.Tests;

public class FlyingTests
{
    private const double Kerbin = 600_000;

    [Fact]
    public void Wraps_angles_to_the_short_way_round()
    {
        Assert.Equal(-20, Pilot.Wrap(340), 6);
        Assert.Equal(10, Pilot.Wrap(-350), 6);
        Assert.Equal(-180, Pilot.Wrap(180), 6);
    }

    [Fact]
    public void Turns_toward_the_heading_by_banking_and_climbs_toward_the_altitude()
    {
        var pilot = new Pilot();
        var level = new PlaneState(Pitch: 2, Roll: 0, Heading: 90, VerticalSpeed: 0, Altitude: 500, Speed: 150);
        var inputs = pilot.Step(level, new PlaneTarget(Heading: 180, Altitude: 800, Speed: 150), 0.05);
        Assert.True(inputs.Roll > 0, "a right turn rolls right");
        Assert.True(inputs.Pitch > 0, "climbing pulls the nose up");
        Assert.InRange(inputs.Pitch, -Pilot.MaxSurface, Pilot.MaxSurface);
        Assert.InRange(inputs.Roll, -Pilot.MaxSurface, Pilot.MaxSurface);

        var left = new Pilot().Step(level with { Heading = 10 }, new PlaneTarget(300, 500, 150), 0.05);
        Assert.True(left.Roll < 0, "350° → 300° is a left turn");
    }

    [Fact]
    public void Pulls_up_when_low_and_descending_and_softens_at_high_speed()
    {
        var target = new PlaneTarget(Heading: 90, Altitude: 50, Speed: 150); // asked to go down to 50 m
        var low = new PlaneState(Pitch: -5, Roll: 20, Heading: 90, VerticalSpeed: -10, Altitude: 120, Speed: 150, HeightAboveGround: 100);
        var inputs = new Pilot().Step(low, target, 0.05);
        Assert.True(inputs.Pitch > 0, "pulls up");
        Assert.True(inputs.Roll < 0, "levels the wings");

        var cruise = new PlaneState(Pitch: 0, Roll: 0, Heading: 90, VerticalSpeed: 0, Altitude: 1000, Speed: 150);
        var up = new PlaneTarget(90, 1500, 150);
        var slow = new Pilot().Step(cruise, up, 0.05).Pitch;
        var fast = new Pilot().Step(cruise with { Speed = 300 }, up, 0.05).Pitch;
        Assert.True(fast < slow / 2, $"twice the speed, much less stick: {fast} vs {slow}");
    }

    [Fact]
    public void Slows_down_for_a_big_turn()
    {
        var fast = new PlaneState(Pitch: 2, Roll: 0, Heading: 0, VerticalSpeed: 0, Altitude: 1500, Speed: 250);
        var straight = new Pilot().Step(fast, new PlaneTarget(5, 1500, 250), 0.5).Throttle;
        var turning = new Pilot().Step(fast, new PlaneTarget(90, 1500, 250), 0.5).Throttle;
        Assert.True(turning < straight, $"throttle back in the turn: {turning} vs {straight}");
    }

    [Fact]
    public void Formation_puts_the_slot_right_and_behind_the_leader()
    {
        // The leader flies east at 150 m/s (1 s apart); the agent is exactly in the slot (40 m right = south, 50 m behind).
        var formation = new Formation();
        var leader0 = new Geo(0, -74.6, 1000);
        var metersPerDegree = Kerbin * Math.PI / 180;
        var leader1 = leader0 with { Longitude = leader0.Longitude + 150 / metersPerDegree };
        var agent = new Geo(-40 / metersPerDegree, leader1.Longitude - 50 / metersPerDegree, 1000);
        Assert.Null(formation.Step(Kerbin, agent, leader0, 1, new Slot(40, 50, 0))); // one point: no heading yet
        var step = formation.Step(Kerbin, agent, leader1, 1, new Slot(40, 50, 0));
        // One move of 150 m in 1 s, smoothed at 0.3: the leader seems to fly 45 m/s east, so the agent is told to go east.
        Assert.NotNull(step);
        Assert.InRange(step!.Value.Target.Heading, 85, 95);
        Assert.True(step.Value.SlotDistance < 1, $"in the slot: {step.Value.SlotDistance}");
        Assert.Equal(1000, step.Value.Target.Altitude, 6);
    }

    [Fact]
    public void Formation_never_lets_the_agent_get_closer_than_the_separation()
    {
        var formation = new Formation();
        var metersPerDegree = Kerbin * Math.PI / 180;
        var leader0 = new Geo(0, 0, 1000);
        var leader1 = leader0 with { Longitude = 100 / metersPerDegree };
        var tooClose = new Geo(0, leader1.Longitude - 10 / metersPerDegree, 1000); // 10 m behind them
        formation.Step(Kerbin, tooClose, leader0, 1, new Slot(0, 5, 0));
        var step = formation.Step(Kerbin, tooClose, leader1, 1, new Slot(0, 5, 0))!.Value;
        Assert.True(step.Target.Altitude >= 1000 + Formation.MinSeparation, "climbs above them");
    }

    [Fact]
    public void Formation_flies_straight_at_a_far_slot_even_head_on()
    {
        // The leader flies east; the agent is 10 km ahead of them and 5 km north: it turns toward them, not east.
        var formation = new Formation();
        var metersPerDegree = Kerbin * Math.PI / 180;
        var leader0 = new Geo(0, 0, 1000);
        var leader1 = leader0 with { Longitude = 150 / metersPerDegree };
        var agent = new Geo(5000 / metersPerDegree, leader1.Longitude + 10000 / metersPerDegree, 1000);
        formation.Step(Kerbin, agent, leader0, 1, new Slot(40, 50, 0));
        var step = formation.Step(Kerbin, agent, leader1, 1, new Slot(40, 50, 0))!.Value;
        Assert.InRange(step.Target.Heading, 235, 250); // south-west, at the slot
        Assert.True(step.Target.Speed <= Formation.MaxSpeed);

        // Far behind them: as fast as allowed, never more.
        formation = new Formation();
        var behind = new Geo(0, leader0.Longitude - 20000 / metersPerDegree, 1000);
        formation.Step(Kerbin, behind, leader0, 1, new Slot(40, 50, 0));
        Assert.Equal(Formation.MaxSpeed, formation.Step(Kerbin, behind, leader1, 1, new Slot(40, 50, 0))!.Value.Target.Speed);
    }
}

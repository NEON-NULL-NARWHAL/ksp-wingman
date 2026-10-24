// KSP Wingman · tests/KspWingman.Tests/LandingTests.cs
// The landing geometry: getting to the final approach, the glide slope, the flare, going around.

using KspWingman.Mcp;

namespace KspWingman.Tests;

public sealed class LandingTests
{
    private const double Kerbin = 600_000;
    private static readonly double MetersPerDegree = Kerbin * Math.PI / 180;

    /// <summary>A point <paramref name="along"/> m east of KSC's threshold and <paramref name="south"/> m south of it.</summary>
    private static Geo AtRunway(double along, double south, double altitude) =>
        new(Runway.Ksc.Threshold.Latitude - south / MetersPerDegree, Runway.Ksc.Threshold.Longitude + along / MetersPerDegree, altitude);

    [Fact]
    public void Heads_for_the_final_approach_from_the_far_side()
    {
        // 20 km east of the runway, flying east: it turns back west, toward the start of the final 8 km out to sea.
        var step = new Landing(Runway.Ksc).Step(Kerbin, AtRunway(20_000, 0, 1500), heading: 90, speed: 150, heightAboveGround: 1430, onGround: false);
        Assert.Equal(LandingPhase.ToFinal, step.Phase);
        Assert.InRange(step.Target!.Value.Heading, 250, 280); // west, to the south downwind (the tower's pick)
    }

    [Fact]
    public void Follows_the_glide_slope_on_final()
    {
        // 5 km before touchdown, lined up, on the slope (69 m + 5 km × tan 4° ≈ 419 m): sinks about 6 m/s at 90 m/s.
        var landing = new Landing(Runway.Ksc);
        var onSlope = 69 + 5000 * Math.Tan(4 * Math.PI / 180);
        var step = landing.Step(Kerbin, AtRunway(Landing.TouchdownAfterThreshold - 5000, 0, onSlope), 90, 90, onSlope, false);
        Assert.Equal(LandingPhase.Final, step.Phase);
        Assert.InRange(step.Target!.Value.ClimbRate!.Value, -7.5, -5);
        Assert.InRange(step.Target.Value.Heading, 88, 92);
        Assert.True(step.Target.Value.Landing);

        // 50 m right of the centerline (south, landing east): steers left, north of east.
        var right = landing.Step(Kerbin, AtRunway(Landing.TouchdownAfterThreshold - 5000, 50, onSlope), 90, 90, onSlope, false);
        Assert.True(right.Cross > 40);
        Assert.True(right.Target!.Value.Heading < 90, $"turns left: {right.Target.Value.Heading}");
    }

    [Fact]
    public void Flares_near_the_ground_and_goes_around_when_off_the_centerline()
    {
        var landing = new Landing(Runway.Ksc);
        landing.Step(Kerbin, AtRunway(-100, 0, 100), 90, 80, 30, false); // on final, on the slope
        var flare = landing.Step(Kerbin, AtRunway(350, 0, 75), 90, 75, 6, false);
        Assert.Equal(LandingPhase.Flare, flare.Phase);
        Assert.Equal(-1, flare.Target!.Value.ClimbRate);
        Assert.Equal(0, flare.Target.Value.Speed); // throttle shut

        var wide = new Landing(Runway.Ksc);
        wide.Step(Kerbin, AtRunway(-2000, 0, 300), 90, 80, 230, false);
        var goAround = wide.Step(Kerbin, AtRunway(-500, 300, 150), 90, 80, 80, false);
        Assert.Equal(LandingPhase.GoAround, goAround.Phase);
        Assert.True(goAround.Target!.Value.ClimbRate > 0, "climbs away");
    }

    [Fact]
    public void Flies_a_circuit_instead_of_turning_onto_final_at_the_last_moment()
    {
        // Over the runway, 500 m south (right of the centerline landing east): out west on the south side first...
        var landing = new Landing(Runway.Ksc, "visual");
        var first = landing.Step(Kerbin, AtRunway(1000, 500, 600), heading: 90, speed: 120, heightAboveGround: 530, onGround: false);
        Assert.Equal(LandingPhase.ToFinal, first.Phase);
        Assert.InRange(first.Target!.Value.Heading, 255, 275); // a little south of west, toward the side point

        // ...then, out past the start of the final and turning back east, in toward it.
        var back = landing.Step(Kerbin, AtRunway(Landing.TouchdownAfterThreshold - 10_000, 2500, 600), heading: 30, speed: 120, heightAboveGround: 530, onGround: false);
        Assert.Equal(LandingPhase.ToFinal, back.Phase);
        Assert.InRange(back.Target!.Value.Heading, 30, 75); // north-east, toward the centerline 8 km out

        // Turned back in and facing the runway, though not at the point: that's the final now, no chasing the point.
        var facing = landing.Step(Kerbin, AtRunway(Landing.TouchdownAfterThreshold - 9500, 1800, 600), heading: 70, speed: 120, heightAboveGround: 530, onGround: false);
        Assert.Equal(LandingPhase.Final, facing.Phase);
        landing = new Landing(Runway.Ksc, "visual");
        landing.Step(Kerbin, AtRunway(Landing.TouchdownAfterThreshold - 10_000, 2500, 600), heading: 30, speed: 120, heightAboveGround: 530, onGround: false);

        // Back inside the line again on the way in: still heading in, not out to the side point again.
        var inside = landing.Step(Kerbin, AtRunway(Landing.TouchdownAfterThreshold - 8200, 1200, 600), heading: 45, speed: 120, heightAboveGround: 530, onGround: false);
        Assert.InRange(inside.Target!.Value.Heading, 0, 90);
    }

    [Fact]
    public void Risks_the_landing_until_the_decision_point()
    {
        var onSlope = (double along) => 69 + Math.Max(0, -along) * Math.Tan(4 * Math.PI / 180);
        // 1.4 km out and 200 m off the centerline (the rig's Aeris 4A went around here three times): it keeps correcting.
        var risky = new Landing(Runway.Ksc);
        risky.Step(Kerbin, AtRunway(Landing.TouchdownAfterThreshold - 3000, 0, onSlope(-3000)), 90, 100, 200, false);
        var still = risky.Step(Kerbin, AtRunway(Landing.TouchdownAfterThreshold - 1400, 200, onSlope(-1400)), 80, 100, 100, false);
        Assert.Equal(LandingPhase.Final, still.Phase);
        // At the decision point still 100 m off: around it goes.
        var late = risky.Step(Kerbin, AtRunway(Landing.TouchdownAfterThreshold - 500, 100, onSlope(-500)), 80, 100, 35, false);
        Assert.Equal(LandingPhase.GoAround, late.Phase);
        // Lined up there and on the slope: it lands.
        var good = new Landing(Runway.Ksc);
        good.Step(Kerbin, AtRunway(Landing.TouchdownAfterThreshold - 3000, 0, onSlope(-3000)), 90, 100, 200, false);
        Assert.Equal(LandingPhase.Final, good.Step(Kerbin, AtRunway(Landing.TouchdownAfterThreshold - 500, 30, onSlope(-500) + 20), 90, 80, 55, false).Phase);
    }

    [Fact]
    public void Goes_around_when_the_ground_comes_up_far_out()
    {
        // 4 km out, on the slope by altitude, but a hill 60 m below: climb away, don't land on the hill.
        var landing = new Landing(Runway.Ksc);
        var onSlope = 69 + (4000 + 0) * Math.Tan(4 * Math.PI / 180);
        var step = landing.Step(Kerbin, AtRunway(Landing.TouchdownAfterThreshold - 4000, 0, onSlope), 90, 85, 60, false);
        Assert.Equal(LandingPhase.GoAround, step.Phase);
    }

    [Fact]
    public void Rolls_out_once_on_the_ground()
    {
        var step = new Landing(Runway.Ksc).Step(Kerbin, AtRunway(600, 5, 70), 90, 60, 0, onGround: true);
        Assert.Equal(LandingPhase.Rollout, step.Phase);
        Assert.Null(step.Target);
    }
}

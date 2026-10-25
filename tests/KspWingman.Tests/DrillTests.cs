// KSP Wingman · tests/KspWingman.Tests/DrillTests.cs
// The free-will drills and their limits: explore's legs (range, fuel), the fuel gauge, the planner's answers, each
// plane's final approach limits, and a steep final flown level until it meets its slope.
// Guide:   Meat and Potatoes/09-Tests-And-Tools.md

using KspWingman.Mcp;
using KspWingman.Tui;

namespace KspWingman.Tests;

public sealed class DrillTests
{
    private const double Kerbin = 600_000;
    private static readonly Geo Ksc = Runway.Ksc.Threshold with { Altitude = 1500 };

    [Fact]
    public void Explore_legs_stay_in_range_and_keep_fuel_to_get_home()
    {
        var north20 = Exploring.Move(Kerbin, Ksc, 0, 20, 1500);
        Assert.InRange(Exploring.DistanceKm(Kerbin, Ksc, north20), 19.9, 20.1);
        Assert.Equal("20 km north of KSC", Exploring.FromKsc(Kerbin, north20.Latitude, north20.Longitude));
        var full = new FuelState(0.9, 0.01); // 1% a minute: 90 minutes left
        Assert.Null(Exploring.Check(Kerbin, Ksc, north20, 150, full));
        Assert.Contains("30 km at most", Exploring.Check(Kerbin, Ksc, Exploring.Move(Kerbin, Ksc, 0, 40, 1500), 150, full));
        var far = Exploring.Move(Kerbin, Ksc, 90, 55, 1500);
        Assert.Contains("within 60 km", Exploring.Check(Kerbin, far, Exploring.Move(Kerbin, far, 90, 20, 1500), 150, full));
        // Little fuel: there and back would leave less than the reserve.
        Assert.Contains("keep 25%", Exploring.Check(Kerbin, Ksc, north20, 150, new FuelState(0.3, 0.02)));
        // The burn not known yet (the first minutes): close to KSC only.
        Assert.Contains("isn't known yet", Exploring.Check(Kerbin, north20, Exploring.Move(Kerbin, north20, 0, 15, 1500), 150, new FuelState(0.9, null)));
        Assert.Null(Exploring.Check(Kerbin, Ksc, north20, 150, null)); // no tanks at all: no fuel rule
    }

    [Fact]
    public void The_fuel_gauge_measures_the_burn_and_starts_over_after_refuelling()
    {
        var gauge = new FuelGauge();
        gauge.Update(1.0, 0);
        Assert.Null(gauge.Now!.Value.PerMinute); // too soon
        gauge.Update(0.98, 60);
        Assert.Equal(0.02, gauge.Now!.Value.PerMinute!.Value, 3);
        Assert.Equal(49, gauge.Now.Value.Endurance!.Value, 0);
        Assert.Contains("98% fuel, about 49 min", gauge.Now.Value.Describe());
        gauge.Update(1.0, 61); // refuelled (a new plane)
        Assert.Null(gauge.Now!.Value.PerMinute);
        gauge.Update(null, 62); // no tanks
        Assert.Null(gauge.Now);
        Assert.Equal(0.63, Learner.FuelLeft("Pilot: Exploring.\nFuel: 63% fuel, about 20 min at this burn (3.1% a minute)"));
    }

    [Fact]
    public void The_planner_s_choices_are_read_and_checked()
    {
        Assert.Equal((45, 12, 2000), Drills.ParseLeg("GO 45 12 2000 - the hills north-east nobody explored"));
        Assert.Equal((315, 8.5, 1500), Drills.ParseLeg("I'd say GO 315° 8.5 km")); // no altitude: 1,500 m
        Assert.Equal((90, 5, 1200), Drills.ParseLeg("GO -270 5 1200"));
        Assert.Null(Drills.ParseLeg("HOME, fuel is low"));
        Assert.True(Drills.Home("HOME - fuel is low"));
        Assert.Null(Drills.ParseLeg("GO north for a while")); // not a leg: home

        Assert.Equal("steep", Drills.ParseStyle("STYLE steep - it slows well", []));
        Assert.Equal("shallow", Drills.ParseStyle("I think a nice one", ["steep"])); // no answer: the first not tried
        const string status = "Limits for the Aeris 4A: final approach 100-135 m/s, touchdown 100 m/s, glide slope 3-6°";
        Assert.Equal((100.0, 135.0), Drills.Limits(status));
        Assert.Equal(135.0, Drills.LandArguments("fast", status)["finalSpeed"]);
        Assert.Equal(6.0, Drills.LandArguments("steep", status)["glideSlope"]);
        Assert.Equal("RNAV 09 STRAIGHT-IN", Drills.LandArguments("long", status)["approach"]);
        Assert.Contains("Limits for the Aeris 4A", Drills.StylePrompt("Vessel: Aeris 4A (Flying)\n" + status, ["fast"]));
    }

    [Fact]
    public void Each_plane_s_final_approach_limits_fit_what_the_agent_asks()
    {
        // The Aeris 3A (slows 1.2 m/s² at idle) and the Aeris 4A (1.8): the 4A may come in steeper and faster.
        var aeris = PlaneSetup.From(new PlaneTest("Aeris 3A", 55, 410, 2.2, 54, 1.2, 7.2, 0.1));
        var heavy = PlaneSetup.From(new PlaneTest("Aeris 4A", 37, 219, 2.1, 74, 1.8, 6.3, 0.19));
        Assert.Equal(4.5, aeris.MaxGlideSlope); // it stayed 40 m above a 5° slope on every try in the rig
        Assert.Equal(6, heavy.MaxGlideSlope);
        Assert.True(heavy.MaxFinalSpeed > heavy.ApproachSpeed + PlaneSetup.FastFinal);
        var fitted = aeris.Fit(new ApproachStyle(FinalSpeed: 300, GlideSlope: 9));
        Assert.Equal((aeris.MaxFinalSpeed, 4.5), (fitted.FinalSpeed, fitted.GlideSlope));
        Assert.Equal("steep 4.5°, final " + aeris.MaxFinalSpeed + " m/s", fitted.Describe());
        Assert.Contains("glide slope 3-4.5°", aeris.Limits());
        // Its card learns from its finals: a 6° final that arrived 45 m high caps the 4A's slope at 5.5°; 4° finals don't.
        var learned = PlaneSetup.From(new PlaneTest("Aeris 4A", 37, 219, 2.1, 74, 1.8, 6.3, 0.19), [new FinalReport(0, 45, 6), new FinalReport(0, 45)]);
        Assert.Equal(5.5, learned.MaxGlideSlope);
        Assert.Contains("1 steep final too high", learned.Why);
        Assert.Contains("1 high final", learned.Why); // the 4° one counts as an ordinary high final
        // A steeper slope leaves less to slow with: slower far out on it; a slow final stays at the landing speed.
        Assert.True(heavy.FinalSpeed(6000, new ApproachStyle(GlideSlope: 6)) < heavy.FinalSpeed(6000));
        Assert.Equal(heavy.LandingSpeed, heavy.FinalSpeed(6000, new ApproachStyle(FinalSpeed: heavy.MinFinalSpeed)));
    }

    [Fact]
    public void A_steep_final_flies_level_until_it_meets_its_slope()
    {
        const double perDegree = Kerbin * Math.PI / 180;
        Geo At(double along, double altitude) =>
            new(Runway.Ksc.Threshold.Latitude, Runway.Ksc.Threshold.Longitude + (along + Landing.TouchdownAfterThreshold) / perDegree, altitude);
        var setup = PlaneSetup.From(new PlaneTest("Aeris 4A", 37, 219, 2.1, 74, 1.8, 6.3, 0.19));
        var steep = new Landing(Runway.Ksc, "visual", setup: setup, style: new ApproachStyle(GlideSlope: 6));
        // 7 km out at the 4° path's height (69 + 7000 × tan 4° ≈ 559 m): under the 6° path, so level, never climbing.
        var onFour = 69 + 7000 * Math.Tan(4 * Math.PI / 180);
        var below = steep.Step(Kerbin, At(-7000, onFour), heading: 90, speed: 110, heightAboveGround: onFour - 69, onGround: false);
        Assert.Equal(LandingPhase.Final, below.Phase);
        Assert.True(below.Target!.Value.ClimbRate <= 0, $"climbs {below.Target.Value.ClimbRate} m/s");
        // On the 6° path: down it, faster than the 4° one would sink.
        var onSix = 69 + 4000 * Math.Tan(6 * Math.PI / 180);
        var on = steep.Step(Kerbin, At(-4000, onSix), heading: 90, speed: 100, heightAboveGround: onSix - 69, onGround: false);
        Assert.True(on.Target!.Value.ClimbRate < -100 * Math.Tan(5 * Math.PI / 180));
        Assert.Equal("steep 6°", steep.Style!.Describe());
    }
}

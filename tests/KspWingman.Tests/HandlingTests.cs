// KSP Wingman · tests/KspWingman.Tests/HandlingTests.cs
// Each plane's own setup: the flight test flown on a simulated plane (it must measure what the simulation is), the
// setup worked out from it, and the corrections from how its finals went.
// Guide:   Meat and Potatoes/09-Tests-And-Tools.md

using System.Text.Json.Nodes;
using KspWingman.Mcp;

namespace KspWingman.Tests;

public sealed class HandlingTests
{
    /// <summary>
    /// A toy plane, enough for the test card: the stick sets pitch and roll rates (with the square of the speed, damped
    /// fast), it needs more angle of attack the slower it flies (it can't hold its height past 15°), and drag goes with
    /// the speed squared. Its true numbers: pitch 110 °/s and roll 300 °/s per full stick (the Aeris 3A: 55 and 410) at 150 m/s, slowest about 55 m/s.
    /// </summary>
    private sealed class ToyPlane
    {
        public const double PitchPerStick = 110, RollPerStick = 300, Damping = 6, AlphaAt150 = 2, MaxThrust = 15, Drag = 0.0002;

        /// <summary>How quickly the roll rate follows the stick (per second): lower is a heavier, laggier plane.</summary>
        public double RollDamping = Damping;
        public double Pitch = 2, Roll, Heading = 90, Speed = 150, Altitude = 1600, PitchRate, RollRate, VerticalSpeed;

        /// <summary>Stick it takes to hold the nose where it is (a nose-heavy plane: positive).</summary>
        public double NoseHeavy;

        public PlaneState State => new(Pitch, Roll, Heading, VerticalSpeed, Altitude, Speed, Altitude - 70);

        public void Step(PlaneInputs u, double dt)
        {
            var q = Math.Pow(Speed / 150, 2);
            PitchRate += (Damping * PitchPerStick * (u.Pitch - NoseHeavy) * q - Damping * PitchRate) * dt;
            RollRate += (RollDamping * RollPerStick * u.Roll * q - RollDamping * RollRate) * dt;
            Pitch += PitchRate * dt;
            Roll = Math.Clamp(Roll + RollRate * dt, -80, 80);
            var alpha = Math.Min(AlphaAt150 * Math.Pow(150 / Speed, 2), 15);
            var lift = AlphaAt150 * Math.Pow(150 / Speed, 2) > 15 ? -3 : 0; // past 15° it stalls: sinks
            var gamma = Pitch - alpha;
            VerticalSpeed = Speed * Math.Sin(gamma * Math.PI / 180) + lift;
            Altitude += VerticalSpeed * dt;
            Heading = (Heading + 9.81 * Math.Tan(Roll * Math.PI / 180) / Speed * 180 / Math.PI * dt + 360) % 360;
            Speed = Math.Max(20, Speed + (MaxThrust * u.Throttle - Drag * Speed * Speed - 9.81 * Math.Sin(gamma * Math.PI / 180)) * dt);
        }
    }

    [Fact]
    public void The_flight_test_measures_a_simulated_plane()
    {
        var plane = new ToyPlane();
        var test = new FlightTest("Toy");
        var pilot = new Pilot();
        var last = new PlaneInputs(0, 0, 0, 0.3f);
        const double dt = 0.05;
        var steps = new List<TestStep>();
        for (var t = 0.0; t < 600 && !test.Finished; t += dt)
        {
            var command = test.Next(plane.State, t, last);
            if (steps.Count == 0 || steps[^1] != test.Step)
                steps.Add(test.Step);
            var flown = pilot.Step(plane.State, command.Target ?? new PlaneTarget(plane.Heading, plane.Altitude, plane.Speed), dt);
            last = command.Inputs ?? flown;
            plane.Step(last, dt);
        }

        Assert.Null(test.Problem);
        Assert.Equal([TestStep.Settle, TestStep.PitchPulse, TestStep.PitchRecover, TestStep.RollPulse, TestStep.RollRecover, TestStep.Turn,
            TestStep.Slow, TestStep.Accelerate, TestStep.Done], steps);
        var r = test.Result!;
        // What the simulation is (the rate builds up over the first ~0.2 s of the pulse, so a little under).
        Assert.InRange(r.PitchRate, ToyPlane.PitchPerStick * 0.75, ToyPlane.PitchPerStick * 1.05);
        Assert.InRange(r.RollRate, ToyPlane.RollPerStick * 0.75, ToyPlane.RollPerStick * 1.05);
        Assert.InRange(r.MinSpeed, 50, 62); // 15° of angle of attack at 150 × √(2/15) ≈ 55 m/s
        Assert.InRange(r.TurnRate, 1.8, 2.6); // 30° of bank at 150 m/s: 2.2 °/s
        Assert.InRange(r.CruiseThrottle, 0.25, 0.35); // drag at 150 m/s is 4.5 m/s² of 15
        Assert.True(r.Deceleration > 1 && r.Acceleration > 1);
        Assert.Contains("× the Aeris 3A", r.Describe());

        // The setup from it: a quicker nose than the Aeris (less elevator), slower roll (more aileron), speeds from its slowest.
        var setup = PlaneSetup.From(r);
        Assert.InRange(setup.PitchGain, 0.5, 0.75);
        Assert.InRange(setup.RollGain, 1.4, 2.0);
        Assert.Equal(setup.LandingSpeed + 15, setup.ApproachSpeed);
        Assert.InRange(setup.LandingSpeed, 68, 84);
    }

    [Fact]
    public void A_nose_heavy_plane_is_trimmed_up_instead_of_sinking()
    {
        // Like the Aeris 4A in the rig: it needs 0.35 of stick held to fly level, more than a quarter stick plus 0.1 of trim.
        var plane = new ToyPlane { NoseHeavy = 0.35, Altitude = 600 };
        var pilot = new Pilot();
        var target = new PlaneTarget(90, 600, 150);
        var lowest = plane.Altitude;
        for (var t = 0.0; t < 90; t += 0.05)
        {
            plane.Step(pilot.Step(plane.State, target, 0.05), 0.05);
            lowest = Math.Min(lowest, plane.Altitude);
        }
        Assert.InRange(plane.Altitude, 560, 640); // holding its height after a minute and a half
        Assert.True(lowest > 450, $"it sank to {lowest:0} m");
    }

    [Fact]
    public void The_ground_test_measures_a_simulated_nose_wheel()
    {
        // A toy plane on the runway: the nose wheel turns it 39 °/s per full lock at 10 m/s (1.5× the Aeris 3A) (KSP's sign: positive turns
        // left), it veers 0.8 °/s right by itself, full throttle gives 3 m/s², the brakes 4 m/s².
        const double turn = 39, veer = 0.8, dt = 0.05;
        var (heading, speed) = (90.0, 0.0);
        var test = new GroundTest("Toy");
        var steps = new List<GroundStep>();
        for (var t = 0.0; t < 150 && !test.Finished; t += dt)
        {
            var c = test.Next(heading, speed, t);
            if (steps.Count == 0 || steps[^1] != test.Step)
                steps.Add(test.Step);
            heading = (heading + (-c.Steer * turn * speed / 10 + (speed > 1 ? veer : 0)) * dt + 360) % 360;
            speed = Math.Max(0, speed + (3 * c.Throttle - (c.Brakes ? 4 : 0) - 0.05) * dt);
        }
        Assert.Null(test.Problem);
        Assert.Equal([GroundStep.Roll, GroundStep.Recover, GroundStep.Pulse, GroundStep.Recover, GroundStep.Brake, GroundStep.Done], steps);
        var r = test.Result!.Value;
        Assert.InRange(r.SteerRate, turn * 0.85, turn * 1.15);
        Assert.InRange(r.Drift, veer * 0.8, veer * 1.2);
        Assert.InRange(r.Braking, 3.2, 4.6);
        // A nose wheel 1.5× the Aeris's: two thirds of the steering, in the setup and its description.
        var setup = PlaneSetup.Default.WithGround(r);
        Assert.InRange(setup.SteerGain, 0.55, 0.8);
        Assert.Contains("nose wheel ×0.", setup.Describe());
        Assert.Contains("veers 0.8 °/s right", test.Describe(r));
    }

    [Fact]
    public void The_ground_test_stops_when_the_plane_swings_off_the_runway()
    {
        var test = new GroundTest("Toy");
        test.Next(90, 0, 0);
        var c = test.Next(120, 8, 5);
        Assert.Equal(GroundStep.Stopped, test.Step);
        Assert.True(c.Brakes);
        Assert.Equal(0, c.Throttle);
        Assert.Contains("swung 30°", c.Doing);
    }

    [Fact]
    public void The_flight_test_stops_itself_when_too_low()
    {
        var test = new FlightTest("Toy");
        test.Next(new PlaneState(2, 0, 90, 0, 1600, 150, 1530), 0, default);
        test.Next(new PlaneState(2, 0, 90, 0, 1600, 150, 1530), 130, default); // two minutes unsteady: on to the pulses
        var command = test.Next(new PlaneState(-10, 0, 90, -40, 400, 150, 330), 131, default);
        Assert.Equal(TestStep.Stopped, test.Step);
        Assert.Contains("too low (330 m", command.Doing);
        Assert.NotNull(command.Target); // and it flies on (level, back up) rather than letting go
    }

    [Fact]
    public void Setups_come_from_the_test_and_are_corrected_by_the_finals()
    {
        Assert.Equal(PlaneSetup.Default, PlaneSetup.From(null));
        var heavy = new PlaneTest("Albatross", PitchRate: 27.5, RollRate: 820, TurnRate: 2, MinSpeed: 70, Deceleration: 0.5, Acceleration: 2, CruiseThrottle: 0.45);
        var setup = PlaneSetup.From(heavy);
        Assert.Equal((2.0, 0.5), (setup.PitchGain, setup.RollGain));
        Assert.Equal((70.0, 94.0, 109.0), (setup.RotateSpeed, setup.LandingSpeed, setup.ApproachSpeed));
        Assert.Equal(119, setup.CircuitSpeed); // hard to slow down: its landing speed + 25 (1.35 × 70 = 94.5, rounds to 94)

        // Two weaving finals: softer ailerons; one high: a slower circuit and a harder sink back onto the slope.
        var corrected = PlaneSetup.From(heavy, [new FinalReport(3, 10), new FinalReport(2, 100), new FinalReport(0, 5)]);
        Assert.Equal(Math.Round(0.5 * 0.85 * 0.85, 2), corrected.RollGain);
        Assert.Equal(109, corrected.CircuitSpeed);
        Assert.Equal(7, corrected.SinkCorrection);
        Assert.Contains("2 weaving finals and 1 high final", corrected.Why);

        // Nothing odd past the limits: a wild test still gives a setup a plane can fly.
        var wild = PlaneSetup.From(new PlaneTest("X", 0, 100_000, 0, 500, 0, 0, 5));
        Assert.Equal((2.5, 0.4, 100.0, 90.0, 1.0), (wild.PitchGain, wild.RollGain, wild.LandingSpeed, wild.RotateSpeed, wild.CruiseThrottle));
    }

    [Fact]
    public void Planes_join_the_final_fast_and_slow_down_on_it_as_they_can()
    {
        // The Aeris 3A (slows 1.2 m/s² at idle): about 98 m/s 8 km out, its landing speed from 1.5 km.
        var aeris = PlaneSetup.From(new PlaneTest("Aeris 3A", 55, 410, 2.2, 54, 1.2, 7.2, 0.1));
        Assert.InRange(aeris.FinalSpeed(8000), 92, 100);
        Assert.Equal(aeris.LandingSpeed, aeris.FinalSpeed(1500));
        Assert.Equal(aeris.LandingSpeed, aeris.FinalSpeed(300));
        // The Aeris 4A (slowest 74 m/s, slows 1.8 m/s²): joins at its approach speed + 20, slows from further out.
        var fast = PlaneSetup.From(new PlaneTest("Aeris 4A", 37, 219, 2.1, 74, 1.8, 6.3, 0.19));
        Assert.Equal(fast.ApproachSpeed + PlaneSetup.FastFinal, fast.FinalSpeed(11_000));
        Assert.True(fast.FinalSpeed(4000) < fast.FinalSpeed(8000));
        Assert.Equal(100, fast.FinalSpeed(1500));
        // A slick plane that hardly slows can't join fast: its final speed stays near its landing speed.
        var slick = PlaneSetup.From(new PlaneTest("Slick", 55, 410, 2, 54, 0.7, 5, 0.1));
        Assert.True(slick.FinalSpeed(8000) < slick.LandingSpeed + 15);
    }

    [Fact]
    public void A_fast_heavy_plane_settles_onto_the_centerline_instead_of_snaking()
    {
        // Like the rig's Aeris 4A: 140 m/s joining the final 400 m off the centerline, slow to roll. Aiming 3 s ahead it
        // snaked ±400 m; the aim point now grows with the turn's width.
        const double kerbin = 600_000, perDegree = kerbin * Math.PI / 180, dt = 0.05;
        var setup = PlaneSetup.Default with { ApproachSpeed = 115, LandingSpeed = 100 };
        var plane = new ToyPlane { Speed = 140, RollDamping = 1.5, Heading = 90 };
        var tan = Math.Tan(Landing.GlideSlope * Math.PI / 180);
        var (along, cross) = (-8000.0, -400.0);
        plane.Altitude = 69 + 8000 * tan;
        var landing = new Landing(Runway.Ksc, "visual", setup: setup);
        var pilot = new Pilot(setup);
        var crossings = 0;
        var side = -1;
        double worst = 0;
        while (along < -800)
        {
            var geo = new Geo(Runway.Ksc.Threshold.Latitude - cross / perDegree,
                Runway.Ksc.Threshold.Longitude + (along + Landing.TouchdownAfterThreshold) / perDegree, plane.Altitude);
            var step = landing.Step(kerbin, geo, plane.Heading, plane.Speed, plane.Altitude - 69, onGround: false);
            Assert.NotEqual(LandingPhase.GoAround, step.Phase);
            plane.Step(pilot.Step(plane.State, step.Target!.Value, dt), dt);
            var h = (plane.Heading - 90) * Math.PI / 180;
            along += plane.Speed * Math.Cos(h) * dt;
            cross += plane.Speed * Math.Sin(h) * dt;
            if (along > -6000)
            {
                var now = cross > 25 ? 1 : cross < -25 ? -1 : 0;
                if (now != 0 && now != side)
                    crossings++;
                if (now != 0)
                    side = now;
                worst = Math.Max(worst, Math.Abs(cross));
            }
        }
        Assert.True(crossings <= 1, $"crossed the centerline {crossings} times, up to {worst:0} m off");
        Assert.True(Math.Abs(cross) < 60, $"{cross:0} m off at the decision point");
    }

    [Fact]
    public void A_final_reports_its_weaving_and_how_high_it_was()
    {
        const double kerbin = 600_000, perDegree = kerbin * Math.PI / 180;
        Geo At(double along, double south, double altitude) =>
            new(Runway.Ksc.Threshold.Latitude - south / perDegree, Runway.Ksc.Threshold.Longitude + (along + Landing.TouchdownAfterThreshold) / perDegree, altitude);
        var reports = new List<FinalReport>();
        var landing = new Landing(Runway.Ksc, "visual") { FinalEnded = reports.Add };
        var tan = Math.Tan(Landing.GlideSlope * Math.PI / 180);
        // Lined up 5 km out, swinging 40 m either side of the centerline, then 60 m high at the decision point: it goes around.
        var path = new (double Along, double Cross, double High)[] { (-5000, 40, 0), (-4500, -40, 0), (-4000, 40, 0), (-3500, -40, 0), (-2000, 10, 0), (-550, 0, 60) };
        foreach (var (along, cross, high) in path)
        {
            var altitude = Runway.Ksc.Threshold.Altitude - along * tan + high;
            landing.Step(kerbin, At(along, cross, altitude), heading: 90, speed: 85, heightAboveGround: altitude - 69, onGround: false);
        }
        Assert.Equal(LandingPhase.GoAround, landing.Phase);
        var report = Assert.Single(reports);
        Assert.Equal(3, report.Weaves);
        Assert.InRange(report.High, 55, 65);
    }

    [Fact]
    public void The_towers_numbers_are_checked_before_they_set_up_a_plane()
    {
        var good = JsonNode.Parse("""{"pitchRate":20,"rollRate":150,"turnRate":2.1,"minSpeed":55,"deceleration":1.5,"acceleration":3,"cruiseThrottle":0.2}""");
        Assert.Equal(55, TowerClient.ParseTest(good, "Aeris 3A")!.MinSpeed);
        Assert.Null(TowerClient.ParseTest(JsonNode.Parse("""{"pitchRate":-5,"rollRate":150,"minSpeed":55}"""), "X")); // out of range
        Assert.Null(TowerClient.ParseTest(JsonNode.Parse("""{"pitchRate":"fast","rollRate":150,"minSpeed":55}"""), "X"));
        Assert.Null(TowerClient.ParseTest(null, "X"));
        var finals = TowerClient.ParseFinals(JsonNode.Parse("""[{"weaves":3,"high":40},{"weaves":"x"},7]"""));
        Assert.Equal([new FinalReport(3, 40), new FinalReport(0, 0)], finals);
    }
}

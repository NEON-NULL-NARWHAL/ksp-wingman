// KSP Wingman · tests/FlightLab/Program.cs
// Development tool: flies the agent's KSP straight through kRPC and logs what the plane does, to tune the pilot.
// Usage: FlightLab <kRPC port> probe      (which way do the pitch and roll inputs move the plane?)
//        FlightLab <kRPC port> ground     (the ground test on the Aeris 3A: the nose wheel reference)
//        FlightLab <kRPC port> test       (the flight test card on the Aeris 3A: the reference numbers)
//        FlightLab <kRPC port> steps <rollGain> <pitchGain>   (30° heading steps; FLIGHTLAB_CRAFT picks the plane)
// Guide:   Meat and Potatoes/09-Tests-And-Tools.md

using System.Globalization;
using System.Net;
using KRPC.Client;
using KRPC.Client.Services.KRPC;
using KRPC.Client.Services.SpaceCenter;

var port = int.Parse(args[0], CultureInfo.InvariantCulture);
using var connection = new Connection("FlightLab", IPAddress.Loopback, port, port + 1);
var space = connection.SpaceCenter();
var krpc = connection.KRPC();
void Log(string text) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.f}] {text}");

// A fresh plane on the runway: kRPC can launch from the flight scene too (after a crash).
Vessel Fresh()
{
    space.LaunchVessel("SPH", Environment.GetEnvironmentVariable("FLIGHTLAB_CRAFT") ?? "Aeris 3A", "Runway", null, true, "");
    Thread.Sleep(3000);
    while (krpc.GameScene != GameScene.Flight) Thread.Sleep(500);
    Thread.Sleep(5000);
    return space.ActiveVessel;
}

if (args.Length > 4 && args[1] == "camera")
{
    // For screenshots without touching the window: FlightLab <port> camera <heading> <pitch> <distance>.
    var camera = space.Camera;
    camera.Mode = CameraMode.Locked;
    camera.Heading = float.Parse(args[2], CultureInfo.InvariantCulture);
    camera.Pitch = float.Parse(args[3], CultureInfo.InvariantCulture);
    camera.Distance = float.Parse(args[4], CultureInfo.InvariantCulture);
    Log($"camera {camera.Mode}: heading {camera.Heading:0}, pitch {camera.Pitch:0}, distance {camera.Distance:0}");
    return;
}

if (args.Length > 1 && args[1] == "show")
{
    // What the controls are set to right now, while something else flies.
    var v = space.ActiveVessel;
    var g = v.Flight(v.Orbit.Body.ReferenceFrame);
    for (var i = 0; i < 5; i++)
    {
        Log(string.Create(CultureInfo.InvariantCulture,
            $"throttle {v.Control.Throttle:0.00}, thrust {v.Thrust / 1000:0.0} kN, speed {g.Speed:0.0}, vs {g.VerticalSpeed:0.0}, pitch {v.Control.Pitch:0.00}, SAS {v.Control.SAS}, brakes {v.Control.Brakes}"));
        Thread.Sleep(1000);
    }
    return;
}

if (args.Length > 1 && args[1] == "wreck")
{
    // What kRPC says about the active vessel after a crash (it keeps answering with the last numbers).
    var wreck = space.ActiveVessel;
    Log($"{wreck.Name}: {wreck.Situation}, MET {wreck.MET:0.0}, UT {space.UT:0.0}");
    try { Log($"parts {wreck.Parts.All.Count}"); } catch (Exception e) { Log("parts: " + e.Message); }
    Thread.Sleep(2000);
    Log($"2 s later: MET {wreck.MET:0.0}, UT {space.UT:0.0}");
    return;
}

var vessel = Fresh();
Log($"on the runway: {vessel.Situation}");

if (args.Length > 1 && args[1] == "ground")
{
    // The ground test (GroundTest) on the Aeris 3A, as the Director runs it: calibrates the nose wheel reference.
    var test = new KspWingman.Mcp.GroundTest("Aeris 3A");
    var clock = System.Diagnostics.Stopwatch.StartNew();
    var said = "";
    vessel.Control.SAS = true;
    vessel.Control.ActivateNextStage();
    while (!test.Finished)
    {
        var g = vessel.Flight(vessel.Orbit.Body.ReferenceFrame);
        var command = test.Next(vessel.Flight(vessel.SurfaceReferenceFrame).Heading, g.Speed, clock.Elapsed.TotalSeconds);
        vessel.Control.Throttle = (float)command.Throttle;
        vessel.Control.WheelSteering = (float)command.Steer;
        vessel.Control.Brakes = command.Brakes;
        if (command.Doing != said && !command.Doing.Contains("m/s)"))
        {
            said = command.Doing;
            Log($"{test.Step}: {said} · speed {g.Speed:0.0}, heading {vessel.Flight(vessel.SurfaceReferenceFrame).Heading:0.0}");
        }
        Thread.Sleep(50);
    }
    Log(test.Result is { } r ? test.Describe(r) : "stopped: " + test.Problem);
    Thread.Sleep(1000);
    vessel.Recover(); // nothing left on the runway for the agents
    return;
}
var ground = vessel.Orbit.Body.ReferenceFrame;
var surface = vessel.SurfaceReferenceFrame;
string State() { var f = vessel.Flight(surface); var g = vessel.Flight(ground); return string.Create(CultureInfo.InvariantCulture, $"alt {g.SurfaceAltitude:0} m, speed {g.Speed:0}, vs {g.VerticalSpeed:0.0}, pitch {f.Pitch:0.0}, roll {f.Roll:0.0}, heading {f.Heading:0.0}"); }

// Take off on the autopilot (that part works), climb to 400 m.
var c = vessel.Control;
c.SAS = true; c.Brakes = false; c.Throttle = 1; c.ActivateNextStage();
while (vessel.Flight(ground).Speed < 55) Thread.Sleep(100);
vessel.AutoPilot.TargetPitchAndHeading(12, 90); vessel.AutoPilot.Engaged = true;
while (vessel.Flight(ground).SurfaceAltitude < 400) Thread.Sleep(200);
c.Gear = false;
vessel.AutoPilot.TargetPitchAndHeading(0, 90);
Thread.Sleep(4000);
vessel.AutoPilot.Engaged = false; c.SAS = false;
Log("level, hands off: " + State());

if (args.Length > 1 && args[1] == "test")
{
    // The flight test (FlightTest) on the Aeris 3A, flown by the pilot as the Director would: calibrates the reference.
    var test = new KspWingman.Mcp.FlightTest(vessel.Name);
    var pilot = new KspWingman.Mcp.Pilot();
    var clock = System.Diagnostics.Stopwatch.StartNew();
    var lastInputs = new KspWingman.Mcp.PlaneInputs(0, 0, 0, 0.3f);
    var last = DateTime.UtcNow;
    var said = "";
    while (!test.Finished && clock.Elapsed < TimeSpan.FromMinutes(9))
    {
        var f = vessel.Flight(surface); var g = vessel.Flight(ground);
        var state = new KspWingman.Mcp.PlaneState(f.Pitch, f.Roll, f.Heading, g.VerticalSpeed, g.MeanAltitude, g.Speed, g.SurfaceAltitude);
        var command = test.Next(state, clock.Elapsed.TotalSeconds, lastInputs);
        var now = DateTime.UtcNow;
        var flown = pilot.Step(state, command.Target ?? new KspWingman.Mcp.PlaneTarget(state.Heading, state.Altitude, state.Speed), (now - last).TotalSeconds);
        last = now;
        var inputs = command.Inputs ?? flown;
        lastInputs = inputs;
        c.Pitch = inputs.Pitch; c.Roll = inputs.Roll; c.Yaw = 0; c.Throttle = inputs.Throttle;
        if (command.Doing != said)
        {
            said = command.Doing;
            Log($"{test.Step}: {said} · {State()}");
        }
        Thread.Sleep(50);
    }
    Log(test.Result?.Describe() ?? "stopped: " + test.Problem);
    c.SAS = true;
    return;
}

if (args.Length > 3 && args[1] == "steps")
{
    // FlightLab <port> steps <rollGain> <pitchGain>: 30° heading steps at 110 m/s, 20° of bank at most (like a final),
    // with those gains: how far it banks past what's asked, how far the heading overshoots, how often it swings back.
    var setup = KspWingman.Mcp.PlaneSetup.Default with
    {
        RollGain = double.Parse(args[2], CultureInfo.InvariantCulture), PitchGain = double.Parse(args[3], CultureInfo.InvariantCulture),
    };
    var pilot = new KspWingman.Mcp.Pilot(setup);
    var last = DateTime.UtcNow;
    // Up to 1,000 m first, level on 90°.
    foreach (var (heading, seconds) in new[] { (90.0, 40), (120.0, 30), (90.0, 30), (60.0, 30), (90.0, 30) })
    {
        var target = new KspWingman.Mcp.PlaneTarget(heading, 1000, 110, MaxBank: 20);
        var until = DateTime.UtcNow.AddSeconds(seconds);
        double maxRoll = 0, overshoot = 0;
        var crossings = 0;
        var side = 0;
        var start = vessel.Flight(surface).Heading;
        var turn = Math.Sign(KspWingman.Mcp.Pilot.Wrap(heading - start));
        while (DateTime.UtcNow < until)
        {
            var f = vessel.Flight(surface); var g = vessel.Flight(ground);
            var state = new KspWingman.Mcp.PlaneState(f.Pitch, f.Roll, f.Heading, g.VerticalSpeed, g.MeanAltitude, g.Speed, g.SurfaceAltitude);
            var now = DateTime.UtcNow;
            var inputs = pilot.Step(state, target, (now - last).TotalSeconds);
            last = now;
            c.Pitch = inputs.Pitch; c.Roll = inputs.Roll; c.Yaw = 0; c.Throttle = inputs.Throttle;
            maxRoll = Math.Max(maxRoll, Math.Abs(f.Roll));
            var error = KspWingman.Mcp.Pilot.Wrap(f.Heading - heading);
            if (turn != 0)
                overshoot = Math.Max(overshoot, error * turn);
            var s2 = error > 2 ? 1 : error < -2 ? -1 : 0;
            if (s2 != 0 && side != 0 && s2 != side)
                crossings++;
            if (s2 != 0)
                side = s2;
            Thread.Sleep(50);
        }
        Log(string.Create(CultureInfo.InvariantCulture,
            $"STEP to {heading:0}° (roll ×{setup.RollGain:0.00}): banked {maxRoll:0}° at most (20 asked), heading overshoot {overshoot:0.0}°, swung across {crossings} times; now {State()}"));
    }
    c.SAS = true;
    Thread.Sleep(500);
    return;
}

if (args.Length > 1 && args[1] == "hold")
{
    // The pilot holding heading 90, 500 m, 140 m/s; then turning to 180; then 270 at 700 m. Logs twice a second.
    var pilot = new KspWingman.Mcp.Pilot();
    var legs = new[] { (90.0, 500.0, 140.0, 40), (180.0, 500.0, 140.0, 40), (270.0, 700.0, 140.0, 40) };
    var last = DateTime.UtcNow;
    foreach (var (heading, altitude, speed, seconds) in legs)
    {
        var target = new KspWingman.Mcp.PlaneTarget(heading, altitude, speed);
        var until = DateTime.UtcNow.AddSeconds(seconds);
        var lastLog = DateTime.MinValue;
        var errors = new List<(double H, double A, double S)>();
        while (DateTime.UtcNow < until)
        {
            var f = vessel.Flight(surface); var g = vessel.Flight(ground);
            var state = new KspWingman.Mcp.PlaneState(f.Pitch, f.Roll, f.Heading, g.VerticalSpeed, g.MeanAltitude, g.Speed);
            var now = DateTime.UtcNow;
            var inputs = pilot.Step(state, target, (now - last).TotalSeconds);
            last = now;
            c.Pitch = inputs.Pitch; c.Roll = inputs.Roll; c.Yaw = inputs.Yaw; c.Throttle = inputs.Throttle;
            if (now - lastLog > TimeSpan.FromSeconds(2))
            {
                lastLog = now;
                Log(string.Create(CultureInfo.InvariantCulture, $"target {heading:0}/{altitude:0}/{speed:0}: {State()} · in {inputs.Pitch:0.00}/{inputs.Roll:0.00}/{inputs.Throttle:0.00}"));
            }
            if (until - now < TimeSpan.FromSeconds(10))
                errors.Add((Math.Abs(KspWingman.Mcp.Pilot.Wrap(heading - f.Heading)), Math.Abs(altitude - g.MeanAltitude), Math.Abs(speed - g.Speed)));
            Thread.Sleep(50);
        }
        Log(string.Create(CultureInfo.InvariantCulture, $"LEG {heading:0}/{altitude:0}/{speed:0} last 10 s: heading off {errors.Average(e => e.H):0.0}°, altitude off {errors.Average(e => e.A):0} m, speed off {errors.Average(e => e.S):0} m/s"));
    }
    c.SAS = true;
    return;
}

foreach (var (axis, set) in new (string, Action<float>)[] { ("pitch", v => c.Pitch = v), ("roll", v => c.Roll = v) })
{
    var before = vessel.Flight(surface);
    var (p0, r0) = (before.Pitch, before.Roll);
    set(0.3f); Thread.Sleep(800); set(0);
    var after = vessel.Flight(surface);
    Log($"{axis} +0.3 for 0.8 s: pitch {p0:0.0} → {after.Pitch:0.0}, roll {r0:0.0} → {after.Roll:0.0}");
    c.SAS = true; Thread.Sleep(3000); c.SAS = false;
}
Log("end: " + State());
c.SAS = true; c.Throttle = 0.5f;

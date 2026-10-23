// KSP Wingman · src/KspWingman.Mcp/FlightTools.cs
// The flying tools: launch a craft, point it and set the throttle (kRPC's autopilot), stage, gear, brakes.

using System.ComponentModel;
using System.Globalization;
using KRPC.Client.Services.KRPC;
using KRPC.Client.Services.SpaceCenter;
using ModelContextProtocol.Server;

namespace KspWingman.Mcp;

/// <summary>
/// Only ever the agent's own vessel (kRPC's active vessel; LMP's locks keep everyone else's out of reach). Deliberately
/// missing: time warp (it moves the agent's clock away from the other players) and revert (it rewrites the shared
/// universe). Agents think in seconds, so these are autopilot-level, not stick and rudder.
/// </summary>
[McpServerToolType]
public sealed class FlightTools(KrpcLink krpc)
{
    [McpServerTool(Name = "launchable_craft"), Description(
        "The craft the agent's KSP can launch: from the VAB (rockets, launch pad) and the SPH (planes and rovers, runway).")]
    public string LaunchableCraft() => krpc.Run(connection =>
    {
        var space = connection.SpaceCenter();
        return "VAB (launch pad): " + string.Join(", ", space.LaunchableVessels("VAB")) + "\nSPH (runway): " + string.Join(", ", space.LaunchableVessels("SPH"));
    });

    [McpServerTool(Name = "launch"), Description(
        "Launches a craft from the Space Center: from the SPH onto the runway (planes, rovers) or from the VAB onto the launch " +
        "pad (rockets). The agent must be at the Space Center, not already flying. Check launchable_craft for the names.")]
    public string Launch(
        [Description("The craft's name, exactly as launchable_craft lists it.")] string craft,
        [Description("\"Runway\" (from the SPH) or \"LaunchPad\" (from the VAB).")] string site = "Runway") => krpc.Run(connection =>
    {
        var scene = connection.KRPC().GameScene;
        if (scene != GameScene.SpaceCenter)
            return $"Not launched: launching works from the Space Center, and the game is in {scene}.";
        var directory = site.Equals("LaunchPad", StringComparison.OrdinalIgnoreCase) ? "VAB" : "SPH";
        var space = connection.SpaceCenter();
        if (!space.LaunchableVessels(directory).Contains(craft))
            return $"Not launched: no craft called \"{craft}\" in the {directory}. Craft there: {string.Join(", ", space.LaunchableVessels(directory))}.";
        space.LaunchVessel(directory, craft, directory == "VAB" ? "LaunchPad" : "Runway", null, true, "");
        return $"Launching {craft} on the {(directory == "VAB" ? "launch pad" : "runway")}. It takes a few seconds; then status shows it. " +
               "Mind LMP's safety bubble: other players can't see the agent within 100 m of the pad or the runway start.";
    });

    [McpServerTool(Name = "fly"), Description(
        "Flies the agent's vessel on kRPC's autopilot: point it at a compass heading and pitch, and set the throttle. Holds " +
        "that until told otherwise. Heading 90 is east (along the runway); pitch 0 is level, 90 straight up.")]
    public string Fly(
        [Description("Compass heading in degrees (0 north, 90 east).")] double heading,
        [Description("Pitch in degrees above the horizon (-90 to 90).")] double pitch,
        [Description("Throttle, 0 to 1.")] double throttle) => krpc.Run(connection =>
    {
        var vessel = InFlight(connection, out var problem);
        if (vessel == null)
            return problem!;
        // On the ground the autopilot steers with the wings and tail, the wheels fight it and the plane swerves: the first
        // test flight turned 80° off the runway into the hangar. take_off rolls straight on the nose wheel instead.
        if (vessel.Situation is VesselSituation.PreLaunch or VesselSituation.Landed && vessel.Flight(vessel.Orbit.Body.ReferenceFrame).Speed < 40)
            return "Not from the ground: for a plane on the runway, use take_off (fly is for the air).";
        vessel.Control.Throttle = (float)Math.Clamp(throttle, 0, 1);
        vessel.AutoPilot.TargetPitchAndHeading((float)Math.Clamp(pitch, -90, 90), (float)((heading % 360 + 360) % 360));
        vessel.AutoPilot.Engaged = true;
        return string.Create(CultureInfo.InvariantCulture, $"Flying heading {heading:0}°, pitch {pitch:0}°, throttle {throttle:0.00}.");
    });

    [McpServerTool(Name = "stage"), Description("Fires the next stage (engines, decouplers, parachutes, whatever the craft has next).")]
    public string Stage() => krpc.Run(connection =>
    {
        var vessel = InFlight(connection, out var problem);
        if (vessel == null)
            return problem!;
        vessel.Control.ActivateNextStage();
        return $"Staged. Now on stage {vessel.Control.CurrentStage}.";
    });

    [McpServerTool(Name = "set_controls"), Description("Sets the agent's vessel's brakes, landing gear, SAS and lights. Leave out what shouldn't change.")]
    public string SetControls(bool? brakes = null, bool? gear = null, bool? sas = null, bool? lights = null) => krpc.Run(connection =>
    {
        var vessel = InFlight(connection, out var problem);
        if (vessel == null)
            return problem!;
        var control = vessel.Control;
        if (brakes is { } b) control.Brakes = b;
        if (gear is { } g) control.Gear = g;
        if (sas is { } s) control.SAS = s;
        if (lights is { } l) control.Lights = l;
        return $"Brakes {On(control.Brakes)}, gear {On(control.Gear)}, SAS {On(control.SAS)}, lights {On(control.Lights)}.";
    });

    [McpServerTool(Name = "stop_flying"), Description("Hands the vessel back: autopilot off, throttle to zero.")]
    public string StopFlying() => krpc.Run(connection =>
    {
        var vessel = InFlight(connection, out var problem);
        if (vessel == null)
            return problem!;
        vessel.AutoPilot.Engaged = false;
        vessel.Control.Throttle = 0;
        return "Autopilot off, throttle zero.";
    });

    [McpServerTool(Name = "recover"), Description(
        "Recovers the agent's vessel when it's landed or splashed (back to the Space Center, ready to launch again).")]
    public string Recover() => krpc.Run(connection =>
    {
        var vessel = InFlight(connection, out var problem);
        if (vessel == null)
            return problem!;
        if (!vessel.Recoverable)
            return $"Can't recover it now ({vessel.Situation}): land or stop first.";
        vessel.Recover();
        return "Recovered; back to the Space Center.";
    });

    [McpServerTool(Name = "take_off"), Description(
        "Takes a plane off the runway and climbs out, then holds the climb on the autopilot. Runs by itself (about a minute) " +
        "and reports how it went. Use it right after launch on the Runway. Planes only: the craft needs wheels and wings.")]
    public async Task<string> TakeOff(
        [Description("Compass heading to take off on (the runway runs 90, east).")] double heading = 90,
        [Description("Speed (m/s) to lift the nose at.")] double rotateSpeed = 55,
        [Description("Pitch (degrees) to climb at.")] double climbPitch = 12,
        CancellationToken cancel = default)
    {
        var vessel = krpc.Run(connection => InFlight(connection, out _));
        if (vessel == null)
            return "Not flying: launch a plane on the Runway first.";
        var (control, autopilot, space) = krpc.Run(c => (vessel.Control, vessel.AutoPilot, c.SpaceCenter()));
        var surface = vessel.SurfaceReferenceFrame;
        var ground = vessel.Orbit.Body.ReferenceFrame;
        krpc.Run(_ =>
        {
            autopilot.Engaged = false;
            control.SAS = true; // wings level while rolling
            control.Brakes = false;
            control.Throttle = 1;
            if (control.CurrentStage > 0 && vessel.Thrust < 1)
                control.ActivateNextStage();
            return 0;
        });
        var start = DateTime.UtcNow;
        var rotated = false;
        while (DateTime.UtcNow - start < TimeSpan.FromSeconds(90))
        {
            var (speed, height, actual) = krpc.Run(_ => (vessel.Flight(ground).Speed, vessel.Flight(ground).SurfaceAltitude, vessel.Flight(surface).Heading));
            if (!rotated)
            {
                // Steer the nose wheel back onto the heading: a few degrees off is a gentle push, never full lock.
                var error = ((heading - actual + 540) % 360) - 180;
                krpc.Run(_ => control.WheelSteering = (float)Math.Clamp(-error / 10, -0.5, 0.5));
                if (speed >= rotateSpeed)
                {
                    rotated = true;
                    krpc.Run(_ =>
                    {
                        control.WheelSteering = 0;
                        control.SAS = false;
                        autopilot.TargetPitchAndHeading((float)climbPitch, (float)heading);
                        autopilot.Engaged = true;
                        return 0;
                    });
                }
            }
            else if (height > 100)
            {
                krpc.Run(_ => control.Gear = false);
                return Airborne(height, speed, climbPitch, heading);
            }
            await Task.Delay(100, cancel);
        }
        var (lastSpeed, lastHeight) = krpc.Run(_ => (vessel.Flight(ground).Speed, vessel.Flight(ground).SurfaceAltitude));
        return NotAirborne(lastSpeed, lastHeight, rotated);
    }

    private static string Airborne(double height, double speed, double pitch, double heading) => string.Create(CultureInfo.InvariantCulture,
        $"Airborne: {height:0} m above the ground at {speed:0} m/s, climbing at {pitch:0}° on heading {heading:0}°. Gear up. The autopilot holds this until fly or stop_flying.");

    private static string NotAirborne(double speed, double height, bool rotated) => string.Create(CultureInfo.InvariantCulture,
        $"Didn't get airborne in 90 s: {speed:0} m/s, {height:0} m above the ground ({(rotated ? "nose lifted, but no climb" : "never reached rotate speed")}). Try stop_flying, then recover.");

    private static string On(bool value) => value ? "on" : "off";

    /// <summary>The agent's vessel, or null and why (not in flight).</summary>
    private static Vessel? InFlight(KRPC.Client.Connection connection, out string? problem)
    {
        var scene = connection.KRPC().GameScene;
        if (scene != GameScene.Flight)
        {
            problem = $"Not flying: the game is in {scene}. Launch something first.";
            return null;
        }
        problem = null;
        return connection.SpaceCenter().ActiveVessel;
    }
}

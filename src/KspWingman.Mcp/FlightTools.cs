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
public sealed class FlightTools(KrpcLink krpc, Director director, TowerLink tower)
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
        if (scene != GameScene.Flight)
            director.Stop(); // a new vessel: the last one's words ("Parked beside...") mustn't carry over
        if (scene == GameScene.Flight)
        {
            // From flight only when the current vessel is done (on the ground, in the sea, or wrecked); never mid-flight.
            director.Stop();
            var current = connection.SpaceCenter().ActiveVessel;
            if (!Wrecked(current) && current.Situation is not (VesselSituation.Landed or VesselSituation.Splashed or VesselSituation.PreLaunch) &&
                current.Flight(current.Orbit.Body.ReferenceFrame).SurfaceAltitude > 5)
                return "Not launched: the agent's vessel is still flying. Land it (or stop_flying) first.";
        }
        else if (scene != GameScene.SpaceCenter)
            return $"Not launched: launching works from the Space Center or after a flight, and the game is in {scene}.";
        var directory = site.Equals("LaunchPad", StringComparison.OrdinalIgnoreCase) ? "VAB" : "SPH";
        var space = connection.SpaceCenter();
        if (!space.LaunchableVessels(directory).Contains(craft))
            return $"Not launched: no craft called \"{craft}\" in the {directory}. Craft there: {string.Join(", ", space.LaunchableVessels(directory))}.";
        try
        {
            krpc.Wingman<int>("CloseDialogs"); // a Mission Summary from the last recovery would hold the launch up
        }
        catch (Exception e) when (e is WingmanException or KRPC.Client.RPCException or System.InvalidOperationException)
        {
            // An older bridge: launch anyway.
        }
        if (AvailableCrew() == 0)
            return "Not launched: no kerbals are free; they're all aboard other vessels (planes parked beside the runway keep " +
                   "their crew). Recover one: fly it and recover, or in KSP's Tracking Station. Otherwise KSP would stop on a " +
                   "\"No Control\" question nobody can answer from here.";
        space.LaunchVessel(directory, craft, directory == "VAB" ? "LaunchPad" : "Runway", null, true, "");
        return $"Launching {craft} on the {(directory == "VAB" ? "launch pad" : "runway")}. It takes a few seconds; then status shows it. " +
               (directory == "SPH" ? "It's on the ground, not flying: next, take_off. " : "It's on the pad: next, stage to launch. ") +
               "Mind LMP's safety bubble: other players can't see the agent within 100 m of the pad or the runway start.";
    });

    /// <summary>Free kerbals from the bridge, or -1 when it can't say (an older bridge, no game).</summary>
    private int AvailableCrew()
    {
        try
        {
            return krpc.Wingman<int>("AvailableCrew");
        }
        catch (Exception e) when (e is WingmanException or KRPC.Client.RPCException or System.InvalidOperationException)
        {
            return -1;
        }
    }

    [McpServerTool(Name = "fly"), Description(
        "Flies the agent's plane (in the air) at a compass heading, altitude and speed, and keeps flying it there until told " +
        "otherwise; it turns by banking, at most 30°. Heading 90 is east (along the runway). Altitude is above sea level " +
        "(KSC is about 70 m up). A stock jet flies well at 120-200 m/s.")]
    public string Fly(
        [Description("Compass heading in degrees (0 north, 90 east).")] double heading,
        [Description("Altitude in meters above sea level.")] double altitude,
        [Description("Speed in m/s.")] double speed = 150) => krpc.Run(connection =>
    {
        var vessel = InFlight(connection, out var problem);
        if (vessel == null)
            return problem!;
        // On the ground the autopilot steers with the wings and tail, the wheels fight it and the plane swerves: the first
        // test flight turned 80° off the runway into the hangar. take_off rolls straight on the nose wheel instead.
        if (vessel.Situation is VesselSituation.PreLaunch or VesselSituation.Landed && vessel.Flight(vessel.Orbit.Body.ReferenceFrame).Speed < 40)
            return "Not from the ground: for a plane on the runway, use take_off (fly is for the air).";
        var ground = vessel.Flight(vessel.Orbit.Body.ReferenceFrame);
        if (altitude - (ground.MeanAltitude - ground.SurfaceAltitude) < 100)
            return "Not that low: at least 100 m above the ground there (status shows the height above ground).";
        director.Hold(new PlaneTarget((heading % 360 + 360) % 360, altitude, Math.Clamp(speed, 60, 400)));
        return director.Doing;
    });

    [McpServerTool(Name = "follow"), Description(
        "Flies formation on another player (in the air): keeps the agent's plane in a slot beside them, matching their " +
        "heading, height and speed, and never closer than 30 m. Keeps going until fly, stop_following or stop_flying. " +
        "status says how close to the slot it is.")]
    public string Follow(
        [Description("The player's name, as players lists it.")] string player,
        [Description("Meters to their right (negative: to their left).")] double right = 40,
        [Description("Meters behind them.")] double behind = 50,
        [Description("Meters above them (negative: below).")] double up = 0) => krpc.Run(connection =>
    {
        var vessel = InFlight(connection, out var problem);
        if (vessel == null)
            return problem!;
        if (vessel.Situation is VesselSituation.PreLaunch or VesselSituation.Landed)
            return "Not from the ground: take_off first.";
        if (krpc.Wingman<IList<double>>("PlayerPosition", player).Count < 3)
            return $"{player} has no vessel in this game right now (players shows who's flying what).";
        // A slot on top of them is no slot (a small model passed 0, 0): the default one instead.
        if (Math.Sqrt(right * right + behind * behind + up * up) < Formation.MinSeparation)
            (right, behind, up) = (40, 50, 0);
        director.Follow(player, new Slot(right, behind, up));
        return director.Doing;
    });

    [McpServerTool(Name = "land"), Description(
        "Lands the agent's plane (in the air) on a runway. The tower picks a published approach for where the plane is " +
        "(or give one from approaches), the pilot flies its fixes, then the final down a 4° glide slope, flares, rolls " +
        "out and stops with the brakes on. Goes around by itself if it isn't lined up, and the tower clears it again. " +
        "Takes a few minutes; status shows how it's going. Runways: \"KSC\" (the Space Center's, landing east).")]
    public string Land(
        [Description("Which runway: \"KSC\".")] string runway = "KSC",
        [Description("An approach's name from approaches (\"RNAV 09 NORTH\" or just \"NORTH\"), \"visual\" for a plain circuit, or leave out for the tower's pick.")] string? approach = null)
    {
        // With LUNA EVO's tower (WINGMAN_TOWER_URL), the tower decides: it clears an approach or holds the plane.
        // An approach named with a tower is a request: granted when nobody else is waiting (free flight practises them).
        if (tower.Client is { } client && krpc.Run(c => InFlight(c, out _)?.Situation == VesselSituation.Flying))
            return client.RequestApproachAsync(approach).GetAwaiter().GetResult();
        return LandOnOwn(runway, approach);
    }

    private string LandOnOwn(string runway, string? approach) => krpc.Run(connection =>
    {
        var vessel = InFlight(connection, out var problem);
        if (vessel == null)
            return problem!;
        if (vessel.Situation is VesselSituation.PreLaunch or VesselSituation.Landed or VesselSituation.Splashed)
            return "Already on the ground (or in the sea): land is for a plane in the air.";
        if (Runway.Find(runway) is not { } found)
            return $"No runway called \"{runway}\". Known: KSC.";
        if (approach is { Length: > 0 } a && !a.Trim().Equals("visual", StringComparison.OrdinalIgnoreCase) && Approach.Find(a) == null)
            return $"No approach called \"{a}\". Approaches: {string.Join(", ", Approach.Ksc.Select(x => x.Name))}, or visual.";
        // The tower's pick now, from where the plane is, so the answer can carry the clearance.
        if (approach is not { Length: > 0 })
        {
            var g = vessel.Flight(vessel.Orbit.Body.ReferenceFrame);
            var (along, cross) = new Landing(found).RunwayFrame(vessel.Orbit.Body.EquatorialRadius, new Geo(g.Latitude, g.Longitude, g.MeanAltitude));
            approach = Tower.Pick(Approach.Ksc, along, cross, vessel.Flight(vessel.SurfaceReferenceFrame).Heading, found.Heading).Name;
        }
        director.Land(found, approach);
        var picked = Approach.Find(approach);
        return picked == null
            ? "Visual approach: flying a circuit to the final approach. " + director.Doing
            : Tower.Clearance(krpc.Wingman<string>("PlayerName"), picked);
    });

    [McpServerTool(Name = "approaches"), Description(
        "The published approaches to a runway: their fixes with the heights and speeds to cross them at, and when to fly " +
        "each. land picks one by itself; give one to land to choose.")]
    public string Approaches([Description("Which airport: \"KSC\".")] string airport = "KSC") =>
        string.Join("\n", Approach.Ksc.Select(a => a.Describe())) + "\nOr \"visual\": a plain circuit to the final approach.";

    [McpServerTool(Name = "stop_following"), Description("Stops formation flying and holds the current heading, height and speed.")]
    public string StopFollowing() => krpc.Run(connection =>
    {
        var vessel = InFlight(connection, out var problem);
        if (vessel == null)
            return problem!;
        var f = vessel.Flight(vessel.SurfaceReferenceFrame);
        var g = vessel.Flight(vessel.Orbit.Body.ReferenceFrame);
        director.Hold(new PlaneTarget(f.Heading, g.MeanAltitude, Math.Max(60, g.Speed)));
        return director.Doing;
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
        director.Stop();
        vessel.AutoPilot.Engaged = false;
        vessel.Control.Throttle = 0;
        vessel.Control.SAS = true;
        return "Not flying it any more: throttle zero, SAS on.";
    });

    [McpServerTool(Name = "recover"), Description(
        "Recovers the agent's vessel when it's landed or splashed (back to the Space Center, ready to launch again).")]
    public string Recover()
    {
        var result = krpc.Run(connection =>
        {
            var vessel = InFlight(connection, out var problem);
            if (vessel == null)
                return problem!;
            if (!vessel.Recoverable)
                return $"Can't recover it now ({vessel.Situation}): land or stop first.";
            vessel.Recover();
            return "Recovered; back to the Space Center.";
        });
        if (!result.StartsWith("Recovered", StringComparison.Ordinal))
            return result;
        // KSP's Mission Summary comes up a moment later, in the Space Center; nobody can click it from here, and with it
        // open the next launch hung half-loaded in the rig. Close it as soon as it's there (an older bridge can't).
        for (var i = 0; i < 16; i++)
        {
            Thread.Sleep(500);
            try
            {
                if (krpc.Wingman<int>("CloseDialogs") > 0)
                    break;
            }
            catch (Exception e) when (e is WingmanException or KRPC.Client.RPCException or System.InvalidOperationException)
            {
                break;
            }
        }
        return result;
    }

    [McpServerTool(Name = "take_off"), Description(
        "Takes a plane off the runway, climbs out and then flies on at the heading, altitude and speed given (no fly needed " +
        "after it). Runs by itself (about a minute) and reports how it went. Use it right after launch on the Runway. " +
        "Planes only: the craft needs wheels and wings.")]
    public async Task<string> TakeOff(
        [Description("Compass heading to fly on once airborne (it always takes off straight along the runway first).")] double heading = 90,
        [Description("Speed (m/s) to lift the nose at.")] double rotateSpeed = 55,
        [Description("Pitch (degrees) to climb at.")] double climbPitch = 12,
        [Description("Altitude (m above sea level) to fly at afterwards; leave out for 500 m above the ground.")] double? altitude = null,
        [Description("Speed (m/s) to fly at afterwards.")] double speed = 150,
        CancellationToken cancel = default)
    {
        var vessel = krpc.Run(connection => InFlight(connection, out _));
        if (vessel == null)
            return "Not flying: launch a plane on the Runway first.";
        // Whatever the agent asked for, within what a plane can do (a small model once asked to rotate at 5 m/s).
        heading = (heading % 360 + 360) % 360;
        rotateSpeed = Math.Clamp(rotateSpeed, 40, 90);
        climbPitch = Math.Clamp(climbPitch, 5, 20);
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
        // Roll and climb out the way the plane points (along the runway); turn to the asked heading only once flying. A
        // small model asked for heading 0 from KSC's east-west runway, which would steer it off the side.
        var runway = krpc.Run(_ => vessel.Flight(surface).Heading);
        var start = DateTime.UtcNow;
        var rotated = false;
        while (DateTime.UtcNow - start < TimeSpan.FromSeconds(90))
        {
            var (rollSpeed, height, actual) = krpc.Run(_ => (vessel.Flight(ground).Speed, vessel.Flight(ground).SurfaceAltitude, vessel.Flight(surface).Heading));
            if (!rotated)
            {
                // Steer the nose wheel back onto the heading: a few degrees off is a gentle push, never full lock.
                var error = ((runway - actual + 540) % 360) - 180;
                krpc.Run(_ => control.WheelSteering = (float)Math.Clamp(-error / 10, -0.5, 0.5));
                if (rollSpeed >= rotateSpeed)
                {
                    rotated = true;
                    krpc.Run(_ =>
                    {
                        control.WheelSteering = 0;
                        control.SAS = false;
                        autopilot.TargetPitchAndHeading((float)climbPitch, (float)runway);
                        autopilot.Engaged = true;
                        return 0;
                    });
                }
            }
            else if (height > 100)
            {
                krpc.Run(_ => control.Gear = false);
                // From here our own pilot flies, to the asked heading and altitude (500 m above the ground if none).
                var sea = krpc.Run(_ => vessel.Flight(ground).MeanAltitude - height);
                var cruise = Math.Max(altitude ?? sea + 500, sea + 150);
                var cruiseSpeed = Math.Clamp(speed, 60, 400);
                director.Hold(new PlaneTarget(heading, cruise, cruiseSpeed));
                return Airborne(height, rollSpeed, heading, cruise, cruiseSpeed);
            }
            await Task.Delay(100, cancel);
        }
        var (lastSpeed, lastHeight) = krpc.Run(_ => (vessel.Flight(ground).Speed, vessel.Flight(ground).SurfaceAltitude));
        return NotAirborne(lastSpeed, lastHeight, rotated);
    }

    private static string Airborne(double height, double speed, double heading, double cruise, double cruiseSpeed) => string.Create(CultureInfo.InvariantCulture,
        $"Airborne: {height:0} m above the ground at {speed:0} m/s on heading {heading:0}°, gear up. Now flying on: heading {heading:0}°, " +
        $"{cruise:0} m, {cruiseSpeed:0} m/s. Use fly, follow or land to change that.");

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
        var vessel = connection.SpaceCenter().ActiveVessel;
        problem = Wrecked(vessel) ? "Not flying: the agent's vessel was destroyed (it crashed). Launch a new one." : null;
        return problem == null ? vessel : null;
    }

    /// <summary>
    /// A crashed vessel stays the active one until the next launch, frozen and still "Flying" in kRPC's eyes, with no
    /// parts left (seen in the rig: a sunk plane at 2 m, 265 m/s, nose down, for minutes).
    /// </summary>
    public static bool Wrecked(Vessel vessel) => vessel.Parts.All.Count == 0;
}

// KSP Wingman · src/KspWingman.Mcp/Director.cs
// Flies the agent's plane in the background, 20 times a second: holding a heading/altitude/speed, or formation on a player.

using System.Globalization;
using KRPC.Client.Services.SpaceCenter;

namespace KspWingman.Mcp;

/// <summary>
/// One flight loop at a time, on its own kRPC connection (the tools keep theirs). The agent decides what to fly; this
/// flies it, between the agent's calls. Any problem (the plane crashed, KSP closed) stops the loop and is reported by
/// the next status.
/// </summary>
public sealed class Director(string host, int port) : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _stop;
    private KrpcLink? _link;
    private volatile string _doing = "Not flying anything.";
    private volatile string? _problem;

    /// <summary>What the loop is doing right now (or why it stopped), for status.</summary>
    public string Doing => _problem is { } p ? "Stopped: " + p : _doing;

    public bool Active { get { lock (_gate) return _stop != null; } }

    /// <summary>Holds a heading, altitude (m above sea level) and speed until told otherwise.</summary>
    public void Hold(PlaneTarget target) =>
        Start(_ => target, string.Create(CultureInfo.InvariantCulture, $"Holding heading {target.Heading:0}°, {target.Altitude:0} m, {target.Speed:0} m/s."));

    /// <summary>Formation on another player's vessel (found by the bridge through LMP's control lock).</summary>
    public void Follow(string player, Slot slot)
    {
        var formation = new Formation();
        var last = DateTime.UtcNow;
        PlaneTarget? hold = null;
        Start(link =>
        {
            var position = link.Wingman<IList<double>>("PlayerPosition", player);
            if (position.Count < 3)
            {
                _doing = $"Following {player}: they have no vessel in this game right now. Holding course.";
                return hold;
            }
            var vessel = link.Run(c => c.SpaceCenter().ActiveVessel);
            var flight = link.Run(_ => vessel.Flight(vessel.Orbit.Body.ReferenceFrame));
            var radius = link.Run(_ => vessel.Orbit.Body.EquatorialRadius);
            var me = new Geo(link.Run(_ => flight.Latitude), link.Run(_ => flight.Longitude), link.Run(_ => flight.MeanAltitude));
            var now = DateTime.UtcNow;
            var step = formation.Step(radius, me, new Geo(position[0], position[1], position[2]), (now - last).TotalSeconds, slot);
            last = now;
            if (step is not { } s)
            {
                _doing = $"Following {player}: waiting for them to fly (they're slower than {Formation.MinLeaderSpeed:0} m/s).";
                return hold;
            }
            hold = s.Target;
            _doing = string.Create(CultureInfo.InvariantCulture,
                $"Following {player} ({slot.Right:0} m right, {slot.Behind:0} m behind, {slot.Up:0} m up): {s.SlotDistance:0} m from the slot, " +
                $"{s.LeaderDistance:0} m from them; they fly {s.LeaderSpeed:0} m/s. Aiming for heading {s.Target.Heading:0}° at {s.Target.Speed:0} m/s.");
            return s.Target;
        }, $"Following {player}: looking for them.");
    }

    /// <summary>
    /// Lands on a runway (from anywhere in the air), rolls out and stops with the brakes on. <paramref name="approach"/>:
    /// null for the tower's pick, "visual", or an approach's name. The tower's clearance shows up in Clearance.
    /// </summary>
    public void Land(Runway runway, string? approach = null)
    {
        var landing = new Landing(runway, approach);
        _clearance = null;
        string? said = null;
        Start(link =>
        {
            var vessel = link.Run(c => c.SpaceCenter().ActiveVessel);
            var (g, f) = link.Run(_ => (vessel.Flight(vessel.Orbit.Body.ReferenceFrame), vessel.Flight(vessel.SurfaceReferenceFrame)));
            var radius = link.Run(_ => vessel.Orbit.Body.EquatorialRadius);
            var me = new Geo(link.Run(_ => g.Latitude), link.Run(_ => g.Longitude), link.Run(_ => g.MeanAltitude));
            var step = landing.Step(radius, me, link.Run(_ => f.Heading), link.Run(_ => g.Speed), link.Run(_ => g.SurfaceAltitude), onGround: false);
            if (step.Phase is LandingPhase.Final or LandingPhase.Flare)
                link.Run(_ => { if (!vessel.Control.Gear) vessel.Control.Gear = true; return 0; });
            _doing = step.Doing;
            if (landing.Approach is { } flying && flying.Name != said)
            {
                said = flying.Name;
                _clearance = Tower.Clearance(link.Wingman<string>("PlayerName"), flying);
            }
            return step.Target;
        }, $"Landing on {runway.Name}: working out the approach.", landing);
    }

    private (DateTime Time, double Heading)? _rolloutLast;

    /// <summary>The tower's last clearance for the current landing (null: none yet, or a visual approach).</summary>
    public string? Clearance => _clearance;

    private volatile string? _clearance;

    public void Stop()
    {
        lock (_gate)
        {
            _stop?.Cancel();
            _stop = null;
        }
        _doing = "Not flying anything.";
    }

    private void Start(Func<KrpcLink, PlaneTarget?> target, string doing, Landing? landing = null)
    {
        Stop();
        CancellationTokenSource stop;
        lock (_gate)
        {
            _link ??= new KrpcLink(host, port);
            stop = _stop = new CancellationTokenSource();
        }
        _problem = null;
        _doing = doing;
        var link = _link;
        _ = Task.Run(async () =>
        {
            try
            {
                await Fly(link, target, landing, stop.Token);
                lock (_gate)
                {
                    if (_stop == stop)
                        _stop = null; // done (landed and stopped)
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                _problem = e.Message;
                lock (_gate)
                {
                    if (_stop == stop)
                        _stop = null;
                }
            }
        });
    }

    private async Task Fly(KrpcLink link, Func<KrpcLink, PlaneTarget?> target, Landing? landing, CancellationToken stop)
    {
        var vessel = link.Run(c => c.SpaceCenter().ActiveVessel);
        var control = link.Run(_ => vessel.Control);
        var ground = link.Run(_ => vessel.Orbit.Body.ReferenceFrame);
        var surface = link.Run(_ => vessel.SurfaceReferenceFrame);
        link.Run(_ => { vessel.AutoPilot.Engaged = false; control.SAS = false; return 0; });
        var pilot = new Pilot();
        var last = DateTime.UtcNow;
        var ticks = 0;
        while (!stop.IsCancellationRequested)
        {
            // Every second: SAS and kRPC's autopilot stay off (either one fights the pilot: SAS held 10° of climb and the
            // leader kept climbing to 5 km in the rig).
            if (ticks++ % 20 == 0)
            {
                if (link.Run(_ => FlightTools.Wrecked(vessel)))
                    throw new WingmanException("the vessel was destroyed (it crashed).");
                link.Run(_ => { if (control.SAS) control.SAS = false; if (vessel.AutoPilot.Engaged) vessel.AutoPilot.Engaged = false; return 0; });
            }
            var situation = link.Run(_ => vessel.Situation);
            if (situation == VesselSituation.Landed && landing != null)
            {
                if (Rollout(link, vessel, landing))
                    return;
                await Task.Delay(50, stop);
                continue;
            }
            if (situation is VesselSituation.Landed or VesselSituation.Splashed or VesselSituation.PreLaunch)
                throw new WingmanException($"the vessel is {situation.ToString().ToLowerInvariant()}: the pilot flies in the air only.");
            var (f, g) = link.Run(_ => (vessel.Flight(surface), vessel.Flight(ground)));
            var state = link.Run(_ => new PlaneState(f.Pitch, f.Roll, f.Heading, g.VerticalSpeed, g.MeanAltitude, g.Speed, g.SurfaceAltitude));
            if (target(link) is { } t)
            {
                var now = DateTime.UtcNow;
                var inputs = pilot.Step(state, t, (now - last).TotalSeconds);
                last = now;
                link.Run(_ => { control.Pitch = inputs.Pitch; control.Roll = inputs.Roll; control.Yaw = inputs.Yaw; control.Throttle = inputs.Throttle; return 0; });
            }
            await Task.Delay(50, stop);
        }
        // Stick centered; SAS is left to whoever comes next (another mode, or stop_flying, which turns it on).
        link.Run(_ => { control.Pitch = 0; control.Roll = 0; control.Yaw = 0; return 0; });
    }

    /// <summary>On the runway after a landing: throttle shut, brakes, nose wheel on the centerline. True once stopped.</summary>
    private bool Rollout(KrpcLink link, Vessel vessel, Landing landing)
    {
        var (g, f) = link.Run(_ => (vessel.Flight(vessel.Orbit.Body.ReferenceFrame), vessel.Flight(vessel.SurfaceReferenceFrame)));
        var radius = link.Run(_ => vessel.Orbit.Body.EquatorialRadius);
        var me = new Geo(link.Run(_ => g.Latitude), link.Run(_ => g.Longitude), link.Run(_ => g.MeanAltitude));
        var speed = link.Run(_ => g.Speed);
        var step = landing.Step(radius, me, link.Run(_ => f.Heading), speed, 0, onGround: true);
        var headingError = Pilot.Wrap(landing.Runway.Heading - link.Run(_ => f.Heading));
        // Wheel steering uses KSP's sign (positive steers left), the opposite of the rudder's.
        var heading = link.Run(_ => f.Heading);
        var now = DateTime.UtcNow;
        var yawRate = _rolloutLast is { } last && (now - last.Time).TotalSeconds > 0.01
            ? Pilot.Wrap(heading - last.Heading) / (now - last.Time).TotalSeconds : 0;
        _rolloutLast = (now, heading);
        var (steer, rudder, brakes) = Landing.Rollout(speed, headingError, step.Cross, yawRate);
        var roll = link.Run(_ => f.Roll);
        link.Run(_ =>
        {
            var c = vessel.Control;
            c.Throttle = 0;
            c.Pitch = 0;
            c.Yaw = (float)rudder;
            c.Roll = (float)Math.Clamp(-0.01 * roll, -0.2, 0.2);
            c.WheelSteering = (float)steer;
            c.Brakes = brakes;
            return 0;
        });
        _doing = step.Doing;
        if (speed > 1)
            return false;
        link.Run(_ => { vessel.Control.Brakes = true; vessel.Control.WheelSteering = 0; return 0; });
        _doing = string.Create(CultureInfo.InvariantCulture,
            $"Landed on {landing.Runway.Name} and stopped, brakes on: {step.Along + Landing.TouchdownAfterThreshold:0} m down the runway, " +
            $"{Math.Abs(step.Cross):0} m off the centerline, pointing {Math.Abs(headingError):0}° off the runway heading. recover or take_off from here.");
        return true;
    }

    public void Dispose()
    {
        var flying = Active;
        Stop();
        // Leaving (the agent's host closed): don't leave the plane with nobody on the stick. SAS holds its attitude.
        if (flying)
        {
            try
            {
                Thread.Sleep(200); // let the loop center the stick first
                _link?.Run(c => { c.SpaceCenter().ActiveVessel.Control.SAS = true; return 0; });
            }
            catch (Exception)
            {
            }
        }
        _link?.Dispose();
    }
}

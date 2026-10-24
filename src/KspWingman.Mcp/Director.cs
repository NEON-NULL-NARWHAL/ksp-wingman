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

    /// <summary>The plane being flown and its own numbers (Handling.cs): set at launch, and by a flight test.</summary>
    public PlaneSetup Setup { get; private set; } = PlaneSetup.Default;

    public string? Craft { get; private set; }

    private readonly List<FinalReport> _finals = [];

    /// <summary>How this flight's finals went (weaving, height 1 km out), for its outcome.</summary>
    public IReadOnlyList<FinalReport> Finals { get { lock (_finals) return _finals.ToList(); } }

    /// <summary>
    /// The plane's name for a vessel (localized first, FlightTools.Localized): the craft launched, when the vessel is
    /// it. KSP names stock craft by their translation key ("#autoLOC_501174" for the Aeris 3A), which made every rig
    /// flight until 0.11 a plane called that.
    /// </summary>
    public string CraftOf(string vesselName) =>
        Craft != null && (vesselName.StartsWith("#autoLOC", StringComparison.Ordinal) || vesselName.Equals(Craft, StringComparison.OrdinalIgnoreCase))
            ? Craft
            : vesselName;

    /// <summary>A new plane on the runway: its setup, and a clean slate for its finals.</summary>
    public void NewFlight(string craft, PlaneSetup setup)
    {
        (Craft, Setup, Ground) = (craft, setup, null);
        lock (_finals)
            _finals.Clear();
    }

    /// <summary>This flight's ground test, until the flight test in the air sends it with its own numbers.</summary>
    public GroundResult? Ground { get; private set; }

    /// <summary>
    /// The ground test on the runway (GroundTest), stopped with the brakes on afterwards. Its nose wheel gain is used
    /// from then on (take_off, the rollout, taxiing). <paramref name="done"/> gets it once finished.
    /// </summary>
    public void TestGround(GroundTest test, Action<GroundTest> done)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var staged = false;
        Start(_ => null, $"Ground test of the {test.Craft}: starting.", (link, vessel) =>
        {
            var g = link.Run(_ => vessel.Flight(vessel.Orbit.Body.ReferenceFrame));
            var (speed, heading) = (link.Run(_ => g.Speed), link.Run(_ => vessel.Flight(vessel.SurfaceReferenceFrame).Heading));
            var command = test.Next(heading, speed, clock.Elapsed.TotalSeconds);
            link.Run(_ =>
            {
                var c = vessel.Control;
                if (!staged)
                {
                    staged = true;
                    c.SAS = true; // wings level while rolling, as take_off does
                    if (c.CurrentStage > 0 && vessel.Thrust < 1)
                        c.ActivateNextStage();
                }
                c.Throttle = (float)command.Throttle;
                c.WheelSteering = (float)command.Steer;
                c.Brakes = command.Brakes;
                return 0;
            });
            _doing = command.Doing;
            if (!test.Finished)
                return false;
            if (test.Result is { } r)
            {
                Ground = r;
                Setup = Setup.WithGround(r);
            }
            done(test);
            return true;
        });
    }

    /// <summary>The setup changed mid-flight (a flight test just measured the plane): the pilot uses it from the next mode.</summary>
    public void Use(PlaneSetup setup) => Setup = setup;

    /// <summary>
    /// Flies a flight test (FlightTest) and then holds level where it ended. <paramref name="done"/> gets the test once
    /// it's finished (done or stopped), from the flight loop's thread.
    /// </summary>
    public void Test(FlightTest test, Action<FlightTest> done)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var reported = false;
        StartSteer((_, state, last) =>
        {
            var command = test.Next(state, clock.Elapsed.TotalSeconds, last);
            _doing = command.Doing;
            if (test.Finished && !reported)
            {
                reported = true;
                done(test);
            }
            return command;
        }, $"Flight test of the {test.Craft}: starting.");
    }

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
    public void Land(Runway runway, string? approach = null) => Land(new Landing(runway, approach, setup: Setup));

    /// <summary>Lands on a route a tower sent (its fixes, then the final).</summary>
    public void Land(Runway runway, Approach route) => Land(new Landing(runway, given: route, setup: Setup));

    /// <summary>Holds: right-hand circles around a point at an altitude, until told otherwise (a tower's hold).</summary>
    public void HoldAt(Geo center, double radius, string fix)
    {
        Start(link =>
        {
            var vessel = link.Run(c => c.SpaceCenter().ActiveVessel);
            var g = link.Run(_ => vessel.Flight(vessel.Orbit.Body.ReferenceFrame));
            var me = new Geo(link.Run(_ => g.Latitude), link.Run(_ => g.Longitude), link.Run(_ => g.MeanAltitude));
            var target = Holding.Target(link.Run(_ => vessel.Orbit.Body.EquatorialRadius), me, center, radius);
            _doing = string.Create(CultureInfo.InvariantCulture, $"Holding at {fix}: {center.Altitude:0} m, right turns, waiting for the tower.");
            return target;
        }, $"Holding at {fix}.");
    }

    private void Land(Landing landing)
    {
        var runway = landing.Runway;
        _clearance = null;
        _lastLanding = null; // this landing's numbers only (the flight record waits for them)
        _touchAndGo = false;
        _touchedDown = false;
        _yawRate = 0;
        landing.FinalEnded = report => { lock (_finals) _finals.Add(report); };
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
            else if (step.Phase == LandingPhase.GoAround && link.Run(_ => g.SurfaceAltitude) > 30)
                link.Run(_ => { if (vessel.Control.Gear) vessel.Control.Gear = false; return 0; }); // less drag on the climb
            _doing = step.Doing;
            _approach = landing.Approach?.Name ?? "visual";
            if (landing.Approach is { } flying && flying.Name != said)
            {
                said = flying.Name;
                _clearance = Tower.Clearance(link.Wingman<string>("PlayerName"), flying);
            }
            return step.Target;
        }, $"Landing on {runway.Name}: working out the approach.", (link, vessel) => Rollout(link, vessel, landing));
    }

    /// <summary>
    /// Taxis off the runway after a landing (a tower's "vacate"): a right turn at walking pace, parked 90 m beside the
    /// centerline with the brakes on. The plane stays in the game (no more recovering it to clear the runway).
    /// </summary>
    public void Vacate(Runway runway)
    {
        var started = DateTime.UtcNow;
        var frame = new Landing(runway);
        Start(_ => null, $"Taxiing off {runway.Name}.", (link, vessel) =>
        {
            var (g, f) = link.Run(_ => (vessel.Flight(vessel.Orbit.Body.ReferenceFrame), vessel.Flight(vessel.SurfaceReferenceFrame)));
            var me = new Geo(link.Run(_ => g.Latitude), link.Run(_ => g.Longitude), link.Run(_ => g.MeanAltitude));
            var (_, cross) = frame.RunwayFrame(link.Run(_ => vessel.Orbit.Body.EquatorialRadius), me);
            var (speed, heading) = (link.Run(_ => g.Speed), link.Run(_ => f.Heading));
            var (steer, throttle, brakes, done) = Taxi.Step(runway.Heading, heading, cross, speed);
            steer = Math.Clamp(steer * Setup.SteerGain, -0.5, 0.5); // this plane's nose wheel (its ground test)
            var timedOut = DateTime.UtcNow - started > Taxi.MaxTime;
            link.Run(_ =>
            {
                var c = vessel.Control;
                c.WheelSteering = (float)(done || timedOut ? 0 : steer);
                c.Throttle = (float)(done || timedOut ? 0 : throttle);
                c.Brakes = brakes || done || timedOut;
                return 0;
            });
            _doing = done || timedOut
                ? string.Create(CultureInfo.InvariantCulture, $"Parked beside {runway.Name}, {Math.Abs(cross):0} m off the centerline, brakes on.")
                : string.Create(CultureInfo.InvariantCulture, $"Taxiing off {runway.Name}: {Math.Abs(cross):0} m off the centerline, {speed:0.0} m/s.");
            return (done && speed < 0.5) || timedOut;
        });
    }

    /// <summary>The approach being flown ("visual", a published one, a tower's route), for the flight's outcome.</summary>
    public string? Approach => _approach;

    /// <summary>Where the last landing stopped, for the flight's outcome.</summary>
    public LandingResult? LastLanding => _lastLanding;

    private volatile string? _approach;
    private volatile LandingResult? _lastLanding;

    private (DateTime Time, double Heading)? _rolloutLast;
    private bool _touchAndGo;
    private double _yawRate;
    private volatile bool _touchedDown;

    /// <summary>After touchdown, "flying" lower than this (m) is a bounce, still the rollout.</summary>
    public const double BounceHeight = 4;

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

    /// <param name="groundStep">On the ground: what to do (the rollout, a taxi); true once done. Null: the pilot only flies.</param>
    private void Start(Func<KrpcLink, PlaneTarget?> target, string doing, Func<KrpcLink, Vessel, bool>? groundStep = null) =>
        StartSteer((link, _, _) => new TestCommand(target(link), null, ""), doing, groundStep);

    /// <summary>Like Start, but the step sees the plane's numbers and the last inputs, and may set the stick itself.</summary>
    private void StartSteer(Func<KrpcLink, PlaneState, PlaneInputs, TestCommand> steer, string doing, Func<KrpcLink, Vessel, bool>? groundStep = null)
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
                await Fly(link, steer, groundStep, stop.Token);
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

    private async Task Fly(KrpcLink link, Func<KrpcLink, PlaneState, PlaneInputs, TestCommand> steer, Func<KrpcLink, Vessel, bool>? groundStep, CancellationToken stop)
    {
        var vessel = link.Run(c => c.SpaceCenter().ActiveVessel);
        var control = link.Run(_ => vessel.Control);
        var ground = link.Run(_ => vessel.Orbit.Body.ReferenceFrame);
        var surface = link.Run(_ => vessel.SurfaceReferenceFrame);
        link.Run(_ => { vessel.AutoPilot.Engaged = false; control.SAS = false; return 0; });
        var pilot = new Pilot(Setup);
        var last = DateTime.UtcNow;
        var lastInputs = new PlaneInputs(0, 0, 0, (float)Setup.CruiseThrottle);
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
            // A bounce after touchdown is still the rollout: the rig's Aeris 4A hopped 2 m up at 58 m/s, below its stall,
            // and the landing, seeing it "flying" 71 m off the centerline, sent it around into the ground.
            var bouncing = situation == VesselSituation.Flying && _touchedDown && !_touchAndGo &&
                           link.Run(_ => vessel.Flight(ground).SurfaceAltitude) < BounceHeight;
            if ((situation is VesselSituation.Landed or VesselSituation.PreLaunch || bouncing) && groundStep != null)
            {
                if (groundStep(link, vessel))
                    return;
                await Task.Delay(50, stop);
                continue;
            }
            if (situation is VesselSituation.Landed or VesselSituation.Splashed or VesselSituation.PreLaunch)
                throw new WingmanException($"the vessel is {situation.ToString().ToLowerInvariant()}: the pilot flies in the air only.");
            var (f, g) = link.Run(_ => (vessel.Flight(surface), vessel.Flight(ground)));
            var state = link.Run(_ => new PlaneState(f.Pitch, f.Roll, f.Heading, g.VerticalSpeed, g.MeanAltitude, g.Speed, g.SurfaceAltitude));
            var command = steer(link, state, lastInputs);
            if (command.Target != null || command.Inputs != null)
            {
                var now = DateTime.UtcNow;
                // The pilot steps even while a flight test holds the stick, so its rates are current when it takes over.
                var flown = pilot.Step(state, command.Target ?? new PlaneTarget(state.Heading, state.Altitude, state.Speed), (now - last).TotalSeconds);
                last = now;
                var inputs = command.Inputs ?? flown;
                lastInputs = inputs;
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
        var rawYaw = _rolloutLast is { } last && (now - last.Time).TotalSeconds > 0.01
            ? Pilot.Wrap(heading - last.Heading) / (now - last.Time).TotalSeconds : 0;
        _rolloutLast = (now, heading);
        // Smoothed: from 20 readings a second it's noisy, and the fast brakes wait for a steady nose.
        _yawRate += 0.3 * (rawYaw - _yawRate);
        var yawRate = _yawRate;
        _touchedDown = true;
        var remaining = landing.Runway.Length - Landing.TouchdownAfterThreshold - step.Along;
        var (steer, rudder, brakes) = Landing.Rollout(speed, headingError, step.Cross, yawRate, remaining, Setup.Braking);
        steer = Math.Clamp(steer * Setup.SteerGain, -0.3, 0.3); // this plane's nose wheel (its ground test)
        // Too fast to stop in what's left: off again (once decided, it stays decided; airborne, the landing goes around).
        _touchAndGo |= Landing.TouchAndGo(speed, remaining, Setup.Braking, Setup.RotateSpeed);
        brakes &= !_touchAndGo;
        var roll = link.Run(_ => f.Roll);
        link.Run(_ =>
        {
            var c = vessel.Control;
            c.Throttle = _touchAndGo ? 1 : 0;
            c.Pitch = _touchAndGo ? 0.3f : 0;
            c.Yaw = (float)rudder;
            c.Roll = (float)Math.Clamp(-0.01 * roll, -0.2, 0.2);
            c.WheelSteering = (float)steer;
            c.Brakes = brakes;
            return 0;
        });
        _doing = _touchAndGo
            ? string.Create(CultureInfo.InvariantCulture, $"Going around from the runway: {speed:0} m/s with {remaining:0} m left, too fast to stop.")
            : step.Doing;
        if (speed > 1)
            return false;
        link.Run(_ => { vessel.Control.Brakes = true; vessel.Control.WheelSteering = 0; return 0; });
        _lastLanding = new LandingResult(landing.Runway.Name, Math.Round(step.Along + Landing.TouchdownAfterThreshold),
            Math.Round(Math.Abs(step.Cross)), Math.Round(Math.Abs(headingError)));
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

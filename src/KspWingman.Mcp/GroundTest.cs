// KSP Wingman · src/KspWingman.Mcp/GroundTest.cs
// The ground half of a flight test (the owner's idea: ground handling matters for takeoffs, rollouts and taxiing): on
// the runway before take_off, a slow roll with the nose wheel straight (does it veer?), a steering pulse (how hard
// does the nose wheel turn it?) and a brake stop. About 200 m of runway. Pure like FlightTest.

using System.Globalization;

namespace KspWingman.Mcp;

public enum GroundStep { Roll, Pulse, Recover, Brake, Done, Stopped }

/// <summary>What to set on the ground: throttle 0..1, nose wheel (KSP's sign: positive steers left), brakes.</summary>
public readonly record struct GroundCommand(double Throttle, double Steer, bool Brakes, string Doing);

/// <summary>What the ground test measured.</summary>
/// <param name="SteerRate">Degrees of heading a second for a full nose wheel at 10 m/s (the turn scales with the speed).</param>
/// <param name="Drift">Degrees a second it veers with the nose wheel straight, rolling at 3-10 m/s (+ is to the right).</param>
/// <param name="Braking">How fast the brakes stop it from taxi speed (m/s²).</param>
public readonly record struct GroundResult(double SteerRate, double Drift, double Braking);

public sealed class GroundTest(string craft)
{
    public const double TaxiSpeed = 12, PulseSteer = 0.2, PulseSeconds = 1.5;
    public static readonly TimeSpan MaxTime = TimeSpan.FromSeconds(120);

    private GroundStep _step = GroundStep.Roll;
    private double _start = double.NaN, _stepStart, _heading;
    private double? _driftFrom, _driftFromHeading;
    private double _drift, _steerRate, _braking;
    private double _pulseFromHeading, _pulseSpeedSum;
    private int _pulseTicks;
    private double _brakeFromSpeed, _steadySince = double.NaN;
    private bool _veered;
    private (double T, double Heading)? _last;

    public string Craft => craft;
    public GroundStep Step => _step;
    public GroundResult? Result { get; private set; }
    public string? Problem { get; private set; }
    public bool Finished => _step is GroundStep.Done or GroundStep.Stopped;

    /// <param name="t">Seconds (any clock that only goes forward).</param>
    public GroundCommand Next(double heading, double speed, double t)
    {
        if (double.IsNaN(_start))
            (_start, _stepStart, _heading) = (t, t, heading);
        var yawRate = _last is { } l && t - l.T > 0.01 ? Pilot.Wrap(heading - l.Heading) / (t - l.T) : 0; // + turning right
        _last = (t, heading);
        var error = Pilot.Wrap(_heading - heading); // positive: the runway heading is to the right
        var inStep = t - _stepStart;
        // Back onto the runway heading (a few degrees is a gentle push), damped by the turn rate: take_off's undamped
        // -error/10 swung the Aeris 3A 79° → 87° → ... for 12 s at 14 m/s in the rig.
        var hold = Math.Clamp(-error / 10 + 0.05 * yawRate, -0.5, 0.5);
        // Gently: a jet's engine lags, and 0.1 + 0.08 a m/s short took the Aeris 3A to 19 m/s in the rig.
        var throttle = speed >= TaxiSpeed ? 0 : Math.Clamp(0.04 + 0.02 * (TaxiSpeed - speed), 0, 0.4);
        if (Finished)
            return new GroundCommand(0, 0, true, Finished && Result is { } r ? "Ground test done: " + Describe(r) : "Ground test stopped: " + Problem);
        if (t - _start > MaxTime.TotalSeconds)
            return Stop("it took more than two minutes");
        if (Math.Abs(error) > 25)
            return Stop(string.Create(CultureInfo.InvariantCulture, $"it swung {Math.Abs(error):0}° off the runway heading"));

        switch (_step)
        {
            case GroundStep.Roll:
                // Nose wheel straight while it speeds up: how it veers by itself (steered back past 5°, measured until then).
                if (speed >= 3 && _driftFrom == null)
                    (_driftFrom, _driftFromHeading) = (t, heading);
                if (!_veered && Math.Abs(error) > 5 && _driftFrom is { } f0 && t > f0)
                {
                    _veered = true;
                    _drift = Pilot.Wrap(heading - _driftFromHeading!.Value) / (t - f0);
                }
                if (speed >= TaxiSpeed - 2)
                {
                    if (!_veered && _driftFrom is { } f && t > f)
                        _drift = Pilot.Wrap(heading - _driftFromHeading!.Value) / (t - f);
                    Go(GroundStep.Recover, t); // straight first, then the pulse
                    _pulseTicks = -1; // the recover step leads to the pulse once
                    return new GroundCommand(throttle, hold, false, "Ground test: lining up for the steering pulse.");
                }
                if (inStep > 40)
                    return Stop(string.Create(CultureInfo.InvariantCulture, $"it didn't get to {TaxiSpeed - 2:0} m/s in 40 s"));
                return new GroundCommand(throttle, _veered ? hold : 0, false, string.Create(CultureInfo.InvariantCulture,
                    $"Ground test: rolling with the nose wheel straight ({speed:0.0} m/s)."));
            case GroundStep.Pulse:
                _pulseSpeedSum += speed;
                _pulseTicks++;
                if (inStep < PulseSeconds)
                    return new GroundCommand(throttle, PulseSteer, false, "Ground test: steering pulse.");
                var averageSpeed = Math.Max(_pulseSpeedSum / Math.Max(_pulseTicks, 1), 1);
                _steerRate = Math.Abs(Pilot.Wrap(heading - _pulseFromHeading)) / inStep / PulseSteer * (10 / averageSpeed);
                Go(GroundStep.Recover, t);
                return new GroundCommand(throttle, hold, false, "Ground test: back onto the runway heading.");
            case GroundStep.Recover:
                if (Math.Abs(error) < 2)
                {
                    if (double.IsNaN(_steadySince))
                        _steadySince = t;
                }
                else
                    _steadySince = double.NaN;
                if ((!double.IsNaN(_steadySince) && t - _steadySince >= 2) || inStep > 12)
                {
                    _steadySince = double.NaN;
                    if (_pulseTicks == -1)
                    {
                        Go(GroundStep.Pulse, t);
                        (_pulseFromHeading, _pulseSpeedSum, _pulseTicks) = (heading, 0, 0);
                        return new GroundCommand(throttle, PulseSteer, false, "Ground test: steering pulse.");
                    }
                    Go(GroundStep.Brake, t);
                    _brakeFromSpeed = speed;
                    return new GroundCommand(0, hold, true, "Ground test: brakes.");
                }
                return new GroundCommand(throttle, hold, false, "Ground test: back onto the runway heading.");
            case GroundStep.Brake:
                if (speed < 0.5)
                {
                    _braking = _brakeFromSpeed / Math.Max(inStep, 0.1);
                    Result = new GroundResult(Math.Round(_steerRate, 1), Math.Round(_drift, 2), Math.Round(_braking, 2));
                    _step = GroundStep.Done;
                    return new GroundCommand(0, 0, true, "Ground test done: " + Describe(Result.Value));
                }
                return new GroundCommand(0, hold, true, string.Create(CultureInfo.InvariantCulture, $"Ground test: brakes ({speed:0.0} m/s)."));
        }
        return new GroundCommand(0, 0, true, "Ground test.");
    }

    public string Describe(GroundResult r) => Describe(craft, r);

    public static string Describe(string craft, GroundResult r) => string.Create(CultureInfo.InvariantCulture,
        $"Ground test of the {craft}: nose wheel turns it {r.SteerRate:0.0} °/s per full lock at 10 m/s ({r.SteerRate / PlaneSetup.ReferenceSteerRate:0.0}× the Aeris 3A), " +
        $"veers {Math.Abs(r.Drift):0.0} °/s {(r.Drift >= 0 ? "right" : "left")} with the wheel straight, brakes stop it at {r.Braking:0.0} m/s².");

    private GroundCommand Stop(string why)
    {
        Problem = why;
        _step = GroundStep.Stopped;
        return new GroundCommand(0, 0, true, "Ground test stopped: " + why);
    }

    private void Go(GroundStep step, double t) => (_step, _stepStart) = (step, t);
}

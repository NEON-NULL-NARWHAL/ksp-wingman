// KSP Wingman · src/KspWingman.Mcp/FlightTest.cs
// The flight test (the owner's idea: learn each plane from real tests, not guesses): a short test card flown by the
// Director, pulses on the stick and a few steady maneuvers, measuring how the plane handles. Pure like Landing: it
// reads the plane's numbers and says what to fly, so it's tested without KSP.

using System.Globalization;

namespace KspWingman.Mcp;

public enum TestStep { Settle, PitchPulse, PitchRecover, RollPulse, RollRecover, Turn, Slow, Accelerate, Done, Stopped }

/// <summary>One step's answer: what the pilot flies (Target), or the stick itself (Inputs, for the pulses), and what's going on.</summary>
public readonly record struct TestCommand(PlaneTarget? Target, PlaneInputs? Inputs, string Doing);

/// <summary>
/// The card, about four minutes at 150 m/s, 1,500 m above the ground: settle level (the throttle and elevator that hold
/// it), a tenth of a stick of elevator for a second, recover, the same on the ailerons, recover, a 180° turn (so it
/// comes back toward where it started), slow down with the throttle shut holding its height until it can't (its
/// slowest speed), then full throttle. Stops itself below 400 m above the ground or after 8 minutes.
/// </summary>
public sealed class FlightTest(string craft)
{
    public const double TestSpeed = 150, TestHeight = 1500, MinHeight = 400, Pulse = 0.1, PulseSeconds = 1;

    /// <summary>The least height above the ground (m) to start from: just after take_off (it climbs from there).</summary>
    public const double StartHeight = 90;
    public static readonly TimeSpan MaxTime = TimeSpan.FromMinutes(8);

    private TestStep _step = TestStep.Settle;
    private double _stepStart, _start = double.NaN;
    private double _heading, _altitude;
    private double? _lastTime, _lastHeading;
    private double _stableSince = double.NaN;
    private readonly List<(double Throttle, double Elevator, double Aileron)> _trim = [];
    private double _throttle = 0.2, _elevator, _aileron;
    private double _pulseSpeed, _pulseFrom;
    private readonly List<double> _turnRates = [];
    private double? _at140, _at110, _slowStart, _slowStartSpeed;
    private double _highPitchSince = double.NaN, _slowest = double.MaxValue;
    private double? _accelFrom, _accelFromSpeed;

    // Results so far.
    private double _pitchRate, _rollRate, _turnRate, _minSpeed, _deceleration, _acceleration, _cruiseThrottle;

    public TestStep Step => _step;
    public string Craft => craft;

    /// <summary>The measurements, once the card is done.</summary>
    public PlaneTest? Result { get; private set; }

    /// <summary>Why it stopped early (too low, too long), or null.</summary>
    public string? Problem { get; private set; }

    public bool Finished => _step is TestStep.Done or TestStep.Stopped;

    /// <param name="t">Seconds (any clock that only goes forward).</param>
    /// <param name="last">What the pilot (or the card) set the controls to on the last tick.</param>
    public TestCommand Next(PlaneState s, double t, PlaneInputs last)
    {
        if (double.IsNaN(_start))
        {
            _start = _stepStart = t;
            _heading = s.Heading;
            var ground = s.HeightAboveGround == double.MaxValue ? 0 : s.Altitude - s.HeightAboveGround;
            _altitude = Math.Max(s.Altitude, ground + TestHeight);
        }
        var dt = _lastTime is { } lt ? t - lt : 0;
        var turnRate = dt > 0.005 && _lastHeading is { } lh ? Pilot.Wrap(s.Heading - lh) / dt : 0;
        (_lastTime, _lastHeading) = (t, s.Heading);
        var inStep = t - _stepStart;

        if (Finished)
            return Hold(_step == TestStep.Done ? "Flight test done: " + Result!.Describe() : "Flight test stopped: " + Problem);
        // While it climbs to the test height (right after take_off it's 100 m up) only a plane going down that low stops it.
        if ((s.HeightAboveGround < MinHeight && _step != TestStep.Settle) || (s.HeightAboveGround < StartHeight * 0.66 && s.VerticalSpeed < 0))
            return Stop(string.Create(CultureInfo.InvariantCulture, $"too low ({s.HeightAboveGround:0} m above the ground)"));
        if (t - _start > MaxTime.TotalSeconds)
            return Stop("it took more than 8 minutes");

        var level = new PlaneTarget(_heading, _altitude, TestSpeed);
        switch (_step)
        {
            case TestStep.Settle:
            {
                var stable = Math.Abs(s.Altitude - _altitude) < 40 && Math.Abs(s.Speed - TestSpeed) < 6 && Math.Abs(s.Roll) < 5 && Math.Abs(s.VerticalSpeed) < 3;
                if (!stable)
                {
                    _stableSince = double.NaN;
                    _trim.Clear();
                }
                else
                {
                    if (double.IsNaN(_stableSince))
                        _stableSince = t;
                    _trim.Add((last.Throttle, last.Pitch, last.Roll));
                }
                // Six steady seconds give the trim; after two minutes, whatever it has.
                if ((stable && t - _stableSince >= 6) || inStep > 120)
                {
                    if (_trim.Count > 0)
                        (_throttle, _elevator, _aileron) = (_trim.Average(x => x.Throttle), _trim.Average(x => x.Elevator), _trim.Average(x => x.Aileron));
                    else
                        (_throttle, _elevator, _aileron) = (last.Throttle, last.Pitch, last.Roll);
                    _cruiseThrottle = _throttle;
                    Go(TestStep.PitchPulse, t);
                    (_pulseSpeed, _pulseFrom) = (s.Speed, s.Pitch);
                    return Stick(Pulse, 0, "Flight test: elevator pulse.");
                }
                return new TestCommand(level, null, string.Create(CultureInfo.InvariantCulture,
                    $"Flight test: settling level at {_altitude:0} m and {TestSpeed:0} m/s ({(stable ? "steady" : "not steady yet")})."));
            }
            case TestStep.PitchPulse:
                // How far the nose came up over the pulse (sturdier than the peak of a rate from 20 readings a second).
                if (inStep < PulseSeconds)
                    return Stick(Pulse, 0, "Flight test: elevator pulse.");
                _pitchRate = Normalize((s.Pitch - _pulseFrom) / inStep, _pulseSpeed);
                Go(TestStep.PitchRecover, t);
                return new TestCommand(level, null, "Flight test: back to level after the elevator pulse.");
            case TestStep.PitchRecover:
            case TestStep.RollRecover:
                if (inStep > 20 || (inStep > 6 && Math.Abs(s.Roll) < 5 && Math.Abs(s.VerticalSpeed) < 3 && Math.Abs(s.Altitude - _altitude) < 60))
                {
                    if (_step == TestStep.PitchRecover)
                    {
                        Go(TestStep.RollPulse, t);
                        (_pulseSpeed, _pulseFrom) = (s.Speed, s.Roll);
                        return Stick(0, Pulse, "Flight test: aileron pulse.");
                    }
                    Go(TestStep.Turn, t);
                    _heading = (_heading + 180) % 360;
                    return new TestCommand(new PlaneTarget(_heading, _altitude, TestSpeed), null, "Flight test: a 180° turn at 30° of bank.");
                }
                return new TestCommand(level, null, "Flight test: back to level.");
            case TestStep.RollPulse:
                if (inStep < PulseSeconds)
                    return Stick(0, Pulse, "Flight test: aileron pulse.");
                _rollRate = Normalize(Pilot.Wrap(s.Roll - _pulseFrom) / inStep, _pulseSpeed);
                Go(TestStep.RollRecover, t);
                return new TestCommand(level, null, "Flight test: back to level after the aileron pulse.");
            case TestStep.Turn:
                if (Math.Abs(s.Roll) is > 25 and < 35)
                    _turnRates.Add(Math.Abs(turnRate));
                if (Math.Abs(Pilot.Wrap(_heading - s.Heading)) < 10 || inStep > 90)
                {
                    _turnRate = _turnRates.Count > 0 ? _turnRates.Average() : 0;
                    Go(TestStep.Slow, t);
                    (_slowStart, _slowStartSpeed) = (t, s.Speed);
                }
                return new TestCommand(level, null, "Flight test: a 180° turn at 30° of bank.");
            case TestStep.Slow:
            {
                // Throttle shut (a target speed of 0), height held: how fast it slows, and where it can't hold its height.
                if (s.Speed <= 140 && _at140 == null)
                    _at140 = t;
                if (s.Speed <= 110 && _at110 == null)
                    _at110 = t;
                _slowest = Math.Min(_slowest, s.Speed);
                if (s.Pitch >= 15)
                {
                    if (double.IsNaN(_highPitchSince))
                        _highPitchSince = t;
                }
                else
                    _highPitchSince = double.NaN;
                var cantHold = (!double.IsNaN(_highPitchSince) && t - _highPitchSince >= 1) || s.Altitude < _altitude - 80 || s.Speed < 40 || inStep > 150;
                if (cantHold)
                {
                    _minSpeed = _slowest;
                    _deceleration = _at140 is { } a && _at110 is { } b && b > a ? 30 / (b - a)
                        : (_slowStartSpeed!.Value - s.Speed) / Math.Max(t - _slowStart!.Value, 1);
                    Go(TestStep.Accelerate, t);
                    return Accelerate(s, t);
                }
                return new TestCommand(new PlaneTarget(_heading, _altitude, 0), null, string.Create(CultureInfo.InvariantCulture,
                    $"Flight test: slowing with the throttle shut, holding {_altitude:0} m ({s.Speed:0} m/s, nose {s.Pitch:0}° up)."));
            }
            case TestStep.Accelerate:
                return Accelerate(s, t);
        }
        return Hold("Flight test.");
    }

    private TestCommand Accelerate(PlaneState s, double t)
    {
        // Nose down a little until it's well above its slowest, then level: full throttle either way (a target of 150).
        var safe = Math.Max(_minSpeed * 1.3, 60);
        if (s.Speed >= safe && _accelFrom == null)
            (_accelFrom, _accelFromSpeed) = (t, s.Speed);
        var done = (_accelFrom != null && s.Speed >= Math.Min(_accelFromSpeed!.Value + 30, 140)) || t - _stepStart > 120;
        if (done)
        {
            _acceleration = _accelFrom is { } from && t > from ? (s.Speed - _accelFromSpeed!.Value) / (t - from) : 0;
            Result = new PlaneTest(craft, Math.Round(_pitchRate, 1), Math.Round(_rollRate, 1), Math.Round(_turnRate, 2), Math.Round(_minSpeed),
                Math.Round(_deceleration, 2), Math.Round(_acceleration, 2), Math.Round(_cruiseThrottle, 3));
            _step = TestStep.Done;
            return Hold("Flight test done: " + Result.Describe());
        }
        var target = s.Speed < safe
            ? new PlaneTarget(_heading, _altitude, TestSpeed, ClimbRate: -2)
            : new PlaneTarget(_heading, _altitude, TestSpeed);
        return new TestCommand(target, null, string.Create(CultureInfo.InvariantCulture, $"Flight test: full throttle ({s.Speed:0} m/s)."));
    }

    /// <summary>A pulse's rate (degrees over its second) for a full stick at 150 m/s (the surfaces bite with the square of the speed).</summary>
    public static double Normalize(double peak, double speed) => peak / Pulse / Math.Pow(Math.Max(speed, 30) / TestSpeed, 2);

    private TestCommand Stick(double elevator, double aileron, string doing) =>
        new(null, new PlaneInputs((float)(_elevator + elevator), (float)(_aileron + aileron), 0, (float)_throttle), doing);

    private TestCommand Hold(string doing) => new(new PlaneTarget(_heading, _altitude, TestSpeed), null, doing);

    private TestCommand Stop(string why)
    {
        Problem = why;
        _step = TestStep.Stopped;
        return Hold("Flight test stopped: " + why);
    }

    private void Go(TestStep step, double t) => (_step, _stepStart) = (step, t);
}

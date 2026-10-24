// KSP Wingman · src/KspWingman.Mcp/Handling.cs
// Each plane's own setup (the owner's idea): measured in a flight test (FlightTest), then corrected by how its landings
// went. The pilot, the landing and take_off read it instead of the Aeris 3A's numbers. Pure, tested without KSP.

using System.Globalization;

namespace KspWingman.Mcp;

/// <summary>
/// What a flight test measured. Rates are degrees a second for a full stick at 150 m/s (the pulses are a tenth of a
/// stick at whatever speed, scaled by the square of the speed); speeds m/s; accelerations m/s².
/// </summary>
/// <param name="PitchRate">How fast the nose comes up for the elevator.</param>
/// <param name="RollRate">How fast it rolls for the ailerons.</param>
/// <param name="TurnRate">Degrees of heading a second in a 30° banked turn at about 150 m/s.</param>
/// <param name="MinSpeed">The slowest it held its height at (pitch at most 15°).</param>
/// <param name="Deceleration">How fast it slows with the throttle shut, level, from 140 to 110 m/s.</param>
/// <param name="Acceleration">How fast it speeds up at full throttle, level, from slow.</param>
/// <param name="CruiseThrottle">The throttle that holds 150 m/s level.</param>
/// <param name="SteerRate">From the ground test (0: none): degrees a second for a full nose wheel at 10 m/s.</param>
/// <param name="Drift">From the ground test: how it veers with the nose wheel straight (°/s, + right).</param>
/// <param name="Braking">From the ground test: how fast the brakes stop it (m/s²).</param>
public sealed record PlaneTest(string Craft, double PitchRate, double RollRate, double TurnRate, double MinSpeed, double Deceleration,
    double Acceleration, double CruiseThrottle, double SteerRate = 0, double Drift = 0, double Braking = 0)
{
    public GroundResult? Ground => SteerRate > 0 ? new GroundResult(SteerRate, Drift, Braking) : null;

    /// <summary>One line for people and the debrief: the numbers, and how they compare with the Aeris 3A.</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"Flight test of the {Craft}: pitch {PitchRate:0} °/s per full stick ({PitchRate / PlaneSetup.ReferencePitchRate:0.0}× the Aeris 3A), " +
        $"roll {RollRate:0} °/s ({RollRate / PlaneSetup.ReferenceRollRate:0.0}×), turns {TurnRate:0.0} °/s at 30° of bank, " +
        $"slowest {MinSpeed:0} m/s, slows {Deceleration:0.0} m/s² at idle, speeds up {Acceleration:0.0} m/s² at full throttle, " +
        $"cruises at {CruiseThrottle * 100:0}% throttle at 150 m/s.") + (Ground is { } g ? " " + GroundTest.Describe(Craft, g) : "");
}

/// <summary>How one of its finals went: centerline crossings (weaving) and how high it was 1 km out (m, + is high).</summary>
public readonly record struct FinalReport(int Weaves, double High);

/// <summary>
/// The pilot's numbers for one plane. Defaults are the stock Aeris 3A's (the plane everything was tuned on).
/// </summary>
/// <param name="PitchGain">Multiplies the elevator (a plane whose nose comes up twice as fast gets 0.5).</param>
/// <param name="RollGain">Multiplies the ailerons.</param>
/// <param name="CircuitSpeed">Speed (m/s) on the way to the final (slower for a plane that's hard to slow down).</param>
/// <param name="SinkCorrection">How much faster than the slope (m/s) it may sink to get back down onto it.</param>
/// <param name="SteerGain">Multiplies the nose wheel on the ground (take_off, rollout, taxi), from the ground test.</param>
/// <param name="SlowingRate">How fast it can be planned to slow down on the glide slope (m/s²): part of what the test
/// measured at idle, level, less what sinking down the slope gives back.</param>
/// <param name="Why">What it was worked out from, for status and the debrief.</param>
public sealed record PlaneSetup(double PitchGain = 1, double RollGain = 1, double CruiseThrottle = 0.2, double RotateSpeed = 55,
    double ApproachSpeed = Landing.ApproachSpeed, double LandingSpeed = Landing.LandingSpeed, double CircuitSpeed = 120,
    double SinkCorrection = 6, string Why = "the Aeris 3A's numbers (no flight test)", double SteerGain = 1, double SlowingRate = 0.3,
    double Braking = PlaneSetup.ReferenceBraking)
{
    /// <summary>How much faster than the approach speed it may join the final (m/s): it slows down along the slope.</summary>
    public const double FastFinal = 20;

    /// <summary>Where it must be down to its landing speed (m before touchdown): the last stretch is flown steady.</summary>
    public const double SteadyFrom = 1500;

    /// <summary>
    /// The speed to fly on final, <paramref name="toGo"/> m before touchdown (the owner's idea: approach faster and slow
    /// down more on final). As fast as it can still slow to its landing speed by <see cref="SteadyFrom"/> at its
    /// <see cref="SlowingRate"/> (v² = landing² + 2·a·d), never more than its approach speed + <see cref="FastFinal"/>.
    /// </summary>
    public double FinalSpeed(double toGo) =>
        Math.Min(ApproachSpeed + FastFinal, Math.Sqrt(LandingSpeed * LandingSpeed + 2 * SlowingRate * Math.Max(0, toGo - SteadyFrom)));

    public static readonly PlaneSetup Default = new();

    /// <summary>
    /// The Aeris 3A in the same terms, from its own flight test in the rig (FlightLab test, 2026-10-24, twice: pitch 55
    /// °/s, roll 408 °/s, slowest 54 m/s, 1.2 m/s² slowing at idle, 10% throttle at 150 m/s). A first guess from an old
    /// probe note was 18 and 160: the real test found the nose 3× quicker than guessed.
    /// </summary>
    public const double ReferencePitchRate = 55, ReferenceRollRate = 410;

    /// <summary>
    /// The Aeris 3A's nose wheel, from its ground test in the rig (°/s per full lock at 10 m/s, pulsed at ~13 m/s:
    /// 26.6; at 18 m/s it was 14.9, the wheel bites less the faster it rolls, so the test holds taxi speed).
    /// </summary>
    public const double ReferenceSteerRate = 26;

    /// <summary>The Aeris 3A's brakes from taxi speed (m/s², its ground test: 9.0-9.2).</summary>
    public const double ReferenceBraking = 9;

    /// <summary>The nose wheel's gain from a ground test (a wheel that turns it twice as hard gets half).</summary>
    public PlaneSetup WithGround(GroundResult ground) => this with
    {
        SteerGain = Math.Round(Math.Clamp(ReferenceSteerRate / Math.Max(ground.SteerRate, 0.5), 0.4, 2.5), 2),
        Braking = Math.Clamp(ground.Braking, 1, 20),
        Why = Why == Default.Why ? "its ground test" : Why + " and its ground test",
    };

    /// <summary>Finals that weave (this many centerline crossings) or arrive this high (m, 1 km out) count against the setup.</summary>
    public const int WeavingFinal = 2;

    public const double HighFinal = 30;

    /// <summary>
    /// From a flight test (null: the defaults) and the plane's last finals (most recent last). Each weaving final among
    /// the last five softens the ailerons by 15%; each high one slows the circuit and lets it sink harder. Everything is
    /// kept within limits a plane can fly.
    /// </summary>
    public static PlaneSetup From(PlaneTest? test, IReadOnlyList<FinalReport>? finals = null)
    {
        var setup = Default;
        var why = new List<string>();
        if (test != null)
        {
            var landing = Math.Clamp(Math.Round(1.35 * test.MinSpeed), 60, 100);
            setup = setup with
            {
                PitchGain = Math.Round(Math.Clamp(ReferencePitchRate / Math.Max(test.PitchRate, 1), 0.4, 2.5), 2),
                RollGain = Math.Round(Math.Clamp(ReferenceRollRate / Math.Max(test.RollRate, 1), 0.4, 2.5), 2),
                CruiseThrottle = Math.Round(Math.Clamp(test.CruiseThrottle, 0.05, 1), 2),
                RotateSpeed = Math.Clamp(Math.Round(test.MinSpeed), 40, 90),
                LandingSpeed = landing,
                ApproachSpeed = landing + 15,
                // Hard to slow down (a slick or heavy plane): be at a slower speed before the final starts.
                CircuitSpeed = test.Deceleration < 0.8 ? Math.Min(120, landing + 25) : 120,
                // Sinking 4° down the slope gives back g·sin 4° ≈ 0.68 m/s²; of the rest, plan on 60%.
                SlowingRate = Math.Round(Math.Clamp((test.Deceleration - 0.68) * 0.6, 0.15, 1.5), 2),
            };
            why.Add("its flight test");
            if (test.Ground is { } ground)
                setup = setup with { SteerGain = setup.WithGround(ground).SteerGain, Braking = setup.WithGround(ground).Braking };
        }
        var last = (finals ?? []).TakeLast(5).ToList();
        var weaving = last.Count(f => f.Weaves >= WeavingFinal);
        var high = last.Count(f => f.High > HighFinal);
        if (weaving > 0)
        {
            setup = setup with { RollGain = Math.Round(Math.Max(0.3, setup.RollGain * Math.Pow(0.85, weaving)), 2) };
            why.Add($"{weaving} weaving final{(weaving == 1 ? "" : "s")}");
        }
        if (high > 0)
        {
            setup = setup with
            {
                CircuitSpeed = Math.Max(setup.ApproachSpeed, setup.CircuitSpeed - 10 * high),
                SinkCorrection = Math.Min(10, setup.SinkCorrection + high),
            };
            why.Add($"{high} high final{(high == 1 ? "" : "s")}");
        }
        return why.Count == 0 ? setup : setup with { Why = string.Join(" and ", why) };
    }

    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"elevator ×{PitchGain:0.00}, ailerons ×{RollGain:0.00}, nose wheel ×{SteerGain:0.00}, rotate at {RotateSpeed:0} m/s, circuit {CircuitSpeed:0} m/s, " +
        $"approach {ApproachSpeed:0} m/s, landing {LandingSpeed:0} m/s (from {Why})");
}

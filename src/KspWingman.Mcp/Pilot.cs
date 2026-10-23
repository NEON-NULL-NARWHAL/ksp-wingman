// KSP Wingman · src/KspWingman.Mcp/Pilot.cs
// The plane pilot: holds a heading, altitude and speed with the elevator, ailerons and throttle (kRPC's own autopilot,
// made for rockets, dove a plane into the ground).

namespace KspWingman.Mcp;

/// <summary>What the pilot reads each tick: angles in degrees, speeds in m/s, altitude above sea level in m.</summary>
public readonly record struct PlaneState(double Pitch, double Roll, double Heading, double VerticalSpeed, double Altitude, double Speed, double HeightAboveGround = double.MaxValue);

/// <summary>
/// What it asks for: heading (degrees), altitude (m above sea level), speed (m/s). A climb rate (m/s) replaces the
/// altitude when given (a glide slope, a flare). Landing turns off the "too low, pull up" rule and banks at most 20°.
/// </summary>
public readonly record struct PlaneTarget(double Heading, double Altitude, double Speed, double? ClimbRate = null, bool Landing = false);

/// <summary>Control inputs, kRPC's ranges: pitch and roll -1..1 (positive is nose up and roll right), throttle 0..1.</summary>
public readonly record struct PlaneInputs(float Pitch, float Roll, float Yaw, float Throttle);

/// <summary>
/// Measured on the stock Aeris 3A at ~260 m/s (tests/FlightLab probe): 0.3 of pitch for 0.8 s gave +13° of pitch, 0.3 of
/// roll +115° of roll. So the inputs stay small and every axis is damped by its own rate. The chain, slowest outside:
/// altitude → climb rate → pitch → elevator; heading → bank → ailerons; speed → throttle.
/// </summary>
public sealed class Pilot
{
    public const double MaxBank = 30, MaxClimbRate = 15, MaxPitchUp = 20, MaxPitchDown = -12;
    public const float MaxSurface = 0.25f; // never more than a quarter stick: this plane needs little

    /// <summary>The speed the gains were tuned at (m/s), and the height (m above ground) below which it won't descend.</summary>
    public const double TunedSpeed = 150, SafeHeight = 150;

    /// <summary>The most it flies (m/s) while turning more than 30°.</summary>
    public const double TurningSpeed = 160;

    private double? _lastPitch, _lastRoll;
    private double _pitchTrim, _throttleTrim = 0.2; // a slick jet cruises on little throttle (the rig's Aeris: under 0.2 at 120 m/s)

    /// <summary>One step of <paramref name="dt"/> seconds. Keeps a little state (rates, trim), so one Pilot per flight.</summary>
    public PlaneInputs Step(PlaneState s, PlaneTarget t, double dt)
    {
        dt = Math.Clamp(dt, 0.02, 0.5);
        var pitchRate = _lastPitch is { } p ? (s.Pitch - p) / dt : 0;
        var rollRate = _lastRoll is { } r ? Wrap(s.Roll - r) / dt : 0;
        _lastPitch = s.Pitch;
        _lastRoll = s.Roll;

        // The surfaces bite with the square of the speed (tuned at 150 m/s; at twice that the plane porpoised into the sea).
        var authority = Math.Clamp(Math.Pow(TunedSpeed / Math.Max(s.Speed, 30), 2), 0.15, 2);

        // Too low and going down: wings level and climb, whatever was asked.
        var tooLow = !t.Landing && s.HeightAboveGround < SafeHeight && s.VerticalSpeed < 0;

        // Altitude → climb rate → pitch. Banked, a plane needs a little more pitch to hold its height.
        var climb = Math.Clamp(t.ClimbRate ?? 0.15 * (t.Altitude - s.Altitude), -MaxClimbRate, MaxClimbRate);
        var pitchTarget = Math.Clamp(2 + 0.6 * (climb - s.VerticalSpeed), MaxPitchDown, MaxPitchUp);
        if (tooLow)
            pitchTarget = Math.Max(pitchTarget, 10);
        var pitchError = pitchTarget - s.Pitch;
        _pitchTrim = Math.Clamp(_pitchTrim + 0.002 * pitchError * dt, -0.1, 0.1); // slow: what the plane needs to fly level
        var elevator = authority * (0.012 * pitchError - 0.006 * pitchRate) + _pitchTrim;

        // Heading → bank → ailerons.
        var maxBank = t.Landing ? 20 : MaxBank;
        var bankTarget = tooLow ? 0 : Math.Clamp(1.2 * Wrap(t.Heading - s.Heading), -maxBank, maxBank);
        var aileron = authority * (0.006 * Wrap(bankTarget - s.Roll) - 0.003 * rollRate);

        // Speed → throttle (slow and smooth). In a big turn, slow down first: at 270 m/s a 30° bank turns 13 km wide, and
        // the rig's agent circled past the player it was meant to join.
        // A slow trim (what it takes to hold the speed) plus a direct share of the error: with the trim alone, the throttle
        // wound up and the agent shot past the player it was joining at 50 m/s too fast.
        var speedTarget = Math.Abs(Wrap(t.Heading - s.Heading)) > 30 ? Math.Min(t.Speed, TurningSpeed) : t.Speed;
        var speedError = speedTarget - s.Speed;
        _throttleTrim = Math.Clamp(_throttleTrim + 0.003 * speedError * dt, 0, 1);
        var throttle = Math.Clamp(_throttleTrim + 0.025 * speedError, 0, 1);

        return new PlaneInputs(
            (float)Math.Clamp(elevator, -MaxSurface, MaxSurface),
            (float)Math.Clamp(aileron, -MaxSurface, MaxSurface),
            0,
            (float)throttle);
    }

    /// <summary>An angle difference in -180..180.</summary>
    public static double Wrap(double degrees) => ((degrees % 360) + 540) % 360 - 180;
}

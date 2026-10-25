// KSP Wingman · src/KspWingman.Mcp/Formation.cs
// Flying formation: from where the leader is (as LMP shows them), the heading, altitude and speed that keep the agent in
// its slot beside them. Pure arithmetic, so it's tested without KSP.
// Guide:   Meat and Potatoes/03-Pilot-And-Handling.md

namespace KspWingman.Mcp;

/// <summary>A position over a body: degrees and meters above sea level.</summary>
public readonly record struct Geo(double Latitude, double Longitude, double Altitude);

/// <summary>Where the agent sits relative to the leader, in meters: to their right (negative: left), behind, above.</summary>
public readonly record struct Slot(double Right, double Behind, double Up);

/// <summary>One step's answer: what to fly, and how far the agent is from its slot and from the leader.</summary>
public readonly record struct FormationStep(PlaneTarget Target, double SlotDistance, double LeaderDistance, double LeaderSpeed);

public sealed class Formation
{
    /// <summary>Never closer to the leader than this (m): any nearer and the agent climbs away and slows down.</summary>
    public const double MinSeparation = 30;

    /// <summary>Below this (m/s) the leader isn't flying: there's no heading to follow.</summary>
    public const double MinLeaderSpeed = 20;

    /// <summary>Never faster than this (m/s), even catching up: the plane gets twitchy (the rig's porpoise into the sea).</summary>
    public const double MaxSpeed = 250;

    /// <summary>Further from the slot than this (m), fly straight at it instead of alongside the leader.</summary>
    public const double InterceptDistance = 2000;

    /// <summary>How fast the plane loses speed with the throttle shut (m/s²), roughly: the stock Aeris 3A, level.</summary>
    public const double Deceleration = 0.8;

    /// <summary>The leader's velocity is averaged over about this many seconds.</summary>
    public const double SmoothingSeconds = 2;

    private Geo? _lastLeader;
    private bool _seen;
    private double _vNorth, _vEast;

    /// <summary>
    /// One step, <paramref name="dt"/> seconds after the last. The leader's velocity comes from how they moved since then
    /// (smoothed: LMP moves other players' vessels in small jumps). Null while the leader isn't flying (yet).
    /// </summary>
    public FormationStep? Step(double bodyRadius, Geo agent, Geo leader, double dt, Slot slot)
    {
        if (_lastLeader is { } last && dt > 0)
        {
            var (north, east) = Offset(bodyRadius, last, leader);
            // Over about 2 s: LMP moves other players in jumps, and smoothing each 50 ms step read a steady 120 m/s
            // leader as anything from 110 to 143.
            var smoothing = _seen ? dt / (SmoothingSeconds + dt) : 1;
            _seen = true;
            _vNorth += smoothing * (north / dt - _vNorth);
            _vEast += smoothing * (east / dt - _vEast);
        }
        _lastLeader = leader;
        var leaderSpeed = Math.Sqrt(_vNorth * _vNorth + _vEast * _vEast);
        if (leaderSpeed < MinLeaderSpeed)
            return null;

        // The leader's heading, and its forward and right directions (north, east).
        var heading = Math.Atan2(_vEast, _vNorth);
        var (fN, fE) = (Math.Cos(heading), Math.Sin(heading));
        var (rN, rE) = (-fE, fN);

        // Where the slot is, seen from the agent.
        var (lN, lE) = Offset(bodyRadius, agent, leader);
        var sN = lN + slot.Right * rN - slot.Behind * fN;
        var sE = lE + slot.Right * rE - slot.Behind * fE;
        var along = sN * fN + sE * fE; // > 0: the slot is ahead, speed up
        var across = sN * rN + sE * rE; // > 0: the slot is to the right, turn right

        var headingDeg = heading * 180 / Math.PI;
        // Aim at a point about 3 s ahead on the slot's line: a plane needs seconds to bank, and aiming 150 m ahead made
        // the agent weave across the leader's track for kilometers.
        var turn = Math.Clamp(Math.Atan2(across, Math.Max(3 * leaderSpeed, 300)) * 180 / Math.PI, -40, 40);
        // Catch up no faster than it can slow down again by the slot (a jet with the throttle shut loses under 1 m/s each
        // second): at 2 km behind, 57 m/s quicker; at 200 m, 18.
        var closing = Math.Sign(along) * Math.Sqrt(2 * Deceleration * Math.Abs(along));
        var speed = Math.Clamp(leaderSpeed + Math.Clamp(closing, -25, 100), 60, MaxSpeed);
        var altitude = leader.Altitude + slot.Up;
        var leaderDistance = Math.Sqrt(lN * lN + lE * lE + Math.Pow(leader.Altitude - agent.Altitude, 2));
        if (leaderDistance < MinSeparation)
        {
            altitude = Math.Max(altitude, leader.Altitude + MinSeparation + 10); // above them, and drop back
            speed = Math.Max(60, leaderSpeed - 15);
        }
        var slotDistance = Math.Sqrt(sN * sN + sE * sE + Math.Pow(altitude - agent.Altitude, 2));
        var course = headingDeg + turn;
        if (Math.Sqrt(sN * sN + sE * sE) > InterceptDistance)
        {
            // Far off (or meeting them head-on): straight for the slot, as fast as allowed; in formation within 2 km.
            course = Math.Atan2(sE, sN) * 180 / Math.PI;
            speed = along > 0 ? MaxSpeed : Math.Max(leaderSpeed, 120);
        }
        return new FormationStep(new PlaneTarget((course + 360) % 360, altitude, speed), slotDistance, leaderDistance, leaderSpeed);
    }

    /// <summary>How far north and east <paramref name="to"/> is from <paramref name="from"/> (m), on a sphere.</summary>
    public static (double North, double East) Offset(double bodyRadius, Geo from, Geo to)
    {
        var r = bodyRadius + (from.Altitude + to.Altitude) / 2;
        var dLon = Pilot.Wrap(to.Longitude - from.Longitude);
        var north = (to.Latitude - from.Latitude) * Math.PI / 180 * r;
        var east = dLon * Math.PI / 180 * r * Math.Cos((from.Latitude + to.Latitude) / 2 * Math.PI / 180);
        return (north, east);
    }
}

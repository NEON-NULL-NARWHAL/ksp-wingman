// KSP Wingman · src/KspWingman.Mcp/Exploring.cs
// Explore (the owner's idea: a freer free flight, agents learning the map): the agent picks where to go, a bearing and a
// distance from where it is; code checks it's in range and that the fuel gets it there and home with a reserve, flies
// there, and notes what it found (the biome, how high the ground is). Pure, tested without KSP.
// Guide:   Meat and Potatoes/07-Explore-And-Fuel.md

using System.Globalization;

namespace KspWingman.Mcp;

/// <summary>A place an agent went to and what it found there.</summary>
public sealed record Place(double Latitude, double Longitude, double Ground, string Biome)
{
    public string Describe(double bodyRadius) => string.Create(CultureInfo.InvariantCulture,
        $"{Biome}, ground {Ground:0} m up, {Exploring.FromKsc(bodyRadius, Latitude, Longitude)}");
}

public static class Exploring
{
    /// <summary>How far from KSC explore may go (km), and how long one leg may be.</summary>
    public const double MaxRangeKm = 60, MaxLegKm = 30, MinLegKm = 1;

    /// <summary>The least height above the ground there (m), and close enough to count as arrived (m).</summary>
    public const double MinClearance = 500, Arrived = 1500;

    /// <summary>Without a known burn yet (the first minutes of a flight), no further from KSC than this (km).</summary>
    public const double UnknownBurnKm = 30;

    /// <summary>
    /// How far ahead the ground is checked in flight (km). The ground below alone was too late: in the rig (2026-10-25)
    /// it rose 22 m/s toward a 1,717 m peak while the plane climbed 10, and a leg at 1,500 m flew into it.
    /// </summary>
    public static readonly double[] AheadKm = [0.5, 1, 2, 3, 4, 6, 8, 10, 12];

    /// <summary>Points from <paramref name="from"/> on <paramref name="bearing"/>, at each of <paramref name="kms"/>.</summary>
    public static IEnumerable<Geo> Along(double bodyRadius, Geo from, double bearing, IEnumerable<double> kms) =>
        kms.Select(km => Move(bodyRadius, from, bearing, km, from.Altitude));

    /// <summary>Every 1 km of a leg, and 2 km past its end (the plane flies on there).</summary>
    public static IEnumerable<double> LegKm(double km) =>
        Enumerable.Range(1, (int)Math.Ceiling(km) + 2).Select(i => (double)i);

    /// <summary>The altitude to fly: what was asked, or higher to clear the highest ground by <see cref="MinClearance"/>.</summary>
    public static double SafeAltitude(double wanted, IEnumerable<double> grounds) =>
        Math.Max(wanted, grounds.DefaultIfEmpty(0).Max() + MinClearance);

    /// <summary>
    /// How steeply a plane is counted on to climb: 5 m per 100 m. The pilot settles near 9 m/s at 150 m/s (6 per 100);
    /// in the rig (2026-10-25) a leg into mountains asked for 4,500 m from 2,800 and the plane hit a face 2 km on.
    /// </summary>
    public const double ClimbGradient = 0.05;

    /// <summary>The first point (km on, its ground) that the plane can't clear by <see cref="MinClearance"/> climbing from <paramref name="altitude"/>, or null.</summary>
    public static (double Km, double Ground)? TooSteep(double altitude, IEnumerable<(double Km, double Ground)> samples)
    {
        foreach (var (km, ground) in samples)
            if (ground + MinClearance > altitude + km * 1000 * ClimbGradient)
                return (km, ground);
        return null;
    }

    /// <summary>The point <paramref name="km"/> km from <paramref name="from"/> on <paramref name="bearing"/> (degrees).</summary>
    public static Geo Move(double bodyRadius, Geo from, double bearing, double km, double altitude)
    {
        var b = bearing * Math.PI / 180;
        var r = bodyRadius + from.Altitude;
        var north = Math.Cos(b) * km * 1000;
        var east = Math.Sin(b) * km * 1000;
        var lat = from.Latitude + north / r * 180 / Math.PI;
        var lon = from.Longitude + east / (r * Math.Cos(from.Latitude * Math.PI / 180)) * 180 / Math.PI;
        return new Geo(lat, (lon + 540) % 360 - 180, altitude);
    }

    public static double DistanceKm(double bodyRadius, Geo a, Geo b)
    {
        var (n, e) = Formation.Offset(bodyRadius, a, b);
        return Math.Sqrt(n * n + e * e) / 1000;
    }

    /// <summary>"12 km north-east of KSC" (for people and planners).</summary>
    public static string FromKsc(double bodyRadius, double latitude, double longitude)
    {
        var ksc = Runway.Ksc.Threshold;
        var here = new Geo(latitude, longitude, ksc.Altitude);
        var (n, e) = Formation.Offset(bodyRadius, ksc, here);
        var km = Math.Sqrt(n * n + e * e) / 1000;
        if (km < 1)
            return "at KSC";
        var bearing = (Math.Atan2(e, n) * 180 / Math.PI + 360) % 360;
        string[] names = ["north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west"];
        return string.Create(CultureInfo.InvariantCulture, $"{km:0} km {names[(int)Math.Round(bearing / 45) % 8]} of KSC");
    }

    /// <summary>
    /// Why a leg isn't allowed (null: it is): too short or long, past the range from KSC, or not enough fuel to get there
    /// and back to KSC keeping <see cref="FuelGauge.Reserve"/>. <paramref name="fuel"/> null: a plane without fuel (fine).
    /// </summary>
    public static string? Check(double bodyRadius, Geo here, Geo target, double speed, FuelState? fuel)
    {
        var leg = DistanceKm(bodyRadius, here, target);
        var ksc = Runway.Ksc.Threshold;
        var home = DistanceKm(bodyRadius, target, ksc with { Altitude = target.Altitude });
        if (leg < MinLegKm)
            return string.Create(CultureInfo.InvariantCulture, $"a leg of at least {MinLegKm:0} km");
        if (leg > MaxLegKm)
            return string.Create(CultureInfo.InvariantCulture, $"legs of {MaxLegKm:0} km at most (that one is {leg:0} km)");
        if (home > MaxRangeKm)
            return string.Create(CultureInfo.InvariantCulture, $"it would be {home:0} km from KSC; explore stays within {MaxRangeKm:0} km");
        if (fuel is not { } f)
            return null;
        if (f.PerMinute is not { } burn || burn <= 0.0005)
            return home > UnknownBurnKm
                ? string.Create(CultureInfo.InvariantCulture, $"the fuel burn isn't known yet (a few minutes of flying first): stay within {UnknownBurnKm:0} km of KSC")
                : null;
        var minutes = (leg + home) * 1000 / Math.Max(speed, 100) / 60;
        var after = f.Left - minutes * burn;
        return after < FuelGauge.Reserve
            ? string.Create(CultureInfo.InvariantCulture, $"there and back is about {minutes:0} min, leaving {Math.Max(0, after) * 100:0}% fuel (keep {FuelGauge.Reserve * 100:0}%)")
            : null;
    }
}

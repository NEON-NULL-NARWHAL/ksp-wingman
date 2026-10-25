// KSP Wingman · src/KspWingman.Mcp/Fuel.cs
// Fuel management, a first step for longer flights (the owner's idea): how full the tanks are, how fast they empty,
// how long that lasts, and what each flight used. FlightWatch feeds it once a second; status shows it; free flight and
// explore turn back on it. Pure, tested without KSP.
// Guide:   Meat and Potatoes/07-Explore-And-Fuel.md

using System.Globalization;

namespace KspWingman.Mcp;

/// <summary>The tanks now (0-1 of full) and the burn (fraction a minute, over the last two minutes), or null without fuel.</summary>
public readonly record struct FuelState(double Left, double? PerMinute)
{
    /// <summary>Minutes left at this burn, when it's burning.</summary>
    public double? Endurance => PerMinute is > 0.0005 and { } p ? Left / p : null;

    public string Describe() => Endurance is { } e
        ? string.Create(CultureInfo.InvariantCulture, $"{Left * 100:0}% fuel, about {e:0} min at this burn ({PerMinute * 100:0.0}% a minute)")
        : string.Create(CultureInfo.InvariantCulture, $"{Left * 100:0}% fuel");
}

public sealed class FuelGauge
{
    /// <summary>Turn back for home below this (and explore, when what's left wouldn't get it back).</summary>
    public const double Reserve = 0.25;

    private readonly Queue<(double Time, double Left)> _samples = new();

    public FuelState? Now { get; private set; }

    /// <summary>A reading at <paramref name="time"/> seconds: the tanks' fraction (null: the plane has no fuel tanks).</summary>
    public void Update(double? left, double time)
    {
        if (left is not { } l || !double.IsFinite(l))
        {
            Now = null;
            _samples.Clear();
            return;
        }
        // Refuelled or a new plane: start over.
        if (_samples.Count > 0 && l > _samples.Last().Left + 0.01)
            _samples.Clear();
        _samples.Enqueue((time, l));
        while (_samples.Count > 1 && time - _samples.Peek().Time > 120)
            _samples.Dequeue();
        var first = _samples.Peek();
        var perMinute = time - first.Time >= 20 ? Math.Max(0, first.Left - l) / ((time - first.Time) / 60) : (double?)null;
        Now = new FuelState(Math.Clamp(l, 0, 1), perMinute);
    }
}

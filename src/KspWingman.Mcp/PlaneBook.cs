// KSP Wingman · src/KspWingman.Mcp/PlaneBook.cs
// Where each plane's flight test and finals come from: LUNA EVO's tower when there is one (shared by every agent on
// the server), else what this session measured itself.

namespace KspWingman.Mcp;

public sealed class PlaneBook(TowerLink tower)
{
    private readonly Dictionary<string, PlaneTest> _tests = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The setup for a plane: its latest flight test (the tower's, or this session's) and its last finals.</summary>
    public PlaneSetup SetupFor(string craft)
    {
        var (test, finals) = Lookup(craft);
        return PlaneSetup.From(test, finals);
    }

    /// <summary>The plane's latest flight test, or null.</summary>
    public PlaneTest? TestOf(string craft) => Lookup(craft).Test;

    /// <summary>A flight test just finished: kept here, and sent to the tower.</summary>
    public void Tested(PlaneTest test)
    {
        lock (_tests)
            _tests[test.Craft] = test;
        if (tower.Client is { } client)
            _ = client.RecordTestAsync(test).ContinueWith(_ => { }, TaskScheduler.Default); // no tower: kept here only
    }

    private (PlaneTest? Test, IReadOnlyList<FinalReport> Finals) Lookup(string craft)
    {
        PlaneTest? test = null;
        IReadOnlyList<FinalReport> finals = [];
        if (tower.Client is { } client)
        {
            try
            {
                (test, finals) = client.CraftAsync(craft).GetAwaiter().GetResult();
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or WingmanException or System.Text.Json.JsonException or IOException or InvalidOperationException)
            {
                // No tower right now: this session's numbers.
            }
        }
        lock (_tests)
            return (test ?? _tests.GetValueOrDefault(craft), finals);
    }
}

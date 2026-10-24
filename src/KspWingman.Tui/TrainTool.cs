// KSP Wingman · src/KspWingman.Tui/TrainTool.cs
// The pilot model's own tool for training on request (the owner's idea: "train flying fast for 20 minutes"): the
// console runs it as free flight on that one skill, after the current order.

using System.Text.Json;

namespace KspWingman.Tui;

/// <summary>The MCP server's tools plus <c>train</c>, which the console handles itself.</summary>
public sealed class TrainTool(IToolHost inner, Action<string, double> start) : IToolHost
{
    private static readonly ToolSpec Spec = new("train",
        "Practise one ability on your own, inside a box around KSC, until it goes well (3 good flights in a row) or the " +
        "time is up: \"fast\" (high-speed legs), \"high\" (climb above 5,000 m), \"landings\", \"visual\", \"rnav\", " +
        "\"planes\" (fly planes you haven't flown) or \"plans\" (file your own flight plans). Any later order stops it.",
        JsonDocument.Parse("""
            {"type":"object","properties":{
              "focus":{"type":"string","description":"fast, high, landings, visual, rnav, planes or plans"},
              "minutes":{"type":"number","description":"How long at most (5-240), 30 if left out"}},
             "required":["focus"]}
            """).RootElement.Clone());

    public IReadOnlyList<ToolSpec> Tools { get; } = [.. inner.Tools, Spec];

    public Task<string> CallAsync(string name, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancel)
    {
        if (name != "train")
            return inner.CallAsync(name, arguments, cancel);
        var focus = arguments.TryGetValue("focus", out var f) ? f?.ToString() ?? "" : "";
        if (FreeFlight.Focus(focus) is not { } skills)
            return Task.FromResult("Train what? fast, high, landings, visual, rnav, planes or plans.");
        var minutes = arguments.TryGetValue("minutes", out var m) && double.TryParse(m?.ToString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var mm) && double.IsFinite(mm) ? Math.Clamp(mm, 5, 240) : 30;
        start(focus, minutes);
        return Task.FromResult($"Training {string.Join(", ", skills)} for up to {minutes:0} minutes starts now; any order stops it.");
    }
}

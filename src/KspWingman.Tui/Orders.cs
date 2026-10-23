// KSP Wingman · src/KspWingman.Tui/Orders.cs
// Orders from LMP's chat: only from players on the allow list, and only when they call the agent by its call sign
// ("wingman, follow me", "@Wingman land"). Everything else in chat is people talking to each other.

namespace KspWingman.Tui;

/// <summary>One line of read_chat's output: "#id from: text".</summary>
public readonly record struct ChatLine(int Id, string From, string Text)
{
    public static ChatLine? Parse(string line)
    {
        if (!line.StartsWith('#'))
            return null;
        var space = line.IndexOf(' ');
        var colon = line.IndexOf(": ", StringComparison.Ordinal);
        if (space < 2 || colon <= space || !int.TryParse(line.AsSpan(1, space - 1), out var id))
            return null;
        return new ChatLine(id, line[(space + 1)..colon], line[(colon + 2)..]);
    }

    public static IReadOnlyList<ChatLine> ParseAll(string text) =>
        text.Split('\n').Select(l => Parse(l.TrimEnd('\r'))).OfType<ChatLine>().ToList();
}

public static class Orders
{
    /// <summary>
    /// The order in a chat line, or null when it isn't one for the agent: said by the agent itself, by someone not on
    /// the allow list (an empty list: no one), or not starting with the call sign.
    /// </summary>
    public static string? From(ChatLine line, string callSign, string me, IReadOnlyCollection<string> allowed)
    {
        if (line.From.Equals(me, StringComparison.OrdinalIgnoreCase) ||
            !allowed.Any(a => a.Equals(line.From, StringComparison.OrdinalIgnoreCase)))
            return null;
        var text = line.Text.Trim();
        foreach (var prefix in new[] { "@" + callSign, callSign, "@" + me, me })
        {
            if (prefix.Length == 0 || !text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            var rest = text[prefix.Length..];
            // "Wingmanx" isn't "Wingman": the call sign must end there.
            if (rest.Length > 0 && char.IsLetterOrDigit(rest[0]))
                continue;
            rest = rest.TrimStart(',', ':', ' ', '!', '-').Trim();
            return rest.Length == 0 ? null : rest;
        }
        return null;
    }
}

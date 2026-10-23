// KSP Wingman · src/KspWingman.Mcp/Tools.cs
// The tools an agent gets: what the game is doing, who's playing, and LMP's chat.

using System.ComponentModel;
using System.Globalization;
using System.Text;
using KRPC.Client.Services.KRPC;
using KRPC.Client.Services.SpaceCenter;
using ModelContextProtocol.Server;

namespace KspWingman.Mcp;

[McpServerToolType]
public sealed class Tools(KrpcLink krpc)
{
    /// <summary>The longest wait_for_chat may wait (an MCP call shouldn't hang for minutes).</summary>
    internal const int MaxWaitSeconds = 120;

    [McpServerTool(Name = "status"), Description(
        "What the agent's KSP is doing: its LMP player name, whether it's in a server, the scene, and (in flight) the " +
        "vessel it flies, where, how high and how fast. Start here.")]
    public string Status() => krpc.Run(connection =>
    {
        var text = new StringBuilder();
        text.AppendLine($"Player: {krpc.Wingman<string>("PlayerName")} · " + (krpc.Wingman<bool>("InGame") ? "in a server" : "not in a server"));
        var scene = connection.KRPC().GameScene;
        text.AppendLine($"Scene: {scene}");
        if (scene == GameScene.Flight)
        {
            var vessel = connection.SpaceCenter().ActiveVessel;
            var body = vessel.Orbit.Body;
            var flight = vessel.Flight(body.ReferenceFrame);
            text.AppendLine($"Vessel: {vessel.Name} ({vessel.Situation}) at {body.Name}");
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"Altitude {flight.MeanAltitude:0} m (above ground {flight.SurfaceAltitude:0} m) · speed {flight.Speed:0.0} m/s · " +
                $"heading {flight.Heading:0}° · pitch {flight.Pitch:0}° · lat {flight.Latitude:0.0000} lon {flight.Longitude:0.0000}"));
        }
        var behind = krpc.Wingman<double>("SecondsBehind");
        if (behind > 1)
            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Time: {behind:0} s behind the player furthest ahead (other players may not see you)."));
        return text.ToString().TrimEnd();
    });

    [McpServerTool(Name = "players"), Description(
        "The other players on the server: name, the vessel they fly, and LMP's status line for them (e.g. \"Flying above Kerbin\").")]
    public string Players()
    {
        var players = krpc.Wingman<IList<string>>("Players");
        return players.Count == 0 ? "No one else is online." : string.Join("\n", players.Select(p => p.Replace('\t', '|')));
    }

    [McpServerTool(Name = "read_chat"), Description(
        "LMP's chat lines after a line number: \"#id from: text\", oldest first. Give the last id you've seen (0 for all " +
        "that are kept) and remember the newest id for next time.")]
    public string ReadChat([Description("Only lines after this number (0 for all).")] int afterId = 0) => Format(krpc.Wingman<IList<string>>("Chat", afterId));

    [McpServerTool(Name = "send_chat"), Description(
        "Says one line in LMP's chat, as the agent's player, for everyone on the server. At most 300 characters and one " +
        "line a second. Be friendly and brief: real people read it.")]
    public string SendChat([Description("What to say.")] string text)
    {
        var problem = krpc.Wingman<string>("SendChat", text);
        return problem.Length == 0 ? "Sent." : "Not sent: " + problem;
    }

    [McpServerTool(Name = "wait_for_chat"), Description(
        "Waits until someone else says something in LMP's chat (or the time runs out), then returns the new lines like " +
        "read_chat. Use it to listen while idle.")]
    public async Task<string> WaitForChat(
        [Description("Only lines after this number.")] int afterId,
        [Description("How long to wait, in seconds (at most 120).")] int timeoutSeconds = 60,
        CancellationToken cancel = default)
    {
        var me = krpc.Wingman<string>("PlayerName");
        var until = DateTime.UtcNow.AddSeconds(Math.Clamp(timeoutSeconds, 1, MaxWaitSeconds));
        while (true)
        {
            var lines = krpc.Wingman<IList<string>>("Chat", afterId);
            if (lines.Any(l => l.Split('\t') is { Length: >= 3 } f && f[1] != me))
                return Format(lines);
            if (DateTime.UtcNow >= until)
                return lines.Count == 0 ? "No new chat." : Format(lines) + "\n(only your own lines; no one else said anything)";
            await Task.Delay(1000, cancel);
        }
    }

    /// <summary>"id \t from \t text" lines → "#id from: text".</summary>
    internal static string Format(IList<string> lines) =>
        lines.Count == 0 ? "No chat lines." : string.Join("\n", lines.Select(l => l.Split('\t', 3) is { Length: 3 } f ? $"#{f[0]} {f[1]}: {f[2]}" : l));
}

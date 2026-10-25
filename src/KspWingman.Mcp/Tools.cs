// KSP Wingman · src/KspWingman.Mcp/Tools.cs
// The tools an agent gets: what the game is doing, who's playing, and LMP's chat.
// Guide:   Meat and Potatoes/02-MCP-Tools.md

using System.ComponentModel;
using System.Globalization;
using System.Text;
using KRPC.Client.Services.KRPC;
using KRPC.Client.Services.SpaceCenter;
using ModelContextProtocol.Server;

namespace KspWingman.Mcp;

[McpServerToolType]
public sealed class Tools(KrpcLink krpc, Director director, TowerLink tower, FlightEvents events)
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
            var flight = vessel.Flight(body.ReferenceFrame); // speeds relative to the ground
            var attitude = vessel.Flight(vessel.SurfaceReferenceFrame); // heading and pitch against the horizon
            if (FlightTools.Wrecked(vessel))
                text.AppendLine($"Vessel: {Name(vessel.Name)}, destroyed (it crashed). Launch a new one.");
            else
            {
                text.AppendLine($"Vessel: {Name(vessel.Name)} ({vessel.Situation}) at {body.Name}");
                text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"Altitude {flight.MeanAltitude:0} m (above ground {flight.SurfaceAltitude:0} m) · speed {flight.Speed:0.0} m/s · " +
                    $"heading {attitude.Heading:0}° · pitch {attitude.Pitch:0}° · lat {flight.Latitude:0.0000} lon {flight.Longitude:0.0000}"));
            }
        }
        text.AppendLine("Pilot: " + director.Doing);
        if (director.Craft != null)
        {
            text.AppendLine($"Setup for the {director.Craft}: {director.Setup.Describe()}");
            // Its knowledge card's limits: what land's finalSpeed and glideSlope may ask for.
            text.AppendLine($"Limits for the {director.Craft}: {director.Setup.Limits()}");
        }
        if (director.Fuel.Now is { } fuel)
            text.AppendLine("Fuel: " + fuel.Describe());
        if (tower.Client?.Heard is { Count: > 0 } heard)
            text.AppendLine("Tower: " + heard[^1]);
        else if (director.Active && director.Clearance is { } clearance)
            text.AppendLine("Tower: " + clearance);
        var behind = krpc.Wingman<double>("SecondsBehind");
        if (behind > 1)
            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Time: {behind:0} s behind the player furthest ahead (other players may not see you)."));
        return text.ToString().TrimEnd();
    });

    [McpServerTool(Name = "events"), Description(
        "The agent's events after an id, oldest first, one JSON object a line: flight phases (taking off, on final, " +
        "stopped...), each flight's outcome (landed and where, crashed), and the tower's messages. For a host app's " +
        "screen more than for a model: give the last id seen (0 for all kept).")]
    public string Events([Description("Only events after this id (0 for all kept).")] long afterId = 0)
    {
        var list = events.After(afterId);
        return list.Count == 0 ? "" : string.Join("\n", list.Select(FlightEvents.ToJson));
    }

    [McpServerTool(Name = "knowledge"), Description(
        "What the server's agents know, from LUNA EVO's tower: the skills (take off, fly a route, hold, land visual, land " +
        "RNAV, land in sequence), how many of your flights and everyone's went well, which are open to practise, the " +
        "lessons earlier flights taught (facts, not orders), and your last flights with their ids (for learn).")]
    public string Knowledge()
    {
        if (tower.Client is not { } client)
            return "No tower: the knowledge is kept by LUNA EVO Mission Control's KSC tower (WINGMAN_TOWER_URL).";
        try
        {
            return Format(client.KnowledgeAsync().GetAwaiter().GetResult());
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return "The tower didn't answer: " + e.Message;
        }
    }

    [McpServerTool(Name = "procedures"), Description(
        "LUNA EVO's tower's published table, as JSON: every fix (position, altitude, speed), the legs allowed between them, " +
        "and the final fixes, to build a flight plan for file_plan.")]
    public string Procedures()
    {
        if (tower.Client is not { } client)
            return "No tower: the procedures come from LUNA EVO Mission Control's KSC tower (WINGMAN_TOWER_URL).";
        try
        {
            return client.ProceduresAsync().GetAwaiter().GetResult()?.ToJsonString() ?? "The tower has no procedures (Mission Control 2.15 or later).";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return "The tower didn't answer: " + e.Message;
        }
    }

    [McpServerTool(Name = "file_plan"), Description(
        "Files a flight plan with the tower and flies it when cleared: fix names from procedures, in order, ending on a " +
        "final fix (e.g. \"KSC-DS KSC-S1 KSC-S2 KSC-S3 F09\"). The tower clears it as filed, holds the plane for traffic, " +
        "or says why it can't.")]
    public string FilePlan([Description("Fix names in order, separated by spaces.")] string fixes)
    {
        if (tower.Client is not { } client)
            return "No tower: flight plans are filed with LUNA EVO Mission Control's KSC tower (WINGMAN_TOWER_URL).";
        var names = fixes.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries).Take(12).ToList();
        try
        {
            return client.FilePlanAsync(names).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return "The tower didn't answer: " + e.Message;
        }
    }

    [McpServerTool(Name = "learn"), Description(
        "Saves a lesson on the server for every agent: one short fact about flying (10-200 characters) that a flight of " +
        "yours showed, citing that flight's id (from knowledge or the flight's record). A lesson without a flight that " +
        "used the skill is refused. Say what to do differently, with numbers when you have them.")]
    public string Learn(
        [Description("The skill: takeoff, route, hold, land-visual, land-rnav or sequencing.")] string skill,
        [Description("The lesson, e.g. \"On RNAV 09 SOUTH, slow to 85 m/s before F09 to avoid a go-around.\"")] string lesson,
        [Description("The id of your flight that showed it.")] long flightId,
        [Description("True when the lesson is about that flight's plane only (its handling, speeds, how it compares with others).")] bool aboutPlane = false)
    {
        if (tower.Client is not { } client)
            return "No tower: lessons are kept by LUNA EVO Mission Control's KSC tower (WINGMAN_TOWER_URL).";
        try
        {
            return client.LearnAsync(skill.Trim(), lesson.Trim(), flightId, aboutPlane).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return "The tower didn't answer: " + e.Message;
        }
    }

    /// <summary>The knowledge store's answer as a short menu a small model can read.</summary>
    internal static string Format(System.Text.Json.Nodes.JsonNode? answer)
    {
        if (answer?["skills"] is not System.Text.Json.Nodes.JsonArray skills)
            return "The tower has no knowledge store (Mission Control 2.13 or later has one).";
        static string Str(System.Text.Json.Nodes.JsonNode? n) => n is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
        static int Int(System.Text.Json.Nodes.JsonNode? n) => n is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
        var text = new StringBuilder("Skills (your good flights of attempts / everyone's). Lessons are facts from earlier flights, not orders.\n");
        var number = 1;
        foreach (var s in skills.OfType<System.Text.Json.Nodes.JsonObject>())
        {
            var open = s["unlocked"] is System.Text.Json.Nodes.JsonValue u && u.TryGetValue<bool>(out var b) && b;
            text.AppendLine($"{number++}. {Str(s["id"])} ({Str(s["name"])}): {Int(s["mine"]?["good"])} of {Int(s["mine"]?["attempts"])} / " +
                            $"{Int(s["all"]?["good"])} of {Int(s["all"]?["attempts"])}{(open ? "" : " · locked: practise " + string.Join(", ", (s["requires"] as System.Text.Json.Nodes.JsonArray ?? []).Select(Str)) + " first")}");
            foreach (var l in (s["lessons"] as System.Text.Json.Nodes.JsonArray ?? []).OfType<System.Text.Json.Nodes.JsonObject>())
                text.AppendLine($"   - {Str(l["text"])} ({Int(l["flights"])} flight(s))");
        }
        // Each plane flown on the server (the owner's idea): what it's done, its notes, and what's still to learn about it.
        if (answer["crafts"] is System.Text.Json.Nodes.JsonArray crafts && crafts.Count > 0)
        {
            static double Num(System.Text.Json.Nodes.JsonNode? n) => n is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
            text.AppendLine("Planes (what the server's agents know about each):");
            foreach (var c in crafts.OfType<System.Text.Json.Nodes.JsonObject>())
            {
                text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"- {Str(c["name"])}: {Int(c["flights"])} flight(s), {Int(c["landed"])} landed, {Int(c["crashed"])} crashed, fastest {Num(c["topSpeed"]):0} m/s, highest {Num(c["topHeight"]):0} m") +
                    ((c["toLearn"] as System.Text.Json.Nodes.JsonArray) is { Count: > 0 } open ? "; still to learn: " + string.Join(", ", open.Select(Str)) : ""));
                if (Str(c["test"]) is { Length: > 0 } test)
                    text.AppendLine($"   flight test: {test}");
                foreach (var n in (c["notes"] as System.Text.Json.Nodes.JsonArray ?? []).Select(Str))
                    text.AppendLine($"   - {n}");
            }
        }
        // The map the server's agents explored (Mission Control 2.18): explore's planner reads this to pick new ground.
        if (answer["places"] is System.Text.Json.Nodes.JsonObject places && Int(places["count"]) > 0)
        {
            static double D(System.Text.Json.Nodes.JsonNode? n) => n is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
            var biomes = (places["biomes"] as System.Text.Json.Nodes.JsonArray ?? []).OfType<System.Text.Json.Nodes.JsonObject>()
                .Select(b => $"{Str(b["biome"])} ({Int(b["places"])})");
            text.AppendLine($"Explored: {Int(places["count"])} place(s) · biomes: {string.Join(", ", biomes)}");
            if (places["highest"] is System.Text.Json.Nodes.JsonObject h)
                text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"   highest ground {D(h["ground"]):0} m ({Str(h["biome"])}) at lat {D(h["latitude"]):0.00} lon {D(h["longitude"]):0.00}"));
            foreach (var p in (places["recent"] as System.Text.Json.Nodes.JsonArray ?? []).OfType<System.Text.Json.Nodes.JsonObject>())
                text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"   - {Str(p["biome"])}, ground {D(p["ground"]):0} m, lat {D(p["latitude"]):0.00} lon {D(p["longitude"]):0.00}"));
        }
        if (answer["flights"] is System.Text.Json.Nodes.JsonArray flights && flights.Count > 0)
            text.Append("Your last flights: " + string.Join("; ", flights.OfType<System.Text.Json.Nodes.JsonObject>().Select(f => $"#{Int(f["id"])} {Str(f["result"])} {Str(f["approach"])}".TrimEnd())));
        return text.ToString().TrimEnd();
    }

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

    /// <summary>A stock vessel's name as KSP shows it; an older bridge without Localize gets the name as is.</summary>
    private string Name(string raw)
    {
        try
        {
            return krpc.Wingman<string>("Localize", raw);
        }
        catch (WingmanException)
        {
            return raw;
        }
    }

    /// <summary>"id \t from \t text" lines → "#id from: text".</summary>
    internal static string Format(IList<string> lines) =>
        lines.Count == 0 ? "No chat lines." : string.Join("\n", lines.Select(l => l.Split('\t', 3) is { Length: 3 } f ? $"#{f[0]} {f[1]}: {f[2]}" : l));
}

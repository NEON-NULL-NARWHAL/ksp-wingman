// KSP Wingman · tests/KspWingman.Tests/TowerClientTests.cs
// Flying for a tower: the hold's circle, and a route as the tower sends it (fixes in degrees) turned into Wingman's fixes.
// Guide:   Meat and Potatoes/09-Tests-And-Tools.md

using System.Text.Json.Nodes;
using KspWingman.Mcp;

namespace KspWingman.Tests;

public sealed class TowerClientTests
{
    private const double Kerbin = 600_000;
    private static readonly double MetersPerDegree = Kerbin * Math.PI / 180;

    [Fact]
    public void A_hold_flies_to_the_point_then_circles_it_clockwise()
    {
        var center = new Geo(0, -74, 1200);
        // 6 km north of it: straight toward it (south).
        var far = Holding.Target(Kerbin, new Geo(6000 / MetersPerDegree, -74, 1000), center, 2000);
        Assert.InRange(far.Heading, 175, 185);
        Assert.Equal(1200, far.Altitude);
        // On the circle, east of the center: south, the center on the right.
        var on = Holding.Target(Kerbin, new Geo(0, -74 + 2000 / MetersPerDegree, 1200), center, 2000);
        Assert.InRange(on.Heading, 175, 185);
        // Inside the circle, east of it: a little away from the center (more east than south).
        var inside = Holding.Target(Kerbin, new Geo(0, -74 + 1000 / MetersPerDegree, 1200), center, 2000);
        Assert.InRange(inside.Heading, 145, 175);
    }

    [Fact]
    public void A_tower_route_becomes_fixes_by_the_runway()
    {
        double Lat(double cross) => -0.0486 - cross / MetersPerDegree;
        double Lon(double along, double cross) => -74.7244 + (along + Landing.TouchdownAfterThreshold) / (MetersPerDegree * Math.Cos(Lat(cross) * Math.PI / 180));
        var route = new JsonObject
        {
            ["name"] = "RNAV 09 NORTH",
            ["fixes"] = new JsonArray(
                new JsonObject { ["name"] = "KSC-N1", ["latitude"] = Lat(-4000), ["longitude"] = Lon(-1000, -4000), ["altitude"] = 900, ["speed"] = 110 },
                new JsonObject { ["name"] = "F09", ["latitude"] = Lat(0), ["longitude"] = Lon(-8000, 0), ["altitude"] = 628, ["speed"] = 90 }),
        };
        var approach = TowerClient.Route(route)!;
        Assert.Equal("RNAV 09 NORTH", approach.Name);
        Assert.InRange(approach.Fixes[0].Along, -1010, -990);
        Assert.InRange(approach.Fixes[0].Cross, -4010, -3990);
        Assert.Equal(628, approach.Fixes[1].Altitude);
        Assert.Null(TowerClient.Route(new JsonObject { ["name"] = "x" })); // no fixes
        // Audit 13 (T3): not to be flown, whoever sent it: into the ground, far away, too fast, too many fixes.
        JsonObject One(double along, double cross, double altitude, double speed) => new()
        {
            ["fixes"] = new JsonArray(new JsonObject { ["name"] = "x", ["latitude"] = Lat(cross), ["longitude"] = Lon(along, cross), ["altitude"] = altitude, ["speed"] = speed }),
        };
        Assert.NotNull(TowerClient.Route(One(-8000, 0, 628, 90)));
        Assert.Null(TowerClient.Route(One(-8000, 0, 50, 90)));
        Assert.Null(TowerClient.Route(One(-90_000, 0, 900, 90)));
        Assert.Null(TowerClient.Route(One(-8000, 0, 900, 900)));
        Assert.Null(TowerClient.Route(new JsonObject { ["fixes"] = new JsonArray(Enumerable.Range(0, 13).Select(_ => (JsonNode)new JsonObject
            { ["latitude"] = Lat(0), ["longitude"] = Lon(-8000, 0), ["altitude"] = 900, ["speed"] = 90 }).ToArray()) }));
        Assert.NotNull(TowerClient.Hold(new JsonObject { ["latitude"] = Lat(7000), ["longitude"] = Lon(4000, 7000), ["altitude"] = 1200, ["radius"] = 2000 }));
        Assert.Null(TowerClient.Hold(new JsonObject { ["latitude"] = Lat(7000), ["longitude"] = Lon(4000, 7000), ["altitude"] = 100, ["radius"] = 2000 }));
        Assert.Null(TowerClient.Hold(new JsonObject { ["latitude"] = Lat(7000), ["longitude"] = Lon(4000, 7000), ["altitude"] = 1200, ["radius"] = 50 }));
        Assert.Null(TowerClient.Route(new JsonObject { ["fixes"] = new JsonArray(new JsonObject { ["latitude"] = "north" }) }));
    }
}

using System.Globalization;
using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Net;
using ArcaneCore.World.Tests.Playerbots.Party;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// <c>.playerbot siege waypoint add at $map $x $y $z</c>: the same waypoint as the position form, from the server console (no player),
/// on one map only, and a clear refusal for bad input.
/// </summary>
public sealed class PlayerbotSiegeWaypointAtTests
{
    private static string Spot(Vector3 at) => string.Create(CultureInfo.InvariantCulture, $"{at.X:F1}, {at.Y:F1}, {at.Z:F1}");

    private static string R(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    [Fact]
    public async Task WaypointAddAt_FromTheConsole_StoresTheSameWaypointAsTheHereForm()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([]);
        await using WorldTestClient gm = await world.World.EnterWorldAsync("PFSIEGEGM", "Pfsiegegm", AccountSecurity.Administrator);
        await gm.CollectAsync();
        await using FleetConsoleRig console = new(world.World.Host);
        Player me = await world.OnWorldAsync(() => world.World.Host.World.FindOnlinePlayer("Pfsiegegm")!);
        Vector3 spot = await world.OnWorldAsync(() => new Vector3(me.X, me.Y, me.Z));
        uint map = me.MapId;

        List<string> viaHere = await FleetWorld.SayAsync(gm, ".playerbot siege waypoint add");
        string[] viaAt = await console.RunAsync($".playerbot siege waypoint add at {map} {R(spot.X)} {R(spot.Y)} {R(spot.Z)}");

        Assert.Equal([$"Playerbot siege: waypoint 1 added on map {map} at {Spot(spot)}."], viaHere);
        Assert.Equal([$"Playerbot siege: waypoint 2 added on map {map} at {Spot(spot)}."], viaAt);
        Assert.Equal([spot, spot], world.Coordinator.SiegeWaypoints.ToArray());
        string[] list = await console.RunAsync(".playerbot siege waypoint list");
        Assert.Equal([$"Playerbot siege: 2 waypoints on map {map}."], list.Take(1));
    }

    [Fact]
    public async Task WaypointAddAt_WithNoPlayerAtAll_BuildsAPathFromTheDocumentedExamples()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([]);
        await using FleetConsoleRig console = new(world.World.Host);

        string[] first = await console.RunAsync(".playerbot siege waypoint add at 0 1925 225 49.41");
        string[] second = await console.RunAsync(".playerbot siege waypoint add at 0 1900 230 50");

        Assert.Equal(["Playerbot siege: waypoint 1 added on map 0 at 1925.0, 225.0, 49.4."], first);
        Assert.Equal(["Playerbot siege: waypoint 2 added on map 0 at 1900.0, 230.0, 50.0."], second);
        Assert.Equal(2, world.Coordinator.SiegeWaypoints.Count);
        Assert.Equal(0u, world.Coordinator.SiegeWaypointMap);
    }

    [Fact]
    public async Task WaypointAddAt_OnAnotherMapThanTheStoredOnes_IsRefusedLikeHere()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([]);
        await using FleetConsoleRig console = new(world.World.Host);
        await console.RunAsync(".playerbot siege waypoint add at 0 1925 225 49.41");

        string[] mixed = await console.RunAsync(".playerbot siege waypoint add at 1 -441 -2596 96");

        Assert.Equal(["Playerbot siege: waypoint refused (waypoint-other-map)."], mixed);
        Assert.Single(world.Coordinator.SiegeWaypoints);
        Assert.Equal(0u, world.Coordinator.SiegeWaypointMap);
    }

    [Theory]
    [InlineData("at 9999 1 2 3", "Playerbot siege: unknown map 9999.")]
    [InlineData("at zero 1 2 3", "Playerbot siege: the map must be a map id.")]
    [InlineData("at 0 NaN 2 3", "Playerbot siege: x, y and z must be finite numbers.")]
    [InlineData("at 0 1 2 Infinity", "Playerbot siege: x, y and z must be finite numbers.")]
    [InlineData("at 0 1 2 nope", "Playerbot siege: x, y and z must be finite numbers.")]
    [InlineData("at 0 99999 2 3", "Playerbot siege: the position is outside the map.")]
    [InlineData("at 0 1 -99999 3", "Playerbot siege: the position is outside the map.")]
    [InlineData("at 0 1 2 1e9", "Playerbot siege: the position is outside the map.")]
    public async Task WaypointAddAt_WithBadInput_IsRefusedAndStoresNothing(string tail, string expected)
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([]);
        await using FleetConsoleRig console = new(world.World.Host);

        string[] reply = await console.RunAsync(".playerbot siege waypoint add " + tail);

        Assert.Equal([expected], reply);
        Assert.Empty(world.Coordinator.SiegeWaypoints);
    }

    [Theory]
    [InlineData("at")]
    [InlineData("at 0 1 2")]
    [InlineData("at 0 1 2 3 4")]
    [InlineData("0 1 2 3")]
    public async Task WaypointAddAt_WithTheWrongShape_ShowsTheHelp(string tail)
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([]);
        await using FleetConsoleRig console = new(world.World.Host);

        string[] reply = await console.RunAsync(".playerbot siege waypoint add " + tail);

        Assert.StartsWith("Syntax: .playerbot siege waypoint add", reply[0], StringComparison.Ordinal);
        Assert.Empty(world.Coordinator.SiegeWaypoints);
    }

    [Fact]
    public async Task WaypointAddWithoutAt_FromTheConsole_StillNeedsAPlayer()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([]);
        await using FleetConsoleRig console = new(world.World.Host);

        string[] reply = await console.RunAsync(".playerbot siege waypoint add");

        Assert.Equal([ArcaneCore.World.Gm.Core.GmStrings.ConsoleNeedsPlayer], reply);
        Assert.Empty(world.Coordinator.SiegeWaypoints);
    }
}

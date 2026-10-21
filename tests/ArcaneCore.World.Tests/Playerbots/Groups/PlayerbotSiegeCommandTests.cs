using System.Globalization;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Tests.Playerbots.Party;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// <c>.playerbot siege waypoint add|list|clear</c>, <c>siege start</c> and <c>siege stop</c> against the scenario world: an Administrator
/// client types, managed bots are enlisted into bot-led raids, and everything is in memory.
/// </summary>
public sealed class PlayerbotSiegeCommandTests
{
    private static async Task<WorldTestClient> AddGm(GroupTestWorld world)
    {
        WorldTestClient gm = await world.World.EnterWorldAsync("SIEGECMDGM", "Siegecmdgm", AccountSecurity.Administrator);
        await gm.CollectAsync();
        return gm;
    }

    private static Player Me(GroupTestWorld world) => world.World.Host.World.FindOnlinePlayer("Siegecmdgm")!;

    private static string Spot(Player player) => string.Create(CultureInfo.InvariantCulture, $"{player.X:F1}, {player.Y:F1}, {player.Z:F1}");

    [Fact]
    public async Task Waypoints_AreAddedAtTheInvokersPosition_ListedInOrder_AndCleared()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([]);
        await using WorldTestClient gm = await AddGm(world);
        Player me = await world.OnWorldAsync(() => Me(world));

        Assert.Equal(["Playerbot siege: no waypoints."], await FleetWorld.SayAsync(gm, ".playerbot siege waypoint list"));

        string first = await world.OnWorldAsync(() => Spot(me));
        Assert.Equal([$"Playerbot siege: waypoint 1 added on map {me.MapId} at {first}."], await FleetWorld.SayAsync(gm, ".playerbot siege waypoint add"));
        await world.OnWorldAsync(() => { me.Relocate(me.X + 20f, me.Y, me.Z, 0, world.World.Host.World.NowMs); return true; });
        string second = await world.OnWorldAsync(() => Spot(me));
        Assert.Equal([$"Playerbot siege: waypoint 2 added on map {me.MapId} at {second}."], await FleetWorld.SayAsync(gm, ".playerbot siege waypoint add"));

        Assert.Equal([$"Playerbot siege: 2 waypoints on map {me.MapId}.", $"1: {first}", $"2: {second}"], await FleetWorld.SayAsync(gm, ".playerbot siege waypoint list", lines: 3));
        Assert.Equal(2, world.Coordinator.SiegeWaypoints.Count);

        Assert.Equal(["Playerbot siege: 2 waypoints cleared."], await FleetWorld.SayAsync(gm, ".playerbot siege waypoint clear"));
        Assert.Empty(world.Coordinator.SiegeWaypoints);
    }

    [Fact]
    public async Task AWaypointOnAnotherMap_IsRefused()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([]);
        await using WorldTestClient gm = await AddGm(world);
        Player me = await world.OnWorldAsync(() => Me(world));
        await FleetWorld.SayAsync(gm, ".playerbot siege waypoint add");

        string? refusal = await world.OnWorldAsync(() => world.Coordinator.AddSiegeWaypoint(me.MapId + 1, new System.Numerics.Vector3(1f, 2f, 3f)));

        Assert.Equal("waypoint-other-map", refusal);
        Assert.Single(world.Coordinator.SiegeWaypoints);
    }

    [Fact]
    public async Task Start_NeedsWaypointsAndBots_AndBadSyntaxShowsTheHelp()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([]);
        await world.AddBotAsync("Sgcmdaa", 1, 1, 10, [GroupTestWorld.Taunt]);
        await world.AddBotAsync("Sgcmdab", 1, 5, 10, []);
        await world.RunAsync(2_000);
        await using WorldTestClient gm = await AddGm(world);

        Assert.Equal(["Playerbot siege: no waypoints; stand at a point and .playerbot siege waypoint add."],
            await FleetWorld.SayAsync(gm, $".playerbot siege start Sgcmda* {OgreEntryText}"));
        await FleetWorld.SayAsync(gm, ".playerbot siege waypoint add");

        Assert.Equal(["No playerbot matches 'Nobodyhere'."], await FleetWorld.SayAsync(gm, $".playerbot siege start Nobodyhere {OgreEntryText}"));
        Assert.Equal(["Playerbot siege: refused (too-few-bots)."], await FleetWorld.SayAsync(gm, $".playerbot siege start Sgcmdaa {OgreEntryText}"));

        foreach (string bad in new[] { "Sgcmda*", "Sgcmda* abc", "Sgcmda* 0", $"Sgcmda* {OgreEntryText} raids 3", $"Sgcmda* {OgreEntryText} raids", $"Sgcmda* {OgreEntryText} extra" })
        {
            List<string> reply = await FleetWorld.SayAsync(gm, ".playerbot siege start " + bad, lines: 2);
            Assert.StartsWith("Syntax: .playerbot siege start", reply[0], StringComparison.Ordinal);
        }

        Assert.Null(world.Coordinator.Siege);
    }

    private static string OgreEntryText => GroupTestWorld.OgreEntry.ToString(CultureInfo.InvariantCulture);

    [Fact]
    public async Task Start_EnlistsTheBotsIntoRaids_AStartedSiegeRefusesChanges_AndStopEndsIt()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([]);
        await world.AddBotAsync("Sgcmdba", 1, 1, 10, [GroupTestWorld.Taunt]);
        await world.AddBotAsync("Sgcmdbb", 1, 1, 10, [GroupTestWorld.Taunt]);
        await world.AddBotAsync("Sgcmdbc", 1, 5, 10, []);
        await world.AddBotAsync("Sgcmdbd", 1, 5, 10, []);
        await world.RunAsync(2_000);
        await using WorldTestClient gm = await AddGm(world);
        await FleetWorld.SayAsync(gm, ".playerbot siege waypoint add");

        List<string> started = await FleetWorld.SayAsync(gm, $".playerbot siege start Sgcmdb* {OgreEntryText} raids 2", lines: 2);

        Assert.Equal("Playerbot siege: matched=4 enlisted=4 skipped=0", started[0]);
        Assert.Equal($"Siege 1 started: 2 raids, objective {OgreEntryText}, 1 waypoint.", started[1]);
        PlayerbotSiege siege = world.Coordinator.Siege!;
        Assert.Equal(2, siege.Raids.Count);
        Assert.Equal([2, 2], siege.Raids.Select(r => r.Group!.Members.Count).ToArray());

        Assert.Equal(["Playerbot siege: refused (siege-running)."], await FleetWorld.SayAsync(gm, $".playerbot siege start Sgcmdb* {OgreEntryText}"));
        Assert.Equal(["Playerbot siege: waypoint refused (siege-running)."], await FleetWorld.SayAsync(gm, ".playerbot siege waypoint add"));
        Assert.Equal(["Playerbot siege: clear refused (siege-running)."], await FleetWorld.SayAsync(gm, ".playerbot siege waypoint clear"));

        Assert.Equal(["Playerbot siege: siege 1 stopped."], await FleetWorld.SayAsync(gm, ".playerbot siege stop"));
        Assert.Null(world.Coordinator.Siege);
        Assert.Equal("stopped: gm", world.Coordinator.SiegeOutcome);
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Count(g => g.Siege is not null) == 0), await world.TraceAsync());
        Assert.Equal(["Playerbot siege: none is running."], await FleetWorld.SayAsync(gm, ".playerbot siege stop"));
    }
}

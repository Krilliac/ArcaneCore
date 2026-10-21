using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Tests.Playerbots.Party;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// <c>.playerbot siege status [$leader]</c> and <c>siege watch [on|off]</c>: what the GM reads of a running siege, and the lines the coordinator
/// sends a watching GM as the raids' orders change. Memory only.
/// </summary>
public sealed class PlayerbotSiegeStatusTests
{
    private static string Entry => GroupTestWorld.OgreEntry.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<WorldTestClient> AddGm(GroupTestWorld world)
    {
        WorldTestClient gm = await world.World.EnterWorldAsync("SIEGESTATGM", "Siegestatgm", AccountSecurity.Administrator);
        await gm.CollectAsync();
        return gm;
    }

    private static async Task<(WorldTestClient Gm, PlayerbotSiege Siege)> StartedSiege(GroupTestWorld world)
    {
        await world.AddBotAsync("Sgstaa", 1, 1, 10, [GroupTestWorld.Taunt]);
        await world.AddBotAsync("Sgstab", 1, 1, 10, [GroupTestWorld.Taunt]);
        await world.AddBotAsync("Sgstac", 1, 5, 10, []);
        await world.AddBotAsync("Sgstad", 1, 5, 10, []);
        await world.RunAsync(2_000);
        WorldTestClient gm = await AddGm(world);
        await FleetWorld.SayAsync(gm, ".playerbot siege waypoint add");
        List<string> started = await FleetWorld.SayAsync(gm, $".playerbot siege start Sgsta* {Entry} raids 2", lines: 2);
        Assert.Equal("Playerbot siege: matched=4 enlisted=4 skipped=0", started[0]);
        return (gm, world.Coordinator.Siege!);
    }

    [Fact]
    public async Task Status_AndWatch_WithoutASiege_SaySoAndTouchNothing()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([]);
        await using WorldTestClient gm = await AddGm(world);

        Assert.Equal(["Playerbot siege: none is running."], await FleetWorld.SayAsync(gm, ".playerbot siege status"));
        Assert.Equal(["Playerbot siege: none is running."], await FleetWorld.SayAsync(gm, ".playerbot siege watch on"));
        Assert.Equal(["Playerbot siege: none is running."], await FleetWorld.SayAsync(gm, ".playerbot siege watch off"));
    }

    [Fact]
    public async Task Status_ListsTheSiegeAndEachRaid_AndOneRaidByItsLeader()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([]);
        (WorldTestClient gm, PlayerbotSiege siege) = await StartedSiege(world);
        await using WorldTestClient _ = gm;
        Assert.True(await world.RunUntilAsync(30_000, () => siege.Raids.All(r => r.Group is { State: PlayerbotGroupState.Engaging } && r.Detail.Length > 0)), await world.TraceAsync());

        List<string> all = await FleetWorld.SayAsync(gm, ".playerbot siege status", lines: 3);

        Assert.StartsWith($"Siege {siege.Id}: 2 raids, objective {Entry}, step 0/1, running ", all[0], StringComparison.Ordinal);
        Assert.StartsWith($"raid 1 ({siege.Raids[0].Group!.Find(siege.Raids[0].Group!.LeaderBotId)!.Name}, 2/2 alive): ", all[1], StringComparison.Ordinal);
        Assert.StartsWith($"raid 2 ({siege.Raids[1].Group!.Find(siege.Raids[1].Group!.LeaderBotId)!.Name}, 2/2 alive): ", all[2], StringComparison.Ordinal);

        string leader = siege.Raids[1].Group!.Find(siege.Raids[1].Group!.LeaderBotId)!.Name;
        List<string> one = await FleetWorld.SayAsync(gm, $".playerbot siege status {leader.ToLowerInvariant()}", lines: 2);
        Assert.StartsWith($"raid 2 ({leader}, ", one[0], StringComparison.Ordinal);
        Assert.StartsWith("said: ", one[1], StringComparison.Ordinal);
        Assert.Equal(["Playerbot siege: no raid led by 'Nobodyhere'."], await FleetWorld.SayAsync(gm, ".playerbot siege status Nobodyhere"));
    }

    [Fact]
    public async Task Watch_SendsTheGmALinePerRaid_AsTheOrdersChange_AndStopsWhenSwitchedOff()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([]);
        (WorldTestClient gm, PlayerbotSiege siege) = await StartedSiege(world);
        await using WorldTestClient _ = gm;

        Assert.Equal(["Playerbot siege: watching on."], await FleetWorld.SayAsync(gm, ".playerbot siege watch on"));
        Assert.Single(siege.Watchers);
        await world.RunAsync(6_000);
        var heard = new List<string>();
        while (heard.Count < 2)
        {
            ChatMessage message = await gm.ReadChatAsync();
            if (message.Type == ChatType.System && message.Text.StartsWith($"Siege {siege.Id} raid ", StringComparison.Ordinal)) heard.Add(message.Text);
        }

        Assert.Contains(heard, line => line.StartsWith($"Siege {siege.Id} raid 1 (", StringComparison.Ordinal));
        Assert.Contains(heard, line => line.StartsWith($"Siege {siege.Id} raid 2 (", StringComparison.Ordinal));

        Assert.Equal(["Playerbot siege: watching off."], await FleetWorld.SayAsync(gm, ".playerbot siege watch off"));
        Assert.Empty(siege.Watchers);
        Assert.Equal(["Playerbot siege: you are not watching."], await FleetWorld.SayAsync(gm, ".playerbot siege watch"));
        List<string> bad = await FleetWorld.SayAsync(gm, ".playerbot siege watch maybe", lines: 2);
        Assert.StartsWith("Syntax: .playerbot siege watch", bad[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADetachDuringTheSiege_TellsTheWatchersItEnded_WithReasonReload()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([]);
        (WorldTestClient gm, PlayerbotSiege siege) = await StartedSiege(world);
        await using WorldTestClient _ = gm;
        Assert.Equal(["Playerbot siege: watching on."], await FleetWorld.SayAsync(gm, ".playerbot siege watch on"));

        await world.OnWorldAsync(() =>
        {
            world.Coordinator.Detach(new ArcaneCore.World.Features.FeatureHandover());
            return true;
        });

        while (true)
        {
            ChatMessage message = await gm.ReadChatAsync();
            if (message.Type == ChatType.System && message.Text == $"Siege {siege.Id} ended: stopped: reload") break;
        }
    }
}

using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Teleport;
using ArcaneCore.World.Npc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;
using ArcaneCore.World.Bots;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// A real player's party bot, inviting a nearby player, and a pair whose player logs out or leaves. Split from the former <c>PlayerbotGroupPlayerTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotGroupPlayerTestBase"/>.
/// </summary>
public sealed class PlayerbotGroupPlayerInviteWorldTests : PlayerbotGroupPlayerTestBase
{
    [Fact]
    public async Task ARealPlayersPartyBot_StaysItsMastersBot_AndTheCoordinatorLeavesItAlone()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], options => options.Party.Allowlist = ["Realmaster"]);
        var bot = await world.AddBotAsync("Partyone", Human, Mage, 10, [Fireball]);
        Assert.True(await world.RunUntilAsync(60_000, () => world.Coordinator.WaitingBots.Any(w => w.BotId == bot.Id)), await world.TraceAsync());
        await using WorldTestClient master = await world.World.EnterWorldAsync("REALMASTER", "Realmaster", AccountSecurity.Player);

        PlayerbotOperationResult invited = await world.OnWorldAsync(() => world.Bots.InviteToGroup(world.World.Host.World.FindOnlinePlayer("Realmaster")!, bot.Name));
        Assert.True(invited.Success, invited.Code);

        // The party AI drives it (follows its master); the coordinator stops waiting for it and never takes it into a bot group.
        Assert.True(await world.RunUntilAsync(5_000, () => world.Bots.IsPartyDriven(bot.Id)), await world.TraceAsync());
        await world.RunAsync(10_000);
        Assert.True(world.Bots.IsPartyDriven(bot.Id));
        Assert.False(world.Coordinator.Drives(bot.Id));
        Assert.DoesNotContain(world.Coordinator.WaitingBots, w => w.BotId == bot.Id);
        Assert.Empty(world.Coordinator.Groups);
        Assert.Empty(await world.OnWorldAsync(() => world.Bots.FindBrain(bot.Id)?.GroupHeld.ToArray() ?? []));
        Assert.Null((await world.OnWorldAsync(() => world.Bots.Snapshot().Single(s => s.BotId == bot.Id))).Group);
    }

    [Fact]
    public async Task WithInvitePlayers_AGroupOneShort_InvitesANearbyRealPlayer_ThroughTheOrdinaryInvitation()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest], options => options.Groups.InvitePlayers = true);
        await using WorldTestClient player = await world.World.EnterWorldAsync("GROUPHELPER", "Grouphelper", AccountSecurity.Player);
        var bot = await world.AddBotAsync("Askerone", Human, Warrior, 1, []);
        await GivePlayerQuestAsync(world, "Grouphelper", DuoQuest);
        await RememberPlayerAsync(world, bot, "Grouphelper", 30);

        Assert.True(await world.RunUntilAsync(60_000, () => PlayerInvited(world)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Assert.Equal(bot.Id, group.LeaderBotId); // a real player never leads a bot group
        byte[] invite = await player.ReadUntilAsync(WorldOpcode.SmsgGroupInvite);
        Assert.Equal(bot.Name, new PacketReader(invite).ReadCString());

        // The player declines: the group is given up and that player is not asked again for a while.
        await player.SendAsync(WorldOpcode.CmsgGroupDecline, []);
        Assert.True(await world.RunUntilPacedAsync(() => world.Coordinator.Groups.Count == 0, 10_000), await world.TraceAsync()); // the packet is read from the socket in real time
        Assert.Contains(world.Coordinator.Events, e => e.Contains("Grouphelper did not join", StringComparison.Ordinal));
        await world.RunAsync(10_000);
        Assert.Empty(world.Coordinator.Groups);
    }

    /// <summary>
    /// A tank-class player (the test character is a warrior) is dealt the only tank slot when no bot tanks, and would lead: the group still
    /// forms with them, a bot leading, instead of the plan being dropped.
    /// </summary>
    [Fact]
    public async Task WithInvitePlayers_ATankClassPlayer_JoinsABotGroupWithoutATank_AndABotLeads()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest], options => options.Groups.InvitePlayers = true);
        await using WorldTestClient player = await world.World.EnterWorldAsync("GROUPTANK", "Grouptank", AccountSecurity.Player);
        var bot = await world.AddBotAsync("Askermage", Human, Mage, 1, [Fireball]);
        await GivePlayerQuestAsync(world, "Grouptank", DuoQuest);
        await RememberPlayerAsync(world, bot, "Grouptank", 30);

        Assert.True(await world.RunUntilAsync(60_000, () => PlayerInvited(world)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Assert.Equal(bot.Id, group.LeaderBotId);
        byte[] invite = await player.ReadUntilAsync(WorldOpcode.SmsgGroupInvite);
        Assert.Equal(bot.Name, new PacketReader(invite).ReadCString());
    }

    /// <summary>One bot and one real player are a group: the player counts toward the two members, so the coordinator keeps it and goes on to work the objective.</summary>
    [Fact]
    public async Task OneBotAndOneRealPlayer_AreAGroup_TheCoordinatorKeeps_AndWorksTheObjectiveWith()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest], options => options.Groups.InvitePlayers = true);
        await using WorldTestClient player = await world.World.EnterWorldAsync("GROUPPAIR", "Grouppair", AccountSecurity.Player);
        var bot = await world.AddBotAsync("Askerpair", Human, Warrior, 1, []);
        await GivePlayerQuestAsync(world, "Grouppair", DuoQuest);
        await RememberPlayerAsync(world, bot, "Grouppair", 30);
        Assert.True(await world.RunUntilAsync(60_000, () => PlayerInvited(world)), await world.TraceAsync());
        await player.ReadUntilAsync(WorldOpcode.SmsgGroupInvite);

        await AcceptAsync(player);
        Assert.True(await world.RunUntilAsync(10_000, () => world.Coordinator.Groups.Any(g => g.Members.Any(m => m.Real && m.Joined))), await world.TraceAsync());

        // Several coordinator passes later (each 2 s) the pair is still one group, and it has moved on from forming.
        Assert.True(await world.RunUntilAsync(60_000, () => world.Coordinator.Groups.Any(g => g.State is PlayerbotGroupState.Travelling or PlayerbotGroupState.Engaging)), await world.TraceAsync());
        await world.RunAsync(20_000);
        PlayerbotGroupCoordinator.BotGroup group = Assert.Single(world.Coordinator.Groups);
        Assert.Equal(2, group.Members.Count);
        Assert.Contains(group.Members, m => m.Real && m.Joined);
        Assert.DoesNotContain(world.Coordinator.Events, e => e.Contains("too-few", StringComparison.Ordinal));
    }

    /// <summary>The server keeps a logged-out player in its group, so the pair is still "two members"; a player who is not online does not count toward the two.</summary>
    [Fact]
    public async Task APairWhosePlayerLogsOut_Ends_BecauseAnOfflinePlayerDoesNotCountTowardTheTwo()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest], options => options.Groups.InvitePlayers = true);
        await using WorldTestClient player = await FormPairAsync(world, "GROUPGONE", "Groupgone", "Askergone");

        await LogOutAsync(world, "Groupgone");

        Assert.True(await world.RunUntilAsync(15_000, () => world.Coordinator.Groups.Count == 0), await world.TraceAsync());
        Assert.Contains(world.Coordinator.Events, e => e.Contains("player-gone", StringComparison.Ordinal));
    }

    /// <summary>A run that already succeeded is not failed because the player logs out while the bots walk out: it ends as completed.</summary>
    [Fact]
    public async Task APairWhosePlayerLogsOutWhileLeaving_AfterASuccess_EndsAsCompleted_NotPlayerGone()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest], options => options.Groups.InvitePlayers = true);
        await using WorldTestClient player = await FormPairAsync(world, "GROUPLEAVING", "Groupleaving", "Askerleaving");
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();

        int held = 0;
        // The group has succeeded and is walking out (held in Leaving, as a bot still inside an instance would hold it) until the logout completes.
        Assert.True(await world.RunUntilAsync(40_000, () =>
        {
            bool online = world.World.Host.World.FindOnlinePlayer("Groupleaving") is not null;
            if (online && world.Coordinator.Groups.Contains(group))
            {
                group.Succeeded = true;
                group.Reason = null;
                group.State = PlayerbotGroupState.Leaving;
                group.StateSinceMs = world.World.Host.World.NowMs;
                if (++held == 3) world.World.Host.World.LogoutPlayer(world.World.Host.World.FindOnlinePlayer("Groupleaving")!);
            }

            return !online;
        }), await world.TraceAsync());

        Assert.True(await world.RunUntilAsync(15_000, () => world.Coordinator.Groups.Count == 0), await world.TraceAsync());
        Assert.True(group.Succeeded);
        Assert.Equal("completed", group.Reason);
        Assert.Equal(0, world.Coordinator.Totals.Failed);
        Assert.DoesNotContain(world.Coordinator.Events, e => e.Contains("player-gone", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APairWhosePlayerLeavesTheGroup_Ends()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest], options => options.Groups.InvitePlayers = true);
        await using WorldTestClient player = await FormPairAsync(world, "GROUPLEAVE", "Groupleave", "Askerleave");

        ObjectGuid guid = await world.OnWorldAsync(() => world.World.Host.World.FindOnlinePlayer("Groupleave")!.Guid);

        await player.SendAsync(WorldOpcode.CmsgGroupDisband, []);

        // The server reads the packet from the socket in real time, which the manual clock does not wait for: poll for its handling in real
        // time (the world thread keeps ticking without game time moving), and only then give the coordinator its original game budget.
        System.Diagnostics.Stopwatch wall = System.Diagnostics.Stopwatch.StartNew();
        bool handled = false;
        while (!handled && wall.Elapsed < TimeSpan.FromSeconds(30))
        {
            handled = await world.OnWorldAsync(() => world.GroupManager.GetGroup(guid) is null);
            if (!handled) await Task.Delay(20);
        }

        Assert.True(handled, "the server handled the disband" + Environment.NewLine + await world.TraceAsync());
        Assert.True(await world.RunUntilAsync(15_000, () => world.Coordinator.Groups.Count == 0), await world.TraceAsync());
        Assert.Contains(world.Coordinator.Events, e => e.Contains("Groupleave left", StringComparison.Ordinal));
        Assert.Contains(world.Coordinator.Events, e => e.Contains("too-few", StringComparison.Ordinal));
    }
}

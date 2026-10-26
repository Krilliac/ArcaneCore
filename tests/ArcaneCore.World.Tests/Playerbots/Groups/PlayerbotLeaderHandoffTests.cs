using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Teleport;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// The pure rules of a leader handoff (<see cref="PlayerbotLeaderHandoff"/>, <see cref="PlayerbotClearList"/>): the clear list's order and
/// pruning, the role assignment and the wing choice.
/// </summary>
public sealed class PlayerbotLeaderHandoffRuleTests
{
    private static (bool Alive, Vector3 Position)? Alive(Vector3 at) => (true, at);

    [Fact]
    public void TheClearList_VisitsTheNearestFirst_KeepsItsTarget_AndDropsTheDead()
    {
        var list = new PlayerbotClearList(
        [
            new(3, 100, new Vector3(30, 0, 0)),
            new(1, 100, new Vector3(10, 0, 0)),
            new(2, 101, new Vector3(20, 0, 0)),
        ]);
        var dead = new HashSet<ulong>();
        (bool Alive, Vector3 Position)? Lookup(ulong guid) => dead.Contains(guid) ? (false, default) : Alive(new Vector3(guid * 10, 0, 0));

        Assert.Equal(3, list.Initial);
        Assert.Equal(1ul, list.Next(Vector3.Zero, Lookup)!.Value.Guid);
        // The current target is kept while it lives, even when the leader now stands nearer another.
        Assert.Equal(1ul, list.Next(new Vector3(30, 0, 0), Lookup)!.Value.Guid);
        dead.Add(1);
        Assert.Equal(3ul, list.Next(new Vector3(30, 0, 0), Lookup)!.Value.Guid);
        Assert.Equal(2, list.Remaining);
        list.GiveUp(3);
        Assert.Equal(1, list.GivenUp);
        Assert.Equal(2ul, list.Next(new Vector3(30, 0, 0), Lookup)!.Value.Guid);
        dead.Add(2);
        Assert.Null(list.Next(Vector3.Zero, Lookup));
        Assert.Equal(0, list.Remaining);
    }

    [Fact]
    public void TheClearList_SkipsAShunnedCreature_ButKeepsIt_AndRebuildsOnceWithoutTheGivenUp()
    {
        var list = new PlayerbotClearList([new(1, 100, Vector3.Zero), new(2, 100, new Vector3(50, 0, 0))]);
        (bool Alive, Vector3 Position)? Lookup(ulong guid) => Alive(guid == 1 ? Vector3.Zero : new Vector3(50, 0, 0));

        Assert.Equal(2ul, list.Next(Vector3.Zero, Lookup, skip: guid => guid == 1)!.Value.Guid);
        Assert.Equal(2, list.Remaining);
        list.GiveUp(2);

        // Empty after the first: rebuilt once with what lives now, never the given-up creature; a second rebuild does nothing.
        Assert.True(list.Rebuild([new(2, 100, new Vector3(50, 0, 0)), new(7, 102, new Vector3(5, 0, 0))]));
        Assert.True(list.Rebuilt);
        Assert.Equal(1, list.Remaining); // only 7: the rebuild replaces the list
        Assert.False(list.Rebuild([new(9, 100, Vector3.Zero)]));
    }

    [Fact]
    public void TheRoles_TakeOneTankAndOneHealerForFive_TheRestDamage_AndNeverRefuse()
    {
        var options = new PlayerbotGroupOptions();
        PlayerbotHandoffBot[] five =
        [
            new(Guid.NewGuid(), Class.Mage, PlayerbotRole.RangeDps, 20),
            new(Guid.NewGuid(), Class.Priest, PlayerbotRole.Healer, 20),
            new(Guid.NewGuid(), Class.Warrior, PlayerbotRole.Tank, 20),
            new(Guid.NewGuid(), Class.Rogue, PlayerbotRole.MeleeDps, 20),
            new(Guid.NewGuid(), Class.Hunter, PlayerbotRole.RangeDps, 20),
        ];
        PlayerbotGroupRole[] roles = PlayerbotLeaderHandoff.AssignRoles(five, raid: false, options);
        Assert.Equal(PlayerbotGroupRole.Tank, roles[2]);
        Assert.Equal(PlayerbotGroupRole.Healer, roles[1]);
        Assert.Equal(3, roles.Count(r => r == PlayerbotGroupRole.Damage));

        // No one can tank or heal: everyone damages, and the clear still goes on.
        PlayerbotGroupRole[] mages = PlayerbotLeaderHandoff.AssignRoles(
            [new(Guid.NewGuid(), Class.Mage, PlayerbotRole.RangeDps, 20), new(Guid.NewGuid(), Class.Rogue, PlayerbotRole.MeleeDps, 20)], raid: false, options);
        Assert.All(mages, r => Assert.Equal(PlayerbotGroupRole.Damage, r));
    }

    [Fact]
    public void TheWing_IsThePlanWhosePlacesAreNearestTheLeader_AndNoneWithoutAPlan()
    {
        PlayerbotRunCatalogue catalogue = RunFixtures.Build();
        PlayerbotRunPlan[] wings = [.. catalogue.Plans.Where(p => p.MapId == RunFixtures.ScarletMonastery)];
        Assert.True(wings.Length > 1);
        foreach (PlayerbotRunPlan wing in wings)
        {
            Assert.Same(wing, PlayerbotLeaderHandoff.ChooseWing(catalogue.Plans, RunFixtures.ScarletMonastery, wing.InsideStart));
            if (wing.Bosses.Count > 0) Assert.Same(wing, PlayerbotLeaderHandoff.ChooseWing(catalogue.Plans, RunFixtures.ScarletMonastery, wing.Bosses[^1].Position));
        }

        Assert.Null(PlayerbotLeaderHandoff.ChooseWing(catalogue.Plans, 9999, Vector3.Zero));
    }

    [Fact]
    public void TheChatLines_NameTheInstance_AndSayWhyInWords()
    {
        Assert.Equal("I'll lead the clear of Ragefire Chasm.", PlayerbotLeaderHandoff.TakeOverLine("Ragefire Chasm"));
        Assert.Equal("The Deadmines is clear. The lead is yours again.", PlayerbotLeaderHandoff.ClearedLine("The Deadmines"));
        Assert.Equal("Stopping the clear of X: we wiped too often.", PlayerbotLeaderHandoff.StoppedLine("X", "wiped"));
        Assert.Equal("this is taking too long", PlayerbotLeaderHandoff.ReasonText("clear-timeout"));
        Assert.True(PlayerbotLeaderHandoff.TakesTheLeadBack(ArcaneCore.World.Playerbots.Party.PlayerbotPartyCommand.Follow));
    }
}

/// <summary>
/// A real player brings managed bots into an instance and passes the lead to one of them (<c>PlayerbotGroupCoordinator.Handoff.cs</c>):
/// the coordinator adopts the player's own group as a run of that instance and the bots clear it; outside an instance nothing happens;
/// an order from the player takes the lead back and the party AI drives the bots again.
/// </summary>
public sealed class PlayerbotLeaderHandoffWorldTests
{
    private const byte Human = 1, Warrior = 1, Priest = 5, Mage = 8;
    private const uint Taunt = GroupTestWorld.Taunt;
    private const uint LesserHeal = 2050, Fireball = 133, Smite = 585;
    private const string Master = "Handmaster";

    [Fact]
    public async Task HandingTheLeadToABot_InsideRagefireChasm_AdoptsTheGroupAsARunOfThatInstance_AndClearsTheFirstPack()
    {
        await using GroupTestWorld world = await StartAsync(rfc: true);
        await using WorldTestClient master = await world.World.EnterWorldAsync("HANDMASTER", Master, AccountSecurity.Player);
        // A party holds five (Group.MaxGroupSize): the player and four bots. A fifth bot cannot join a non-raid group.
        (Guid Id, string Name)[] bots = await AddBots(world, 4, level: 16);
        await FormGroup(world, bots, raid: false);
        await EnterInstance(world, bots, GroupRfcContent.Map, new Vector3(0.797643f, -8.23429f, -15.5288f));
        await master.CollectAsync();

        await PassLead(world, bots[0]);
        PlayerbotGroupCoordinator.BotGroup group = await Adopted(world);
        Assert.NotNull(group.Handoff);
        Assert.Equal(GroupRfcContent.Map, group.Goal.MapId);
        Assert.Equal(PlayerbotLeaderHandoff.Source, group.Goal.Source);
        Assert.NotNull(group.Run); // the catalogue routes Ragefire Chasm
        Assert.Null(group.Handoff!.Sweep);
        Assert.Equal(PlayerbotRunStep.Clear, group.Run!.Step);
        Assert.Equal(bots[0].Id, group.LeaderBotId);
        Assert.Equal(4, group.Members.Count(m => !m.Real));
        Assert.Single(group.Members, m => m.Real);
        Assert.All(bots, b => Assert.True(world.Coordinator.Drives(b.Id)));
        Assert.Contains(world.Coordinator.Events, e => e.Contains($"adopted from {Master}'s party", StringComparison.Ordinal));
        Assert.Equal(1, world.Coordinator.HandoffTotals.Started);

        // The take-over line reaches the player in party chat.
        Assert.True(await ChatSaid(world, master, "I'll lead the clear of Ragefire Chasm."), await world.TraceAsync());

        // The run progresses: the first pack dies.
        Assert.True(await world.RunUntilAsync(180_000, () => Pack(world, bots[0].Id) is { Length: 3 } pack && pack.All(c => !c.IsAlive)), await world.TraceAsync());
        // PullsLeft is recounted when the leader next picks a target, one think after the last of the pack dies.
        Assert.True(await world.RunUntilAsync(30_000, () => group.Run.PullsLeft == 0 || group.Run.Killed > 0), await world.TraceAsync());
        Assert.Same(group, world.Coordinator.HandoffOf(await ServerGroupId(world, bots[0])));
    }

    [Fact]
    public async Task HandingTheLeadToABot_OutsideAnInstance_IsNotAdopted_AndTheBotsStayWithTheParty()
    {
        await using GroupTestWorld world = await StartAsync(rfc: false);
        await using WorldTestClient master = await world.World.EnterWorldAsync("HANDMASTER", Master, AccountSecurity.Player);
        (Guid Id, string Name)[] bots = await AddBots(world, 3, level: 10);
        await FormGroup(world, bots, raid: false);
        Assert.True(await world.RunUntilAsync(5_000, () => bots.All(b => world.Bots.IsPartyDriven(b.Id))), await world.TraceAsync());

        await PassLead(world, bots[0]);
        await world.RunAsync(5_000);
        Assert.Empty(world.Coordinator.Groups);
        Assert.Equal(0, world.Coordinator.HandoffTotals.Started);
        Assert.All(bots, b => Assert.False(world.Coordinator.Drives(b.Id)));
        Assert.Equal(await world.OnWorldAsync(() => world.Player(bots[0].Id).Guid), await LeaderOf(world, bots[0]));
    }

    [Fact]
    public async Task APartyFollowOrder_TakesTheLeadBack_AndThePartyAIDrivesTheBotsAgain()
    {
        await using GroupTestWorld world = await StartAsync(rfc: true);
        await using WorldTestClient master = await world.World.EnterWorldAsync("HANDMASTER", Master, AccountSecurity.Player);
        (Guid Id, string Name)[] bots = await AddBots(world, 3, level: 16);
        await FormGroup(world, bots, raid: false);
        await EnterInstance(world, bots, GroupRfcContent.Map, new Vector3(0.797643f, -8.23429f, -15.5288f));
        await PassLead(world, bots[0]);
        PlayerbotGroupCoordinator.BotGroup group = await Adopted(world);
        await master.CollectAsync();

        await master.SendChatAsync(ChatType.Party, Language.Common, "follow");
        Assert.True(await world.RunUntilPacedAsync(() => world.Coordinator.Groups.Count == 0, 5_000), await world.TraceAsync()); // the order is read from the socket in real time
        ObjectGuid masterGuid = await world.OnWorldAsync(() => world.World.Host.World.FindOnlinePlayer(Master)!.Guid);
        Assert.True(await world.RunUntilAsync(5_000, () => world.GroupManager.GetGroup(masterGuid)?.LeaderGuid == masterGuid), await world.TraceAsync());
        Assert.True(await world.RunUntilAsync(5_000, () => bots.All(b => world.Bots.IsPartyDriven(b.Id) && !world.Coordinator.Drives(b.Id))), await world.TraceAsync());
        Assert.False(group.Succeeded);
        Assert.Equal("called-back", group.Reason);
        Assert.Equal(1, world.Coordinator.HandoffTotals.Ended);
        // The bots stay in the player's group, inside the instance.
        Assert.Equal(4, await world.OnWorldAsync(() => world.GroupManager.GetGroup(masterGuid)!.MemberCount));
        Assert.All(bots, b => Assert.Equal(GroupRfcContent.Map, world.Player(b.Id).MapId));
    }

    [Fact]
    public async Task WithoutACatalogueRoute_TheBotsClearTheListedCreatures_ThenSayItIsClear_AndHandTheLeadBack()
    {
        await using GroupTestWorld world = await StartAsync(rfc: false, dungeon: true);
        await using WorldTestClient master = await world.World.EnterWorldAsync("HANDMASTER", Master, AccountSecurity.Player);
        (Guid Id, string Name)[] bots = await AddBots(world, 3, level: 16);
        await FormGroup(world, bots, raid: false);
        await EnterInstance(world, bots, 36, new Vector3(-16.4f, -383.07f, DeadminesTestContent.InsideFloor));
        await PassLead(world, bots[0]);
        PlayerbotGroupCoordinator.BotGroup group = await Adopted(world);
        Assert.Equal(36u, group.Goal.MapId);
        if (group.Run is null)
        {
            Assert.NotNull(group.Handoff!.Sweep);
            Assert.Equal(1, group.Handoff.Sweep!.Initial); // the boss is the only hostile creature inside
        }

        await master.CollectAsync();
        ObjectGuid masterGuid = await world.OnWorldAsync(() => world.World.Host.World.FindOnlinePlayer(Master)!.Guid);
        Assert.True(await world.RunUntilAsync(180_000, () => world.Coordinator.Groups.Count == 0), await world.TraceAsync());
        Assert.True(group.Succeeded, await world.TraceAsync());
        Assert.Equal("completed", group.Reason);
        Assert.Equal(1, world.Coordinator.HandoffTotals.Completed);
        Assert.False(await world.OnWorldAsync(() => world.Player(bots[0].Id).Map!.FindUpdater<CreatureMapSystem>()!.Creatures
            .Any(c => c.Entry == GroupTestWorld.BossEntry && c.IsAlive)));
        Assert.True(await ChatSaid(world, master, PlayerbotLeaderHandoff.ClearedLine("The Deadmines")), await world.TraceAsync());
        Assert.True(await world.RunUntilAsync(5_000, () => world.GroupManager.GetGroup(masterGuid)?.LeaderGuid == masterGuid), await world.TraceAsync());
        Assert.True(await world.RunUntilAsync(5_000, () => bots.All(b => world.Bots.IsPartyDriven(b.Id))), await world.TraceAsync());
    }

    [Fact]
    public async Task WithoutACatalogueRoute_WhileEveryCreatureLeftIsShunned_TheClearWaits_AndIsNeitherDoneNorRebuilt()
    {
        await using GroupTestWorld world = await StartAsync(rfc: false, dungeon: true);
        await using WorldTestClient master = await world.World.EnterWorldAsync("HANDMASTER", Master, AccountSecurity.Player);
        (Guid Id, string Name)[] bots = await AddBots(world, 3, level: 16);
        await FormGroup(world, bots, raid: false);
        await EnterInstance(world, bots, 36, new Vector3(-16.4f, -383.07f, DeadminesTestContent.InsideFloor));
        await PassLead(world, bots[0]);
        PlayerbotGroupCoordinator.BotGroup group = await Adopted(world);
        PlayerbotClearList sweep = group.Handoff!.Sweep ?? throw new Xunit.Sdk.XunitException("the map has no catalogue route: a sweep was expected");

        // The group gives up the only creature left for ten minutes (as a stalled fight or an unplanned path does).
        await world.OnWorldAsync(() =>
        {
            Creature boss = world.Player(bots[0].Id).Map!.FindUpdater<CreatureMapSystem>()!.Creatures.Single(c => c.Entry == GroupTestWorld.BossEntry && c.IsAlive);
            group.Shun(boss.Guid, world.World.Host.World.NowMs + 600_000u);
            return true;
        });
        await world.RunAsync(10_000);

        Assert.False(group.Succeeded, await world.TraceAsync());
        Assert.NotEqual("completed", group.Reason);
        Assert.Equal(0, world.Coordinator.HandoffTotals.Completed);
        Assert.Contains(group, world.Coordinator.Groups);
        Assert.False(sweep.Rebuilt); // the one rebuild is kept for grids loaded later
        Assert.Equal(1, sweep.Remaining);
    }

    [Fact]
    public async Task ARaidOfTen_InTwoSubgroups_IsAdoptedWhole_AndKeepsItsSubgroups()
    {
        await using GroupTestWorld world = await StartAsync(rfc: true);
        await using WorldTestClient master = await world.World.EnterWorldAsync("HANDMASTER", Master, AccountSecurity.Player);
        (Guid Id, string Name)[] bots = await AddBots(world, 9, level: 16);
        await FormGroup(world, bots, raid: true);
        byte[] before = await Subgroups(world, bots);
        Assert.Equal(2, before.Distinct().Count());
        await EnterInstance(world, bots, GroupRfcContent.Map, new Vector3(0.797643f, -8.23429f, -15.5288f));

        await PassLead(world, bots[0]);
        PlayerbotGroupCoordinator.BotGroup group = await Adopted(world);
        Assert.True(group.Raid);
        Assert.Equal(9, group.Members.Count(m => !m.Real));
        Assert.Equal(GroupRfcContent.Map, group.Goal.MapId);
        Assert.Equal(9, group.Goal.Size);
        Assert.All(bots, b => Assert.True(world.Coordinator.Drives(b.Id)));
        await world.RunAsync(2_000);
        Assert.Equal(before, await Subgroups(world, bots));
        Assert.Equal(10, await world.OnWorldAsync(() => world.GroupManager.GetGroup(world.Player(bots[0].Id).Guid)!.MemberCount));
    }

    private static Task<GroupTestWorld> StartAsync(bool rfc, bool dungeon = false) => GroupTestWorld.StartAsync([], options =>
    {
        options.MaxBots = 12;
        options.Groups.DungeonRuns.Enabled = true;
        options.Groups.DungeonRuns.StartChancePerMinute = 0;
        options.Party.TeleportToLeader = false;
        options.Chat.Enabled = false;
    }, dungeon: dungeon, rfc: rfc);

    private static async Task<(Guid Id, string Name)[]> AddBots(GroupTestWorld world, int count, byte level)
    {
        var bots = new List<(Guid Id, string Name)>
        {
            await world.AddBotAsync("Handtank", Human, Warrior, level, [Taunt]),
            await world.AddBotAsync("Handheal", Human, Priest, level, [LesserHeal, Smite]),
        };
        for (int i = 2; i < count; i++) bots.Add(await world.AddBotAsync("Handmage" + (char)('a' + i), Human, Mage, level, [Fireball]));
        await world.RunAsync(1_000);
        return [.. bots];
    }

    /// <summary>The player invites the bots (world thread, as the ordinary invitation); a raid is converted after the first four.</summary>
    private static Task FormGroup(GroupTestWorld world, (Guid Id, string Name)[] bots, bool raid) => world.OnWorldAsync(() =>
    {
        Player leader = world.World.Host.World.FindOnlinePlayer(Master)!;
        for (int i = 0; i < bots.Length; i++)
        {
            if (raid && i == 4) world.GroupManager.ConvertToRaid(leader);
            world.GroupManager.Invite(leader, bots[i].Name);
            world.GroupManager.Accept(world.Player(bots[i].Id));
        }

        Group group = world.GroupManager.GetGroup(leader.Guid)!;
        Assert.Equal(bots.Length + 1, group.MemberCount);
        Assert.Equal(raid, group.IsRaid);
        return true;
    });

    private static async Task EnterInstance(GroupTestWorld world, (Guid Id, string Name)[] bots, uint map, Vector3 at)
    {
        await world.OnWorldAsync(() =>
        {
            TeleportFeature teleports = world.World.Services.GetRequiredService<TeleportFeature>();
            foreach ((Guid id, string _) in bots) Assert.True(teleports.Teleports.TeleportTo(world.Player(id), map, at.X, at.Y, at.Z, 0f));
            return true;
        });
        Assert.True(await world.RunUntilAsync(30_000, () => bots.All(b => world.Player(b.Id) is { IsInWorld: true } p && p.MapId == map)), await world.TraceAsync());
        Assert.Single(await world.OnWorldAsync(() => bots.Select(b => world.Player(b.Id).Map!.InstanceId).Distinct().ToArray()));
    }

    /// <summary>The player passes the lead to <paramref name="bot"/> (CMSG_GROUP_SET_LEADER's handler, on the world thread).</summary>
    private static Task PassLead(GroupTestWorld world, (Guid Id, string Name) bot) => world.OnWorldAsync(() =>
    {
        world.GroupManager.SetLeader(world.World.Host.World.FindOnlinePlayer(Master)!, world.Player(bot.Id).Guid);
        Assert.Equal(world.Player(bot.Id).Guid, world.GroupManager.GetGroup(world.Player(bot.Id).Guid)!.LeaderGuid);
        return true;
    });

    private static async Task<PlayerbotGroupCoordinator.BotGroup> Adopted(GroupTestWorld world)
    {
        Assert.True(await world.RunUntilAsync(5_000, () => world.Coordinator.Groups.Any(g => g.Handoff is not null)), await world.TraceAsync());
        return world.Coordinator.Groups.Single(g => g.Handoff is not null);
    }

    private static Task<ObjectGuid> LeaderOf(GroupTestWorld world, (Guid Id, string Name) bot)
        => world.OnWorldAsync(() => world.GroupManager.GetGroup(world.Player(bot.Id).Guid)!.LeaderGuid);

    private static Task<uint> ServerGroupId(GroupTestWorld world, (Guid Id, string Name) bot)
        => world.OnWorldAsync(() => world.GroupManager.GetGroup(world.Player(bot.Id).Guid)!.Id);

    private static Task<byte[]> Subgroups(GroupTestWorld world, (Guid Id, string Name)[] bots) => world.OnWorldAsync(() =>
    {
        Group group = world.GroupManager.GetGroup(world.Player(bots[0].Id).Guid)!;
        return bots.Select(b => group.Members.Single(m => m.Guid == world.Player(b.Id).Guid).SubGroup).ToArray();
    });

    /// <summary>Whether the player's client got <paramref name="text"/> in party or raid chat (the world runs between reads).</summary>
    private static async Task<bool> ChatSaid(GroupTestWorld world, WorldTestClient client, string text)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            foreach ((WorldOpcode opcode, byte[] payload) in await client.CollectAsync())
                if (opcode == WorldOpcode.SmsgMessagechat && System.Text.Encoding.UTF8.GetString(payload).Contains(text, StringComparison.Ordinal)) return true;
            await world.RunAsync(250);
        }

        return false;
    }

    private static Creature[] Pack(GroupTestWorld world, Guid member)
        => world.Player(member).MapId == GroupRfcContent.Map && world.Player(member).Map?.FindUpdater<CreatureMapSystem>() is { } creatures
            ? [.. creatures.Creatures.Where(c => c.Entry is GroupRfcContent.Trogg or GroupRfcContent.Shaman)] : [];
}

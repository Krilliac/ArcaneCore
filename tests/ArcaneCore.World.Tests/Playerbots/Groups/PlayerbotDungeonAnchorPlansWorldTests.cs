using ArcaneCore.Game.Guilds;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// Companion-only waiters, plans of derived-only waiters, and a formed group losing members while travelling. Split from the former <c>PlayerbotDungeonAnchorWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotDungeonAnchorWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotDungeonAnchorPlansWorldTests : PlayerbotDungeonAnchorWorldTestBase
{
    // --- recruiting follows the qualifying anchor, not the oldest waiter ------------------------------------------------------

    [Fact]
    public async Task ACompanionOnlyWaiter_OlderThanTheInBandSeed_DoesNotPullFreeLevel35BotsIn_ButARecruitNearTheSeedStillComes()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], o =>
        {
            o.MoveSpeed = 0.1f; // bots crawl: the free recruits never walk to the quest giver
            NoOwnGuilds(o);
            o.Groups.Cooperation.RecruitWaitSeconds = 5;
            o.Groups.Cooperation.RecruitRadiusYards = 1500;
            o.Groups.Cooperation.RecruitBaseChance = 1f;
        }, dungeon: true);
        var far = EntranceArea + new System.Numerics.Vector3(0, -900f, 0);

        // The first seed (in band, 27) makes the level-31 priest wait on a forming-group reason; a second in-band warrior then waits too (younger than the priest);
        // the first seed leaves. The priest is now the oldest waiter, but it cannot anchor: the second seed (27) is the qualifying anchor.
        var first = await AddSeedAsync(world, "Firstseed", 27);
        // A mage: the waiters (two warriors and a mage) have no healer, so they cannot form a group among themselves.
        var companion = await world.AddBotAsync("Oldcomp", Human, Mage, 31, [Fireball], EntranceArea);
        Assert.True(await world.RunUntilAsync(60_000, () => world.HasQuest(companion.Id, DungeonQuest) && Noted(world, "Oldcomp needs a group")), await world.TraceAsync());
        Assert.Contains(await EventsAsync(world), e => e.Contains("Oldcomp needs a group", StringComparison.Ordinal) && e.Contains("fit=out-of-band 31 vs ", StringComparison.Ordinal));
        var second = await AddSeedAsync(world, "Secondseed", 27);
        var stopped = await world.Bots.StopAsync(first.Name);
        Assert.True(stopped.Success, stopped.Code);
        // Precondition: the two remaining waiters (no healer) are not a group, and the companion survives a seed recheck because the second seed is in band.
        await world.RunAsync(2 * PlayerbotGroupCoordinator.SeedRecheckMs);
        Assert.Empty(world.Coordinator.Groups);
        Assert.Contains(world.Coordinator.WaitingBots, w => w.Name == "Oldcomp");
        Assert.Contains(world.Coordinator.WaitingBots, w => w.Name == "Secondseed");

        var tank35 = await world.AddBotAsync("Freetank", Human, Warrior, 35, [Taunt], far);
        var healer35 = await world.AddBotAsync("Freeheal", Human, Priest, 35, [LesserHeal, Smite], far + new System.Numerics.Vector3(5f, 0, 0));
        Assert.False(await world.RunUntilAsync(60_000, () => Noted(world, "is recruited") || world.Coordinator.Groups.Count > 0), await world.TraceAsync());
        Assert.Contains(world.Coordinator.WaitingBots, w => w.Name == "Oldcomp"); // the check is not vacuous: the companion still waits
        Assert.Contains(world.Coordinator.WaitingBots, w => w.Name == "Secondseed");
        Assert.Equal(0, world.Coordinator.Totals.Formed);
        Assert.DoesNotContain(world.Coordinator.Events, e => e.Contains("Freetank", StringComparison.Ordinal) || e.Contains("Freeheal", StringComparison.Ordinal));

        // Control: a free level-29 priest is within LevelRange of the anchor (27) and is recruited, so recruiting itself is live.
        var near = await world.AddBotAsync("Freenear", Human, Priest, 29, [LesserHeal, Smite], far + new System.Numerics.Vector3(10f, 0, 0));
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Count > 0), await world.TraceAsync());
        Assert.Contains(await EventsAsync(world), e => e.Contains("Freenear is recruited", StringComparison.Ordinal));
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Assert.NotNull(group.Find(second.Id));
        Assert.Null(group.Find(tank35.Id));
        Assert.Null(group.Find(healer35.Id));
    }

    // --- a plan the Match steps accept, but that holds no waiting anchor -----------------------------------------------------

    [Fact]
    public async Task APlanOfDerivedOnlyWaiters_FilledByAnInBandRecruit_IsNotFormed_WhenTheInBandAnchorIsLeftOutOfIt()
    {
        // Waiters: the anchor (a level-20 mage, in band 15-28, the YOUNGEST waiter) and five level-60 bots on a forming-group reason only (a warrior
        // and four mages, no healer). Fill takes the tank, the healer and then the rest oldest first, so with a free in-band priest recruited as the
        // healer the plan is the five level-60 bots plus the priest minus one, and the anchor is left out. The recruit is in band, so the plan passes
        // Match's needQualifying; only PlanAnchored (no waiting bot of the plan anchors) rejects it.
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], o =>
        {
            o.MoveSpeed = 0.1f; // bots crawl: the free recruit never walks to the quest giver
            NoOwnGuilds(o);
            o.Social.Lists.Enabled = false; // no companion reason: the derived waiters' lines end in exactly ": forminggroup"
            o.Groups.LevelRange = 50;
            o.Groups.Cooperation.RecruitWaitSeconds = 5;
            o.Groups.Cooperation.RecruitRadiusYards = 1500;
            o.Groups.Cooperation.RecruitBaseChance = 1f;
            o.Groups.Cooperation.RefusalMemoryMinutes = 1; // a refusal is rolled again every minute, so the recruit does come within the window
        }, dungeon: true);
        var far = EntranceArea + new System.Numerics.Vector3(0, -900f, 0);

        var first = await AddSeedAsync(world, "Firstseed", 20);
        var tank = await world.AddBotAsync("Dtank", Human, Warrior, 60, [Taunt], EntranceArea);
        var mages = new List<(Guid Id, string Name)>();
        for (int i = 0; i < 4; i++) mages.Add(await world.AddBotAsync("Dmage" + (char)('a' + i), Human, Mage, 60, [Fireball], EntranceArea));
        (Guid Id, string Name)[] derived = [tank, .. mages];
        Assert.True(await world.RunUntilAsync(120_000, () => derived.All(b => world.HasQuest(b.Id, DungeonQuest) && Noted(world, b.Name + " needs a group"))), await world.TraceAsync());

        var seed = await world.AddBotAsync("Younganchor", Human, Mage, 20, [Fireball], EntranceArea);
        await AwaitSeedWaitingAsync(world, seed);
        var stopped = await world.Bots.StopAsync(first.Name);
        Assert.True(stopped.Success, stopped.Code);
        await world.RunAsync(2 * PlayerbotGroupCoordinator.SeedRecheckMs);

        // Preconditions: the anchor and all five derived-only bots wait, the anchor younger than every one of them; nothing has formed (no healer yet).
        string[] events = await EventsAsync(world);
        foreach ((Guid Id, string Name) bot in derived)
        {
            Assert.Contains(world.Coordinator.WaitingBots, w => w.BotId == bot.Id);
            string need = Assert.Single(events, e => e.Contains(bot.Name + " needs a group", StringComparison.Ordinal));
            Assert.True(need.EndsWith(": forminggroup", StringComparison.Ordinal), need);
        }

        var anchorWaiting = Assert.Single(world.Coordinator.WaitingBots, w => w.BotId == seed.Id);
        Assert.All(world.Coordinator.WaitingBots.Where(w => derived.Any(d => d.Id == w.BotId)), w => Assert.True(unchecked((int)(anchorWaiting.SinceMs - w.SinceMs)) > 0, w.Name));
        Assert.Empty(world.Coordinator.Groups);

        // The filler: a free level-25 priest (in band), near the anchor (inside the recruit radius, far from the giver), holding no quest.
        var priest = await world.AddBotAsync("Freeheal", Human, Priest, 25, [LesserHeal, Smite], far);
        await world.OnWorldAsync(() =>
        {
            var low = world.Player(seed.Id);
            var filler = world.Player(priest.Id);
            Assert.InRange(filler.Level, 15, 28);
            Assert.True(System.Numerics.Vector3.Distance(new System.Numerics.Vector3(filler.X, filler.Y, filler.Z), new System.Numerics.Vector3(low.X, low.Y, low.Z)) <= 1500f);
            return true;
        });
        Assert.DoesNotContain(world.Coordinator.WaitingBots, w => w.BotId == priest.Id);

        // No group of the five level-60 bots and the recruit may form in the window, and the recruit is never taken.
        Assert.False(await world.RunUntilAsync(300_000, () => world.Coordinator.Groups.Count > 0 || Noted(world, "Freeheal is recruited")), await world.TraceAsync());
        Assert.Equal(0, world.Coordinator.Totals.Formed);
        Assert.Contains(world.Coordinator.WaitingBots, w => w.BotId == seed.Id);
        Assert.Contains(world.Coordinator.WaitingBots, w => w.BotId == tank.Id);
        Assert.DoesNotContain(world.Coordinator.WaitingBots, w => w.BotId == priest.Id);
    }

    // --- fit-lost finish ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task FormedInstanceGroup_WhoseOnlyQualifyingMemberLeavesWhileTravelling_EndsWithFitLost_BeforeEntering()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], NoOwnGuilds, dungeon: true);
        // Seed is level 27 mage (in band 15-28, qualifying).
        // Tank and healer are level 31 warrior and priest (out of band, non-qualifying guildmates).
        // The level-31 warrior is the tank and leader.
        var seed = await world.AddBotAsync("Fitseed", Human, Mage, 27, [Fireball], EntranceArea);
        await AwaitSeedWaitingAsync(world, seed);

        var tank = await world.AddBotAsync("Fittank", Human, Warrior, 31, [Taunt], EntranceArea);
        var healer = await world.AddBotAsync("Fitprie", Human, Priest, 31, [LesserHeal, Smite], EntranceArea);
        await MakeGuildAsync(world, "Fitguild", seed, tank, healer);

        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State == PlayerbotGroupState.Travelling)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Assert.Equal(tank.Id, group.LeaderBotId);
        Assert.Single(group.Members, m => m.Qualifying);
        Assert.Equal(seed.Id, group.Members.Single(m => m.Qualifying).BotId);

        // While the group is travelling, its only qualifying member leaves.
        var stopped = await world.Bots.StopAsync(seed.Name);
        Assert.True(stopped.Success, stopped.Code);

        // The group arrives at the entrance without any qualifying member left and ends with reason "fit-lost".
        Assert.True(await world.RunUntilAsync(60_000, () => world.Coordinator.Groups.Count == 0 || group.Reason == "fit-lost"), await world.TraceAsync());
        Assert.Equal("fit-lost", group.Reason);
        Assert.False(group.Succeeded);
        string[] events = await EventsAsync(world);
        Assert.Contains(events, e => e.Contains("gave up", StringComparison.Ordinal) && e.Contains("fit-lost", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FormedInstanceGroup_WhoseNonQualifyingMemberLeavesWhileTravelling_ContinuesAndDoesNotEndWithFitLost()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], NoOwnGuilds, dungeon: true);
        // Seed is level 27 mage (in band 15-28, qualifying).
        // Tank and healer are level 31 warrior and priest (out of band, non-qualifying guildmates).
        // The level-31 warrior is the tank and leader.
        var seed = await world.AddBotAsync("Contseed", Human, Mage, 27, [Fireball], EntranceArea);
        await AwaitSeedWaitingAsync(world, seed);

        var tank = await world.AddBotAsync("Conttank", Human, Warrior, 31, [Taunt], EntranceArea);
        var healer = await world.AddBotAsync("Contprie", Human, Priest, 31, [LesserHeal, Smite], EntranceArea);
        await MakeGuildAsync(world, "Contguild", seed, tank, healer);

        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State == PlayerbotGroupState.Travelling)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Assert.Equal(tank.Id, group.LeaderBotId);

        // A non-qualifying member (the healer) leaves while travelling.
        var stopped = await world.Bots.StopAsync(healer.Name);
        Assert.True(stopped.Success, stopped.Code);

        // The group still holds the qualifying seed, so it does not end with "fit-lost" and advances towards entering.
        Assert.True(await world.RunUntilAsync(60_000, () => group.State is PlayerbotGroupState.Entering or PlayerbotGroupState.Engaging || group.Reason is not null),
            await world.TraceAsync());
        Assert.NotEqual("fit-lost", group.Reason);
        string[] events = await EventsAsync(world);
        Assert.DoesNotContain(events, e => e.Contains("fit-lost", StringComparison.Ordinal));
    }
}

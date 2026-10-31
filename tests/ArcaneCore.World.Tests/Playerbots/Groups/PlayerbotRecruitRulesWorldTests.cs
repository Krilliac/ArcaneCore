using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;
using ArcaneCore.World.Bots;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// The recruiting rules: recruiting off, range and level, the group cap, and answering another bot's LFG. Split from the former <c>PlayerbotRecruitWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotRecruitWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotRecruitRulesWorldTests : PlayerbotRecruitWorldTestBase
{
    [Fact]
    public async Task WithRecruitingOff_TheGroupKeepsWaiting()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], options =>
        {
            Eager(options);
            options.Groups.Cooperation.Recruit = false;
        });
        await WaitingPairAsync(world);
        await world.AddBotAsync("Offheal", Human, Priest, 10, [LesserHeal, Smite], South);
        await world.RunAsync(60_000);
        Assert.DoesNotContain(world.Coordinator.Events, e => e.Contains("is recruited", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABotOutOfRange_OrOfAnotherLevel_IsNotRecruited()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], options =>
        {
            Eager(options);
            options.Groups.Cooperation.RecruitRadiusYards = 100;
        });
        await WaitingPairAsync(world);
        await world.AddBotAsync("Farheal", Human, Priest, 10, [LesserHeal, Smite], South); // 150 yards: outside the 100 yard radius
        await world.AddBotAsync("Lowheal", Human, Priest, 1, [LesserHeal], new Vector3(StartX, StartY - 60f, StartZ)); // level 1: outside the level range
        await world.RunAsync(60_000);
        Assert.DoesNotContain(world.Coordinator.Events, e => e.Contains("is recruited", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheGroupCap_IsLogged_WhenItHoldsWaitingBotsBack()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest], options =>
        {
            Still(options);
            options.Groups.MaxGroups = 1;
        });
        foreach (string name in new[] { "Capone", "Captwo", "Capthree", "Capfour" })
            await world.AddBotAsync(name, Human, Mage, 10, [Fireball]);

        Assert.True(await world.RunUntilAsync(180_000, () => world.Coordinator.Events.Any(e => e.Contains("the group cap (1)", StringComparison.Ordinal))), await world.TraceAsync());
        Assert.Single(world.Coordinator.Groups);
    }

    [Fact]
    public async Task ABotAnswersAnotherBotsLfg_OnlyWhenTheCoordinatorCanBuildTheGroupWithIt()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], options =>
        {
            Still(options);
            options.Groups.Cooperation.RecruitRadiusYards = 1500;
            options.Groups.Cooperation.RecruitBaseChance = 1f; // the wait stays at its default: only the answer can bring the healer in
        });
        var (tank, mage) = await WaitingPairAsync(world);
        var healer = await world.AddBotAsync("Lfgheal", Human, Priest, 10, [LesserHeal, Smite], South);
        var rival = await world.AddBotAsync("Lfgrival", Human, Priest, 10, [LesserHeal, Smite], South + new Vector3(5f, 0, 0));
        var extraMage = await world.AddBotAsync("Lfgmage", Human, Mage, 10, [Fireball], South + new Vector3(-5f, 0, 0));
        await world.OnWorldAsync(() =>
        {
            world.Bots.Social.Memory.Load([Relation(world, rival, tank, -80, PlayerbotRelationFlags.None)]);
            return true;
        });

        PlayerbotLfgOffer Offer(Guid hearer, Guid poster) => world.OnWorldAsync(() => world.Coordinator.OfferLfg(hearer, poster)).GetAwaiter().GetResult();
        Assert.Equal(PlayerbotLfgOfferOutcome.NotWaiting, Offer(tank.Id, tank.Id).Outcome); // its own call
        Assert.Equal(PlayerbotLfgOfferOutcome.NotWaiting, Offer(healer.Id, healer.Id).Outcome);
        Assert.Equal(PlayerbotLfgOfferOutcome.Declined, Offer(rival.Id, tank.Id).Outcome);      // a rival of the caller
        Assert.Equal(PlayerbotLfgOfferOutcome.Declined, Offer(extraMage.Id, tank.Id).Outcome);  // no healer: no group can be built with it
        Assert.False(world.Coordinator.IsPledged(extraMage.Id, (PlayerbotGroupGoalKind.Quest, OgreEntry)));

        PlayerbotLfgOffer pledged = Offer(healer.Id, tank.Id);
        Assert.Equal(PlayerbotLfgOfferOutcome.Pledged, pledged.Outcome);
        Assert.Equal(PlayerbotGroupRole.Healer, pledged.Role);
        Assert.True(world.Coordinator.IsPledged(healer.Id, (PlayerbotGroupGoalKind.Quest, OgreEntry)));
        Assert.Equal(PlayerbotLfgOfferOutcome.AlreadyPledged, Offer(healer.Id, tank.Id).Outcome);

        // The pledged healer joins at the next scan, long before the 20 second wait of a silent recruit.
        Assert.True(await world.RunUntilAsync(8_000, () => world.Coordinator.Groups.Count > 0), await world.TraceAsync());
        Assert.NotNull(world.Coordinator.Groups.Single().Find(healer.Id));
        Assert.False(world.Coordinator.IsPledged(healer.Id, (PlayerbotGroupGoalKind.Quest, OgreEntry)));
        Assert.Empty(world.Coordinator.TakeRecruitSays()); // a bot that answered the call already said so
    }
}

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
/// Which candidates are recruited: rivals, disliked and liked bots, the group id, and the refusal memory. Split from the former <c>PlayerbotRecruitWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotRecruitWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotRecruitCandidatesWorldTests : PlayerbotRecruitWorldTestBase
{
    [Fact]
    public async Task AWaitingGroupShortAHealer_RecruitsANearbyIdleHealerWithoutTheQuest_AndSharesTheQuestWithIt()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], Eager);
        var (tank, mage) = await WaitingPairAsync(world);
        var healer = await world.AddBotAsync("Recheal", Human, Priest, 10, [LesserHeal, Smite, Resurrection], South);

        bool lacked = false;
        Assert.True(await world.RunUntilAsync(120_000, () =>
        {
            if (world.Coordinator.Groups.Count == 0) return false;
            lacked = !world.HasQuest(healer.Id, EliteQuest);
            return true;
        }), await world.TraceAsync());

        Assert.True(lacked, "the healer had the quest already: it was no recruit" + Environment.NewLine + await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Assert.Equal(3, group.Goal.Size);
        Assert.Equal(PlayerbotGroupRole.Healer, group.Find(healer.Id)!.Role);
        Assert.Equal(tank.Id, group.LeaderBotId);
        Assert.Contains(world.Coordinator.Events, e => e.Contains("Recheal is recruited as healer", StringComparison.Ordinal));

        // The group's quest is shared with the recruit through the ordinary party packets once the group stands together.
        Assert.True(await world.RunUntilAsync(240_000, () => world.HasQuest(healer.Id, EliteQuest)), await world.TraceAsync());
        Assert.Contains(world.Coordinator.Events, e => e.Contains("shares quest", StringComparison.Ordinal) && e.Contains("Recheal", StringComparison.Ordinal));
    }

    /// <summary>
    /// A rival by the memory's own Rival flag and nothing else: the flag follows <c>RivalAffinity</c>, so the line is raised to -20 and the
    /// affinity is -30, above the refusal line (-40). With the rule deleted this candidate, the only healer around and the nearest bot, comes.
    /// </summary>
    [Theory]
    [InlineData(false)] // the candidate holds a member as a rival
    [InlineData(true)]  // a member holds the candidate as a rival
    public async Task ARivalByTheRivalFlagAlone_IsNeverRecruited_EvenAsTheOnlyAndNearestCandidate(bool memberHoldsCandidate)
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], options =>
        {
            Eager(options);
            options.Social.Memory.RivalAffinity = -20;
        });
        var (tank, mage) = await WaitingPairAsync(world);
        var rival = await world.AddBotAsync("Rivalpr", Human, Priest, 10, [LesserHeal, Smite], FarSouth);
        await world.OnWorldAsync(() =>
        {
            world.Bots.Social.Memory.Load([memberHoldsCandidate ? Relation(world, tank, rival, -30, PlayerbotRelationFlags.None) : Relation(world, rival, tank, -30, PlayerbotRelationFlags.None)]);
            return true;
        });
        Assert.True(await world.OnWorldAsync(() =>
        {
            PlayerbotSocialMemory memory = world.Bots.Social.Memory;
            (Guid holder, Guid other) = memberHoldsCandidate ? (tank.Id, rival.Id) : (rival.Id, tank.Id);
            return memory.TryGet(holder, world.Player(other).Guid.Value, out PlayerbotRelation relation) && memory.IsRival(relation)
                && memory.EffectiveAffinity(relation) > world.Options.Groups.Cooperation.RefuseAffinity;
        }), "the relation is a rival by the flag and above the refusal affinity");

        await world.RunAsync(90_000);
        Assert.False(world.Coordinator.Groups.Any(g => g.Find(rival.Id) is not null), await world.TraceAsync());
    }

    [Fact]
    public async Task ADislikedBotAtOrBelowTheRefusalAffinity_WithoutTheRivalFlag_IsNeverRecruited()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], Eager);
        var (tank, mage) = await WaitingPairAsync(world);
        var disliked = await world.AddBotAsync("Dislikepr", Human, Priest, 10, [LesserHeal, Smite], FarSouth);
        await world.OnWorldAsync(() =>
        {
            world.Bots.Social.Memory.Load([Relation(world, disliked, mage, -45, PlayerbotRelationFlags.None)]); // -45: below -40, above the default rival line -50
            PlayerbotSocialMemory memory = world.Bots.Social.Memory;
            Assert.True(memory.TryGet(disliked.Id, world.Player(mage.Id).Guid.Value, out PlayerbotRelation relation) && !memory.IsRival(relation));
            return true;
        });

        await world.RunAsync(90_000);
        Assert.False(world.Coordinator.Groups.Any(g => g.Find(disliked.Id) is not null), await world.TraceAsync());
    }

    [Fact]
    public async Task ALikedBot_IsRecruited_WhereARivalAndADislikedOneAreNot()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], Eager);
        var (tank, mage) = await WaitingPairAsync(world);
        var rival = await world.AddBotAsync("Rivalpr", Human, Priest, 10, [LesserHeal, Smite], South);
        var disliked = await world.AddBotAsync("Dislikepr", Human, Priest, 10, [LesserHeal, Smite], South + new Vector3(10f, 0, 0));
        var liked = await world.AddBotAsync("Likedpr", Human, Priest, 10, [LesserHeal, Smite], South + new Vector3(-10f, 0, 0));
        await world.OnWorldAsync(() =>
        {
            world.Bots.Social.Memory.Load(
            [
                Relation(world, rival, tank, -80, PlayerbotRelationFlags.None), // a rival is an affinity at or below the memory's rival line
                Relation(world, disliked, mage, -45, PlayerbotRelationFlags.None),
                Relation(world, liked, tank, 40, PlayerbotRelationFlags.None),
            ]);
            return true;
        });

        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Count > 0), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Assert.NotNull(group.Find(liked.Id));
        Assert.Null(group.Find(rival.Id));
        Assert.Null(group.Find(disliked.Id));
        await world.RunAsync(30_000);
        Assert.DoesNotContain(world.Coordinator.Groups, g => g.Find(rival.Id) is not null || g.Find(disliked.Id) is not null);
    }

    [Fact]
    public async Task AWaitingGroupsId_IsOneForItsMembers_AndStaysWhileItWaits()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], Still);
        var (tank, mage) = await WaitingPairAsync(world);
        (int tankId, int mageId) = await world.OnWorldAsync(() => (world.Coordinator.WaitingGroupIdOf(tank.Id), world.Coordinator.WaitingGroupIdOf(mage.Id)));
        Assert.NotEqual(0, tankId);
        Assert.Equal(tankId, mageId);
        await world.RunAsync(60_000);
        Assert.Equal(tankId, await world.OnWorldAsync(() => world.Coordinator.WaitingGroupIdOf(tank.Id)));
    }

    /// <summary>
    /// The refusal is decided once per refusal window (RefusalMemoryMinutes, phase-shifted per candidate and group): raising the chance afterwards does
    /// not ask the candidate again for the whole memory counted from the refusal; after it the candidate rolls afresh, on a seed that includes the window.
    /// </summary>
    [Fact]
    public async Task ACandidateThatRefusedAGroup_IsNotAskedAgainForTheFullMemory_AndRollsAfreshAfterIt()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], options =>
        {
            Still(options);
            options.Groups.Cooperation.RecruitWaitSeconds = 600; // no recruiting until the chance is set below
            options.Groups.Cooperation.RecruitRadiusYards = 1500;
            options.Groups.Cooperation.RefusalMemoryMinutes = 1;
            options.Groups.FormationTimeoutSeconds = 3_600;
            options.Chat.Voice.Enabled = false; // a bot's LFG answer (OfferLfg, offer:true) skips the refusal memory and adds a bonus; its post time is unseeded
        });
        var (tank, mage) = await WaitingPairAsync(world);

        // The pair waits for a group of three for the whole test, which runs for minutes of game time. Left alone they would solo-grind the
        // elite they wait on (separate behaviour), and the mage can die in that fight, leaving no second live waiting bot to recruit for. So the
        // fight cannot happen: the ogre is made unattackable (it stays on the map, so the pair keeps waiting where it is; despawned, the pair wanders off to explore and out of the healer's radius) and the combat dice are pinned to a fixed seed.
        await world.OnWorldAsync(() =>
        {
            Game.Maps.Map map = world.Player(tank.Id).Map!;
            map.Combat.Random = new ArcaneCore.World.Tests.Playerbots.Risk.RiskTestWorld.SeededDice(7);
            CreatureMapSystem creatures = map.FindUpdater<CreatureMapSystem>()!;
            foreach (Creature ogre in creatures.Creatures.Where(c => c.Entry == OgreEntry))
                ogre.UnitFlags |= UnitFlags.NotAttackable1 | UnitFlags.NonAttackable2 | UnitFlags.ImmuneToPlayer | UnitFlags.NotSelectable;
            return true;
        });

        var healer = await world.AddBotAsync("Refusepr", Human, Priest, 10, [LesserHeal, Smite], new Vector3(StartX, StartY - 1450f, StartZ)); // at the edge of the 1500 yard radius, hours of walking from the quest
        uint period = (uint)world.Options.Groups.Cooperation.RefusalMemoryMinutes * 60_000u;
        int gid = await world.OnWorldAsync(() => world.Coordinator.WaitingGroupIdOf(tank.Id));
        ulong healerGuid = await world.OnWorldAsync(() => world.Player(healer.Id).Guid.Value);
        uint phase = PlayerbotRecruitment.RefusalPhase(period, healerGuid, gid);
        uint WindowOf(uint ms) => PlayerbotRecruitment.RefusalWindow(ms, period, healerGuid, gid);
        uint UntilNextWindow(uint ms) => period - (unchecked(ms + phase) % period);

        // Wait for a window whose roll refuses (above the chance, below 0.9) and whose next window's roll comes (below 0.9): the rolls are fixed per
        // (candidate, group, window), so the proof needs such a pair; the waiting pair holds its group meanwhile (no recruiting yet).
        await world.RunAsync(await world.OnWorldAsync(() => UntilNextWindow((uint)world.World.Host.World.NowMs) + 1_000));
        for (int tries = 0; tries < 5; tries++)
        {
            (double now, double later, uint w) = await world.OnWorldAsync(() =>
            {
                ulong guid = world.Player(healer.Id).Guid.Value;
                uint current = WindowOf((uint)world.World.Host.World.NowMs);
                double other = (0.4 * PlayerbotTraits.For(guid).Sociability) + 0.2;
                double r0 = PlayerbotRecruitment.Roll(guid, PlayerbotRecruitment.RollGroup(gid, current));
                double r1 = PlayerbotRecruitment.Roll(guid, PlayerbotRecruitment.RollGroup(gid, current + 1));
                return (r0 >= other + 0.02 && r0 <= 0.9 ? r0 : -1, r1, current);
            });
            if (now >= 0 && later < 0.9) break;
            Assert.True(tries < 4, "no window with a usable pair of rolls");
            uint left = await world.OnWorldAsync(() => UntilNextWindow((uint)world.World.Host.World.NowMs));
            await world.RunAsync(left + 1_000);
        }

        // Pick a base chance that puts this healer's own roll for this group in this window just above its chance: it refuses.
        (double roll, uint window) = await world.OnWorldAsync(() =>
        {
            Player player = world.Player(healer.Id);
            uint w = WindowOf((uint)world.World.Host.World.NowMs);
            double other = (0.4 * PlayerbotTraits.For(player.Guid.Value).Sociability) + 0.2; // sociability weight and the missing-role bonus
            double r = PlayerbotRecruitment.Roll(player.Guid.Value, PlayerbotRecruitment.RollGroup(gid, w));
            Assert.InRange(r, other + 0.02, 0.9); // feasible: refuses at (r - other - 0.01), accepts at the 0.95 clamp
            world.Options.Groups.Cooperation.RecruitBaseChance = (float)(r - other - 0.01);
            world.Options.Groups.Cooperation.RecruitWaitSeconds = 1;
            return (r, w);
        });
        await world.RunAsync(25_000);
        Assert.Equal(window, await world.OnWorldAsync(() => WindowOf((uint)world.World.Host.World.NowMs))); // still the same window
        Assert.False(world.Coordinator.Groups.Any(g => g.Find(healer.Id) is not null), $"roll {roll}: " + await world.TraceAsync());

        await world.OnWorldAsync(() =>
        {
            world.Options.Groups.Cooperation.RecruitBaseChance = 1f; // everybody would come now: but this one refused this group in this window
            return true;
        });
        await world.RunAsync(20_000);
        Assert.Equal(window, await world.OnWorldAsync(() => WindowOf((uint)world.World.Host.World.NowMs)));
        Assert.False(world.Coordinator.Groups.Any(g => g.Find(healer.Id) is not null), await world.TraceAsync());

        // The refusal holds for the whole memory counted from the refusal, not to the end of a fixed clock slice; then a new roll (it must be
        // one that passes at the 0.95 clamp for the proof to mean something).
        double next = await world.OnWorldAsync(() => PlayerbotRecruitment.Roll(world.Player(healer.Id).Guid.Value, PlayerbotRecruitment.RollGroup(gid, window + 1)));
        Assert.InRange(next, 0.0, 0.9);
        uint refusedAt = await world.OnWorldAsync(() => world.Coordinator.RefusedAtMs(healer.Id, gid));
        // the time of the refusal, not a window number
        Assert.True(refusedAt >= 10_000u && refusedAt <= (uint)world.World.Host.World.NowMs, $"refused at {refusedAt} ms, now {world.World.Host.World.NowMs} ms, roll {roll} in window {window}" + Environment.NewLine + await world.TraceAsync());
        long untilExpiry = await world.OnWorldAsync(() => (long)refusedAt + period - 6_000 - world.World.Host.World.NowMs);
        Assert.True(untilExpiry > 0, $"the refusal memory ran out before the check ({untilExpiry} ms left)");
        await world.RunAsync((uint)untilExpiry);
        Assert.True(await world.OnWorldAsync(() => world.Player(mage.Id).IsAlive), "the mage died while waiting: only one live waiting bot is left, so no group can form" + Environment.NewLine + await world.TraceAsync());
        Assert.True(await world.OnWorldAsync(() => !world.Player(tank.Id).Combat.IsInCombat && !world.Player(mage.Id).Combat.IsInCombat),
            "a waiting bot is in combat: the pair was meant to wait undisturbed" + Environment.NewLine + await world.TraceAsync());
        Assert.False(world.Coordinator.Groups.Any(g => g.Find(healer.Id) is not null), "asked again before the refusal memory ran out: " + await world.TraceAsync());
        bool walkedIn = false; // the healer must be recruited, not arrive at the quest and wait for a group itself
        Assert.True(await world.RunUntilAsync(30_000, () =>
        {
            walkedIn |= world.Coordinator.WaitingBots.Any(w => w.BotId == healer.Id);
            return world.Coordinator.Groups.Any(g => g.Find(healer.Id) is not null);
        }), $"next roll {next}: " + await world.TraceAsync());
        Assert.False(walkedIn, "the healer walked to the quest and waited by itself: it was not recruited");
    }

    [Fact]
    public async Task TheAnswer_IsTheRuleOfTheCandidateAndTheGroup_NothingElse()
    {
        // Default chance: whether the healer comes is exactly the pure rule's answer for (healer, first group), whichever way it falls.
        // The refusal window is keyed on the candidate's guid, so it is the same on every run. The coordinator asks once, a few seconds after the
        // healer appears (RecruitWaitSeconds 5): the expected answer is computed at the time of the decision itself, the refusal's own time
        // (RefusedAtMs) or the moment the group stands, so a window boundary between "now" and the ask cannot change it. Which branch is taken
        // depends on the guid; the decline branch is covered whatever the guid by ACandidateThatRefusedAGroup_... (it forces a refusal).
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], options =>
        {
            Still(options);
            options.Groups.Cooperation.RecruitWaitSeconds = 5;
            options.Chat.Voice.Enabled = false; // a stray LFG answer skips the refusal rule (PlanWithRecruits offer:true)
        });
        var (tank, _) = await WaitingPairAsync(world);
        var healer = await world.AddBotAsync("Detheal", Human, Priest, 10, [LesserHeal, Smite], South);
        int groupId = await world.OnWorldAsync(() => world.Coordinator.WaitingGroupIdOf(tank.Id)); // the waiting group's own id, whatever other groups there are
        Assert.NotEqual(0, groupId);
        uint decidedAt = 0;
        bool formed = false;
        Assert.True(await world.RunUntilAsync(90_000, () =>
        {
            uint refusedAt = world.Coordinator.RefusedAtMs(healer.Id, groupId);
            if (refusedAt != 0) { decidedAt = refusedAt; return true; }
            if (world.Coordinator.Groups.Count == 0) return false;
            formed = true;
            decidedAt = (uint)world.World.Host.World.NowMs;
            return true;
        }), "the healer was neither recruited nor refused" + Environment.NewLine + await world.TraceAsync());

        bool expected = await world.OnWorldAsync(() =>
        {
            Player player = world.Player(healer.Id);
            var facts = new PlayerbotRecruitFacts(PlayerbotTraits.For(player.Guid.Value).Sociability, 0, 0, false, FillsRole: true, false, false);
            uint period = (uint)world.Options.Groups.Cooperation.RefusalMemoryMinutes * 60_000u;
            return PlayerbotRecruitment.Accepts(world.Options.Groups.Cooperation, player.Guid.Value,
                PlayerbotRecruitment.RollGroup(groupId, PlayerbotRecruitment.RefusalWindow(decidedAt, period, player.Guid.Value, groupId)), facts);
        });
        Assert.Equal(expected, formed);
        if (formed) Assert.NotNull(world.Coordinator.Groups.Single().Find(healer.Id));
    }
}

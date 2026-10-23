using System.Numerics;
using ArcaneCore.Game;
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
/// Recruiting, bots answering bots' calls and guild calls (docs/areas/playbots-groups.md, Cooperation), end to end on the real world
/// handlers and the manual clock: a waiting group short of a healer asks a free bot nearby that lacks the quest, and the group's own
/// invitations, acceptances and quest share do the rest.
/// </summary>
public sealed class PlayerbotRecruitWorldTests
{
    private const byte Human = 1, Warrior = 1, Priest = 5, Mage = 8;
    private const uint LesserHeal = 2050, Fireball = 133, Smite = 585;

    private static readonly Vector3 South = new(StartX, StartY - 150f, StartZ);

    /// <summary>Too far from the quest giver for a bot to walk there and take the quest by itself within the test (so only a recruit can bring it into the group).</summary>
    private static readonly Vector3 FarSouth = new(StartX, StartY - 900f, StartZ);

    /// <summary>Bots crawl (they stay where the test puts them, and a bot far from the giver never takes the quest) and found no guilds of their own.</summary>
    private static void Still(PlayerbotOptions options)
    {
        options.MoveSpeed = 0.1f;
        options.Social.Guilds.Enabled = false;
    }

    private static void Eager(PlayerbotOptions options)
    {
        Still(options);
        options.Groups.Cooperation.RecruitWaitSeconds = 5;
        options.Groups.Cooperation.RecruitRadiusYards = 1500; // a waiting bot wanders off quickly; the radius has its own tests
        options.Groups.Cooperation.RecruitBaseChance = 1f;
    }

    /// <summary>A warrior and a mage that took the elite quest and wait for a third (and a healer).</summary>
    private static async Task<((Guid Id, string Name) Tank, (Guid Id, string Name) Mage)> WaitingPairAsync(GroupTestWorld world)
    {
        var tank = await world.AddBotAsync("Rectank", Human, Warrior, 10, [Taunt]);
        var mage = await world.AddBotAsync("Recmage", Human, Mage, 10, [Fireball]);
        Assert.True(await world.RunUntilAsync(60_000, () => world.Coordinator.WaitingBots.Count() == 2), await world.TraceAsync());
        return (tank, mage);
    }

    private static PlayerbotRelation Relation(GroupTestWorld world, (Guid Id, string Name) bot, (Guid Id, string Name) other, short affinity, PlayerbotRelationFlags flags)
    {
        Player self = world.Player(bot.Id), them = world.Player(other.Id);
        return new PlayerbotRelation(bot.Id, (int)self.Guid.Low, them.Guid.Value, true, affinity, 6, 500, flags, world.Bots.Social.Memory.NowMs, "", "");
    }

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

    /// <summary>The refusal is decided once for the group: raising the chance afterwards (or any other change of the facts) does not ask the candidate again.</summary>
    [Fact]
    public async Task ACandidateThatRefusedAGroup_IsNotAskedAgain_WhileTheGroupWaits()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], options =>
        {
            Still(options);
            options.Groups.Cooperation.RecruitWaitSeconds = 600; // no recruiting until the chance is set below
            options.Groups.Cooperation.RecruitRadiusYards = 1500;
        });
        var (tank, _) = await WaitingPairAsync(world);
        var healer = await world.AddBotAsync("Refusepr", Human, Priest, 10, [LesserHeal, Smite], FarSouth);

        // Pick a base chance that puts this healer's own roll for this group just above its chance: it refuses.
        double roll = await world.OnWorldAsync(() =>
        {
            Player player = world.Player(healer.Id);
            double other = (0.4 * PlayerbotTraits.For(player.Guid.Value).Sociability) + 0.2; // sociability weight and the missing-role bonus
            double r = PlayerbotRecruitment.Roll(player.Guid.Value, world.Coordinator.WaitingGroupIdOf(tank.Id));
            Assert.InRange(r, other + 0.02, 0.9); // feasible: refuses at (r - other - 0.01), accepts at the 0.95 clamp
            world.Options.Groups.Cooperation.RecruitBaseChance = (float)(r - other - 0.01);
            world.Options.Groups.Cooperation.RecruitWaitSeconds = 1;
            return r;
        });
        await world.RunAsync(40_000);
        Assert.False(world.Coordinator.Groups.Any(g => g.Find(healer.Id) is not null), $"roll {roll}: " + await world.TraceAsync());

        await world.OnWorldAsync(() =>
        {
            world.Options.Groups.Cooperation.RecruitBaseChance = 1f; // everybody would come now: but this one refused this group
            return true;
        });
        await world.RunAsync(60_000);
        Assert.False(world.Coordinator.Groups.Any(g => g.Find(healer.Id) is not null), await world.TraceAsync());
    }

    [Fact]
    public async Task TheAnswer_IsTheRuleOfTheCandidateAndTheGroup_NothingElse()
    {
        // Default chance: whether the healer comes is exactly the pure rule's answer for (healer, first group), whichever way it falls.
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], options =>
        {
            Still(options);
            options.Groups.Cooperation.RecruitWaitSeconds = 5;
        });
        var (tank, _) = await WaitingPairAsync(world);
        var healer = await world.AddBotAsync("Detheal", Human, Priest, 10, [LesserHeal, Smite], South);
        bool expected = await world.OnWorldAsync(() =>
        {
            Player player = world.Player(healer.Id);
            int groupId = world.Coordinator.WaitingGroupIdOf(tank.Id); // the waiting group's own id, whatever other groups there are
            Assert.NotEqual(0, groupId);
            var facts = new PlayerbotRecruitFacts(PlayerbotTraits.For(player.Guid.Value).Sociability, 0, 0, false, FillsRole: true, false, false);
            return PlayerbotRecruitment.Accepts(world.Options.Groups.Cooperation, player.Guid.Value, groupId, facts);
        });

        bool formed = await world.RunUntilAsync(90_000, () => world.Coordinator.Groups.Count > 0);
        Assert.Equal(expected, formed);
        if (formed) Assert.NotNull(world.Coordinator.Groups.Single().Find(healer.Id));
    }

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

    /// <summary>One LFG line heard by many free bots: the line's data is built once, and no more than MaxLfgAnswersPerLine of the hearers get a place.</summary>
    [Fact]
    public async Task OneLfgLineHeardByManyBots_IsBuiltOnce_AndAnsweredByAtMostTheCap()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], options =>
        {
            Still(options);
            options.Groups.Cooperation.RecruitRadiusYards = 1500;
            options.Groups.Cooperation.RecruitBaseChance = 1f;
            options.MaxBots = 40;
            options.MaxRegisteredBots = 60;
            options.Groups.Cooperation.MaxLfgAnswersPerLine = 4;
        });
        var (tank, _) = await WaitingPairAsync(world);
        var hearers = new List<(Guid Id, string Name)>();
        for (int i = 0; i < 24; i++)
            hearers.Add(await world.AddBotAsync("Hearer" + (char)('a' + (i / 5)) + (char)('a' + (i % 5)), Human, Priest, 10, [LesserHeal, Smite], South + new Vector3(i % 5, i / 5, 0)));

        (int builds, int pledged) = await world.OnWorldAsync(() =>
        {
            int before = world.Coordinator.LfgLineBuilds, answers = 0;
            foreach ((Guid id, string _) in hearers) // one line, one tick
                if (world.Coordinator.OfferLfg(id, tank.Id).Outcome == PlayerbotLfgOfferOutcome.Pledged) answers++;
            return (world.Coordinator.LfgLineBuilds - before, answers);
        });
        Assert.Equal(1, builds);
        Assert.Equal(4, pledged);
    }

    /// <summary>A hearer that cannot answer (busy, or out of the group's level range) is turned away without a single allocation.</summary>
    [Fact]
    public async Task ARejectedLfgHearer_CostsNoAllocation()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], options =>
        {
            Still(options);
            options.Groups.Cooperation.RecruitRadiusYards = 1500;
            options.Groups.Cooperation.RecruitBaseChance = 1f;
        });
        var (tank, mage) = await WaitingPairAsync(world);
        var high = await world.AddBotAsync("Hearerhigh", Human, Priest, 30, [LesserHeal, Smite], South);

        long bytes = await world.OnWorldAsync(() =>
        {
            Assert.Equal(PlayerbotLfgOfferOutcome.Declined, world.Coordinator.OfferLfg(mage.Id, tank.Id).Outcome); // warm up: it waits itself
            Assert.Equal(PlayerbotLfgOfferOutcome.Declined, world.Coordinator.OfferLfg(high.Id, tank.Id).Outcome); // builds the line's data once
            long start = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                world.Coordinator.OfferLfg(mage.Id, tank.Id);  // a waiting bot: not free
                world.Coordinator.OfferLfg(high.Id, tank.Id);  // 20 levels above the group's anchor
            }

            return GC.GetAllocatedBytesForCurrentThread() - start;
        });
        Assert.True(bytes < 1024, $"2000 rejected hearers allocated {bytes} bytes");
    }

    [Fact]
    public async Task AnswerBotCallsOff_NoBotOffers()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], options =>
        {
            Still(options);
            options.Groups.Cooperation.RecruitBaseChance = 1f;
            options.Groups.Cooperation.AnswerBotCalls = false;
        });
        var (tank, _) = await WaitingPairAsync(world);
        var healer = await world.AddBotAsync("Nooffer", Human, Priest, 10, [LesserHeal, Smite], South);
        Assert.Equal(PlayerbotLfgOfferOutcome.NotWaiting, (await world.OnWorldAsync(() => world.Coordinator.OfferLfg(healer.Id, tank.Id))).Outcome);
    }

    [Fact]
    public async Task ABotHearingAnotherBotsLfmLine_OffersAndAnswersInTheChannel_OnlyWithARealMatch()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], options =>
        {
            Still(options);
            options.Chat.Enabled = true;
            options.Groups.Cooperation.RecruitBaseChance = 1f;
        });
        var (tank, mage) = await WaitingPairAsync(world);
        var healer = await world.AddBotAsync("Voiceheal", Human, Priest, 10, [LesserHeal, Smite], South);
        var extraMage = await world.AddBotAsync("Voicemage", Human, Mage, 10, [Fireball], South + new Vector3(-5f, 0, 0));
        PlayerbotSocialFeature feature = world.World.Services.GetRequiredService<PlayerbotSocialFeature>();
        PlayerbotChat chat = world.Bots.Chat!;
        // Wait for the voice to hook the chat service (its first pass).
        Assert.True(await world.RunUntilAsync(10_000, () => chat.Responder is not null), await world.TraceAsync());

        long Heard(Guid hearer, Guid poster) => world.OnWorldAsync(() =>
        {
            Player hearing = world.Player(hearer), speaking = world.Player(poster);
            chat.Voice.Post(poster, new PlayerbotVoicePost(PlayerbotVoicePostKind.LookingForGroup, chat.NowMs, "the ogre", MinLevel: 1));
            long before = feature.Voice.Asked;
            bool answeredByVoice = chat.Hear(new BotHeardLine(hearer, hearing.Guid, hearing.Name, ChatType.Channel, "LookingForGroup", speaking.Guid, speaking.Name,
                SenderIsBot: true, "LFM the ogre need heals", AddressesBot: false, FirstHearing: true));
            Assert.False(answeredByVoice); // a bot's line is never swallowed: the party AI sees it as before
            return feature.Voice.Asked - before;
        }).GetAwaiter().GetResult();

        Assert.Equal(0, Heard(extraMage.Id, tank.Id));   // the group needs a healer: no match, no reply
        Assert.False(world.Coordinator.IsPledged(extraMage.Id, (PlayerbotGroupGoalKind.Quest, OgreEntry)));
        Assert.Equal(1, Heard(healer.Id, tank.Id));      // a match: pledged and answered
        Assert.True(world.Coordinator.IsPledged(healer.Id, (PlayerbotGroupGoalKind.Quest, OgreEntry)));
        Assert.Equal(0, Heard(healer.Id, tank.Id));      // answered once
    }

    [Fact]
    public async Task AGuildCall_ForADungeonGroup_BringsAGuildmateWithoutTheWait_AndOnlyThen()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], options =>
        {
            Still(options);
            options.Chat.Enabled = true;
            options.Groups.Cooperation.RecruitBaseChance = 1f;
        }, dungeon: true);
        var tank = await world.AddBotAsync("Gtank", Human, Warrior, 12, [Taunt], EntranceArea);
        var mage = await world.AddBotAsync("Gmage", Human, Mage, 12, [Fireball], EntranceArea);
        Assert.True(await world.RunUntilAsync(60_000, () => world.Coordinator.WaitingBots.Count() == 2), await world.TraceAsync());
        Vector3 away = EntranceArea + new Vector3(0, 120f, 0);
        var mate = await world.AddBotAsync("Gmate", Human, Priest, 12, [LesserHeal, Smite], away);
        var stranger = await world.AddBotAsync("Gstranger", Human, Priest, 12, [LesserHeal, Smite], away + new Vector3(5f, 0, 0));
        Guild guild = await world.OnWorldAsync(() =>
        {
            GuildManager guilds = world.World.Services.GetRequiredService<SocialFeature>().Context.Guilds;
            Assert.Equal(GuildAdminResult.Ok, guilds.Create(world.Player(tank.Id).Guid.Low, "Callers", out Guild? created));
            Assert.Equal(GuildAdminResult.Ok, guilds.AdminInvite(world.Player(mage.Id).Guid.Low, "Callers"));
            Assert.Equal(GuildAdminResult.Ok, guilds.AdminInvite(world.Player(mate.Id).Guid.Low, "Callers"));
            return created!;
        });

        // The coordinator has a forming dungeon group: the caller is the bot that waited longest, and its guild may be asked.
        var calls = await world.OnWorldAsync(() => world.Coordinator.FormingCalls());
        PlayerbotFormingCall call = Assert.Single(calls);
        Assert.Contains(call.CallerBotId, new[] { tank.Id, mage.Id });
        Assert.Equal(2, call.Have);
        Assert.Equal(PlayerbotGroupGoalKind.Dungeon, call.Goal.Kind);

        // The caller's guild has the "anyone for the dungeon?" topic queued (it is said once a real player can read guild chat).
        PlayerbotSocialFeature feature = world.World.Services.GetRequiredService<PlayerbotSocialFeature>();
        await world.RunAsync(5_000);
        Assert.Equal(1, await world.OnWorldAsync(() => feature.GuildChat.Waiting(guild.Id, "guild.lfm")));
        Assert.Empty(await world.OnWorldAsync(() => world.Coordinator.Groups));

        // Nobody comes before the call (the 20 second wait has not run out); once the guild is called, the guildmate does and the stranger does not.
        await world.OnWorldAsync(() => { world.Coordinator.NoteGuildCall(guild.Id, call.Goal.Key); return true; });
        Assert.True(await world.OnWorldAsync(() => world.Coordinator.HasGuildCall(guild.Id, call.Goal.Key)));
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Count > 0), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Assert.NotNull(group.Find(mate.Id));
        Assert.Null(group.Find(stranger.Id));
    }

    [Fact]
    public async Task AnOpenWorldQuestGroup_IsNoGuildCall()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], Still);
        await WaitingPairAsync(world);
        Assert.Empty(await world.OnWorldAsync(() => world.Coordinator.FormingCalls()));
    }
}

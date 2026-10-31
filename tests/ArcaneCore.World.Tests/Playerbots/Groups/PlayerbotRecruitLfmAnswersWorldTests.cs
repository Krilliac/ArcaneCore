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
/// Rejected LFG hearers, answering switched off, LFM offers, and two LFG posts over several ticks. Split from the former <c>PlayerbotRecruitWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotRecruitWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotRecruitLfmAnswersWorldTests : PlayerbotRecruitWorldTestBase
{
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

    /// <summary>
    /// Many free bots hear two bots' LFG posts through the chat service, over several ticks (their thinks are staggered as in production):
    /// each post is built once, and at most MaxLfgAnswersPerLine hearers pledge and reply for each post, whatever tick they hear it in.
    /// </summary>
    [Fact]
    public async Task TwoLfgPosts_HeardByManyBotsOverSeveralTicks_AreBuiltOnceAndAnsweredWithinTheCapEach()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], options =>
        {
            Still(options);
            options.MaxBots = 60;
            options.MaxRegisteredBots = 80;
            options.Chat.Enabled = true;
            options.Groups.Cooperation.RecruitBaseChance = 1f;
            options.Groups.Cooperation.RecruitRadiusYards = 1500;
            options.Groups.Cooperation.MaxLfgAnswersPerLine = 4;
        });
        var (tank, mage) = await WaitingPairAsync(world);
        var healers = new List<(Guid Id, string Name)>();
        for (int i = 0; i < 32; i++)
            healers.Add(await world.AddBotAsync("Lfghear" + (char)('a' + (i / 8)) + (char)('a' + (i % 8)), Human, Priest, 10, [LesserHeal, Smite], South + new Vector3(i % 8, i / 8, 0)));
        PlayerbotChat chat = world.Bots.Chat!;
        Assert.True(await world.RunUntilAsync(10_000, () => chat.Responder is not null), await world.TraceAsync());
        long buildsBefore = await world.OnWorldAsync(() => (long)world.Coordinator.LfgLineBuilds);

        // Each post is said once (the voice board holds it); then every hearer hears it, a few per tick.
        long postedMs = await world.OnWorldAsync(() =>
        {
            long at = chat.NowMs;
            foreach (var poster in new[] { tank, mage })
                chat.Voice.Post(poster.Id, new PlayerbotVoicePost(PlayerbotVoicePostKind.LookingForGroup, at, "the ogre", MinLevel: 1));
            return at;
        });
        void Hear(Guid hearer, (Guid Id, string Name) poster) => world.OnWorldAsync(() =>
        {
            Player hearing = world.Player(hearer), speaking = world.Player(poster.Id);
            chat.Hear(new BotHeardLine(hearer, hearing.Guid, hearing.Name, ChatType.Channel, "LookingForGroup", speaking.Guid, speaking.Name,
                SenderIsBot: true, "LFM the ogre need heals", AddressesBot: false, FirstHearing: true));
            return true;
        }).GetAwaiter().GetResult();

        for (int batch = 0; batch < 4; batch++)
        {
            foreach (var hearer in healers.Skip(batch * 8).Take(8))
            {
                Hear(hearer.Id, tank);
                Hear(hearer.Id, mage); // the second poster is interleaved with the first
            }

            await world.RunAsync(100); // the next hearers think in a later tick
        }

        // The places held (pledges, not replies): each post holds between one and the cap, and the pledged hearers are exactly those places.
        (int fromTank, int fromMage, int pledgedHealers) = await world.OnWorldAsync(() =>
        {
            var goalKey = world.Coordinator.WaitingBots.Single(w => w.BotId == tank.Id).Goal.Key;
            return (world.Coordinator.LfgAnswersOf(tank.Id, postedMs), world.Coordinator.LfgAnswersOf(mage.Id, postedMs), healers.Count(h => world.Coordinator.IsPledged(h.Id, goalKey)));
        });
        Assert.InRange(fromTank, 1, 4);
        Assert.InRange(fromMage, 1, 4);
        Assert.Equal(fromTank + fromMage, pledgedHealers);
        Assert.Equal(2, await world.OnWorldAsync(() => (long)world.Coordinator.LfgLineBuilds) - buildsBefore); // once per post
    }
}

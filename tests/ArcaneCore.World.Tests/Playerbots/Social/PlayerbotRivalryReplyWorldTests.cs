using ArcaneCore.Game;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using Xunit;
using Emotes = ArcaneCore.World.Playerbots.BotSocial.PlayerbotSocialEmotes.TextEmotes;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// The stance-aware answer to an emote aimed at a bot (P6a, design 4.2, 7.1) through the real party AI hook: the bot hears an
/// SMSG_TEXT_EMOTE naming it, and what it sends back is what <see cref="PlayerbotRivalryRules.ReplyTo"/> says for its stance, the emote, the
/// target kind and the hash roll of (bot, emoter, emote, bucket of the party AI's clock, which is world uptime and so bucket 0 in a test).
/// The expected answer of every bot is computed with the pure rules, so a hook that ignores the stance, the roll or the layer fails; the
/// non-vacuity of each test (bots that really answered) is asserted.
/// </summary>
public sealed class PlayerbotRivalryReplyWorldTests
{
    private static readonly string[] Suffixes = ["a", "b", "c", "d", "e", "f", "g", "h"];

    /// <summary>The roll the hook is specified to use for a reply heard at party AI clock 0 (uptime inside the first half hour).</summary>
    private static double Roll(ulong bot, ulong emoter, uint emote) => PlayerbotRivalryRules.ReplyRoll(bot, emoter, emote, 0);

    /// <summary><paramref name="count"/> Human bots and one Undead player within a few yards of each other; the bots' names and guids, the player's guid and client.</summary>
    private static async Task<(string[] Bots, ulong[] BotGuids, ulong FoeGuid, WorldTestClient Foe)> CrowdAsync(SocialWorld world, string prefix, int count)
    {
        string[] bots = [.. Suffixes.Take(count).Select(s => prefix + s)];
        foreach (string bot in bots) await world.AddBotAsync(bot);
        WorldTestClient foe = await world.AddPlayerAsync(prefix + "foe", RivalryKit.Undead);
        await HelpTestKit.StandAsync(world, bots[0], [.. bots.Skip(1).Select((b, i) => (b, 2f + i)), (prefix + "foe", 1f)]);
        ulong[] guids = new ulong[bots.Length];
        for (int i = 0; i < bots.Length; i++) guids[i] = (await RivalryKit.GuidAsync(world, bots[i])).Value;
        return (bots, guids, (await RivalryKit.GuidAsync(world, prefix + "foe")).Value, foe);
    }

    private static async Task<Dictionary<ulong, List<uint>>> WaveAtAsync(SocialWorld world, WorldTestClient foe, string[] bots, ulong[] botGuids, ulong foeGuid,
        uint emote, uint?[] predicted)
    {
        ObjectGuid foeObject = ObjectGuid.Player((uint)(foeGuid & 0xFFFFFFFF));
        foreach (string bot in bots) await RivalryKit.HearAsync(world, bot, foeObject, emote);
        return await RivalryKit.RepliesAsync(foe, botGuids, predicted.Count(p => p is not null));
    }

    private static void AssertAnswers(string[] bots, ulong[] botGuids, Dictionary<ulong, List<uint>> actual, uint?[] predicted)
    {
        for (int i = 0; i < bots.Length; i++)
        {
            uint[] expected = predicted[i] is { } answer ? [answer] : [];
            Assert.True(expected.SequenceEqual(actual[botGuids[i]]),
                $"{bots[i]} answered [{string.Join(",", actual[botGuids[i]])}], the rules say [{string.Join(",", expected)}]");
        }
    }

    // --- rival, friend, disabled --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARivalPlayersWave_GetsWhatTheRulesSay_RudeOrNothing_AndItsRudeEmoteGetsARudeOne()
    {
        await using SocialWorld world = await RivalryKit.StartAsync(chat: true);
        (string[] bots, ulong[] guids, ulong foeGuid, WorldTestClient foe) = await CrowdAsync(world, "Rvrep", 8);
        await using WorldTestClient keep = foe;
        foreach (string bot in bots) await RivalryKit.SeedAsync(world, bot, "Rvrepfoe", killerIsBot: false);

        uint?[] wave = [.. guids.Select(g => PlayerbotRivalryRules.ReplyTo(PlayerbotStance.Rival, Emotes.Wave, toBot: false, Roll(g, foeGuid, Emotes.Wave)))];
        Assert.DoesNotContain(wave, a => a is { } e && PlayerbotEmoteMoods.Classify(e) == PlayerbotEmoteMood.Friendly); // the rule: never a friendly answer
        AssertAnswers(bots, guids, await WaveAtAsync(world, foe, bots, guids, foeGuid, Emotes.Wave, wave), wave);

        // Non-vacuity: the same bots, the same player, a rude gesture: every one of them answers (and rudely), so the bots do hear and answer in this world.
        uint?[] rude = [.. guids.Select(g => PlayerbotRivalryRules.ReplyTo(PlayerbotStance.Rival, Emotes.Rude, toBot: false, Roll(g, foeGuid, Emotes.Rude)))];
        Assert.All(rude, a => Assert.True(a is { } e && PlayerbotEmoteMoods.Classify(e) == PlayerbotEmoteMood.Rude));
        AssertAnswers(bots, guids, await WaveAtAsync(world, foe, bots, guids, foeGuid, Emotes.Rude, rude), rude);
    }

    [Fact]
    public async Task ARudeReplyToARealPlayer_BecomesAShrug_InACapital_OrWithoutTheOptInTheWorldAsks()
    {
        foreach (bool capital in new[] { true, false })
        {
            string prefix = capital ? "Rvqca" : "Rvqop";
            await using SocialWorld world = await RivalryKit.StartAsync(o => o.Social.Rivalry.Emotes.RudeRequiresOptIn = !capital, chat: true);
            (string[] bots, ulong[] guids, ulong foeGuid, WorldTestClient foe) = await CrowdAsync(world, prefix, 4);
            await using WorldTestClient keep = foe;
            foreach (string bot in bots) await RivalryKit.SeedAsync(world, bot, prefix + "foe", killerIsBot: false);
            // Every character stands in an ordinary zone, except (first world) the player in a capital.
            await world.Host.OnWorldAsync(() =>
            {
                foreach (ArcaneCore.Game.Entities.Player p in world.Host.World.OnlinePlayers) p.ZoneId = RivalryKit.OrdinaryZone;
                if (capital) RivalryKit.Online(world, prefix + "foe").ZoneId = RivalryKit.CapitalZone;
                return true;
            });

            // The rules alone would answer a rival's rude emote rudely, so a shrug is the guard at work.
            Assert.All(guids, g => Assert.True(RivalryKit.IsRude(PlayerbotRivalryRules.ReplyTo(PlayerbotStance.Rival, Emotes.Rude, toBot: false, Roll(g, foeGuid, Emotes.Rude)) ?? 0)));
            uint?[] shrugs = [.. guids.Select(_ => (uint?)Emotes.Shrug)];
            AssertAnswers(bots, guids, await WaveAtAsync(world, foe, bots, guids, foeGuid, Emotes.Rude, shrugs), shrugs);
        }
    }

    [Fact]
    public async Task AFriendsWave_IsAnsweredWithAFriendlyEmote_ByEveryBot()
    {
        await using SocialWorld world = await RivalryKit.StartAsync(chat: true);
        (string[] bots, ulong[] guids, ulong foeGuid, WorldTestClient foe) = await CrowdAsync(world, "Rvfri", 4);
        await using WorldTestClient keep = foe;
        foreach (string bot in bots) await RivalryKit.LoadAsync(world, bot, foeGuid, affinity: 40);

        uint?[] expected = [.. guids.Select(g => PlayerbotRivalryRules.ReplyTo(PlayerbotStance.Friendly, Emotes.Wave, toBot: false, Roll(g, foeGuid, Emotes.Wave)))];
        Assert.All(expected, a => Assert.NotNull(a));

        Dictionary<ulong, List<uint>> actual = await WaveAtAsync(world, foe, bots, guids, foeGuid, Emotes.Wave, expected);

        AssertAnswers(bots, guids, actual, expected);
        Assert.All(actual.Values.SelectMany(list => list), e => Assert.Contains(e, new[] { Emotes.Wave, Emotes.Hello, Emotes.Smile }));
    }

    [Fact]
    public async Task WithRivalryDisabled_ARivalsWave_GetsTodaysFriendlyAnswer_FromEveryBot()
    {
        await using SocialWorld world = await RivalryKit.StartAsync(o => o.Social.Rivalry.Enabled = false, chat: true);
        (string[] bots, ulong[] guids, ulong foeGuid, WorldTestClient foe) = await CrowdAsync(world, "Rvoff", 4);
        await using WorldTestClient keep = foe;
        foreach (string bot in bots) await RivalryKit.SeedAsync(world, bot, "Rvofffoe", killerIsBot: false);
        ObjectGuid foeObject = ObjectGuid.Player((uint)(foeGuid & 0xFFFFFFFF));
        foreach (string bot in bots) await RivalryKit.HearAsync(world, bot, foeObject, Emotes.Wave);

        Dictionary<ulong, List<uint>> actual = await RivalryKit.RepliesAsync(foe, guids, expected: bots.Length);

        // Today's ReplyTo always answers a wave with a wave, a hello or a smile, whatever the bot thinks of the waver.
        Assert.All(guids, g => Assert.Single(actual[g]));
        Assert.All(actual.Values.SelectMany(list => list), e => Assert.Contains(e, new[] { Emotes.Wave, Emotes.Hello, Emotes.Smile }));
    }

    // --- War Mode: replies to real players read the normal layer (7.1) -------------------------------------------------------------------------------

    [Fact]
    public async Task UnderWarMode_AReplyToARealPlayer_IsTheNormalLayersStances_NotTheWarLayers()
    {
        await using SocialWorld world = await RivalryKit.StartAsync(chat: true);
        (string[] bots, ulong[] guids, ulong foeGuid, WorldTestClient foe) = await CrowdAsync(world, "Rvwrp", 8);
        await using WorldTestClient keep = foe;
        world.Options.Social.WarMode.Enabled = true;
        foreach (string bot in bots) await RivalryKit.SeedAsync(world, bot, "Rvwrpfoe", killerIsBot: false, kills: 1); // normal -20, war -60: Disliked and a war rival
        PlayerbotRelation seeded = RivalryKit.RelationOf(world, world.Bots[bots[0]], foeGuid)!;
        Assert.True(RivalryKit.Memory(world).IsRival(seeded));
        Assert.False(seeded.Flags.HasFlag(PlayerbotRelationFlags.Rival));

        uint?[] normal = [.. guids.Select(g => PlayerbotRivalryRules.ReplyTo(PlayerbotStance.Disliked, Emotes.Wave, toBot: false, Roll(g, foeGuid, Emotes.Wave)))];
        uint?[] war = [.. guids.Select(g => PlayerbotRivalryRules.ReplyTo(PlayerbotStance.Rival, Emotes.Wave, toBot: false, Roll(g, foeGuid, Emotes.Wave)))];
        Assert.True(normal.Zip(war).Any(pair => pair.First != pair.Second), "no bot would tell the layers apart: add bots"); // the test must be able to see a leak
        Assert.Contains(normal, a => a is not null);

        Dictionary<ulong, List<uint>> actual = await WaveAtAsync(world, foe, bots, guids, foeGuid, Emotes.Wave, normal);

        AssertAnswers(bots, guids, actual, normal);
    }

    // --- opt-out ------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnOptedOutPlayer_GetsNoAnswer_ToAnEmote_UntilItOptsBackIn()
    {
        await using SocialWorld world = await RivalryKit.StartAsync(chat: true);
        (string[] bots, ulong[] guids, ulong foeGuid, WorldTestClient foe) = await CrowdAsync(world, "Rvopt", 3);
        await using WorldTestClient keep = foe;
        int account = await world.Host.PlayerStateAsync("Rvoptfoe", p => p.AccountId);
        ObjectGuid foeObject = ObjectGuid.Player((uint)(foeGuid & 0xFFFFFFFF));
        PlayerbotSocialRivalryOptions options = world.Options.Social.Rivalry;
        RivalryKit.OptOut(world).Handle(foeGuid, account, "rivals off", -100_000, options);

        foreach (string bot in bots) await RivalryKit.HearAsync(world, bot, foeObject, Emotes.Wave);
        Dictionary<ulong, List<uint>> silent = await RivalryKit.RepliesAsync(foe, guids, expected: 1, seconds: 3);
        Assert.All(silent.Values, list => Assert.Empty(list));

        // The control, in the same world: opted back in, the same wave is answered (a neutral stranger answers as today).
        RivalryKit.OptOut(world).Handle(foeGuid, account, "rivals on", -50_000, options);
        foreach (string bot in bots) await RivalryKit.HearAsync(world, bot, foeObject, Emotes.Wave);
        Dictionary<ulong, List<uint>> answered = await RivalryKit.RepliesAsync(foe, guids, expected: bots.Length);
        Assert.All(guids, g => Assert.Single(answered[g]));
    }

    // --- the asymmetric bot pair (review item 2) -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAsymmetricBotPair_NeverExchangesARudeEmote_AndNeitherAffinityEverFalls_WhileRudeAtBotsIsOff()
    {
        await using SocialWorld world = await RivalryKit.StartAsync(chat: true);
        Assert.False(world.Options.Social.Rivalry.Emotes.RudeAtBots);
        Guid a = await world.AddBotAsync("Rvasyma");
        string[] others = ["Rvasymba", "Rvasymbb", "Rvasymbc", "Rvasymbd", "Rvasymbe", "Rvasymbf"];
        var botIds = new Dictionary<string, Guid>();
        foreach (string name in others) botIds[name] = await world.AddBotAsync(name, race: RivalryKit.Undead);
        await using WorldTestClient watcher = await world.AddPlayerAsync("Rvasymwatch");
        await HelpTestKit.StandAsync(world, "Rvasyma", [.. others.Select((n, i) => (n, 2f + i)), ("Rvasymwatch", 1f)]);
        ulong aGuid = (await RivalryKit.GuidAsync(world, "Rvasyma")).Value;
        var guids = new Dictionary<string, ulong>();
        foreach (string name in others) guids[name] = (await RivalryKit.GuidAsync(world, name)).Value;

        // A holds every B as a stranger (no memory); every B holds A as a rival in the normal layer.
        foreach (string name in others) await RivalryKit.SeedAsync(world, name, "Rvasyma", killerIsBot: true);
        var starts = new Dictionary<(Guid, ulong), int>();
        foreach (string name in others)
        {
            starts[(botIds[name], aGuid)] = RivalryKit.RelationOf(world, botIds[name], aGuid)!.Affinity;
            Assert.True(RivalryKit.Memory(world).IsRival(RivalryKit.RelationOf(world, botIds[name], aGuid)!));
            Assert.Null(RivalryKit.RelationOf(world, a, guids[name]));
        }

        // Rounds at times chosen from the real rolls and the real reply rules: only one B's greeting roll passes at that time, and the rules answer
        // the greeting A will pick (from the pick roll) with something rather than nothing, so that the reply side is seen too.
        var heard = new List<(ulong Emoter, uint Emote, string Target)>();
        long after = 0;
        var greeted = new HashSet<string>();
        var answered = new HashSet<string>();
        for (int round = 0; round < 3; round++)
        {
            (string b, long t, uint reply) = FindRound(aGuid, guids, after);
            after = t;
            await RivalryKit.RunAsync(world, t);
            Assert.Contains(RivalryKit.Lane(world).Sends, s => s.Bot == a && s.Target == guids[b] && s.TargetIsBot && !s.Rude);
            greeted.Add(b);

            List<(ulong Emoter, uint Emote, string Target)> now = await RivalryKit.HearUntilAsync(watcher, all => RivalryKit.BySpeaker(all, [guids[b]], "Rvasyma")[guids[b]].Count >= 1, seconds: 10);
            heard.AddRange(now);
            if (RivalryKit.BySpeaker(now, [guids[b]], "Rvasyma")[guids[b]].Contains(reply)) answered.Add(b);

            // Sampled after every step: nobody lost affinity toward anybody.
            foreach (string name in others)
            {
                Assert.True(RivalryKit.RelationOf(world, botIds[name], aGuid)!.Affinity >= starts[(botIds[name], aGuid)], $"{name} thinks less of A after round {round}");
                short toward = RivalryKit.RelationOf(world, a, guids[name]) is { } r ? r.Affinity : (short)0;
                Assert.True(toward >= 0, $"A thinks less of {name} after round {round}");
            }
        }

        Assert.DoesNotContain(RivalryKit.Lane(world).Sends, s => s.Rude);
        Assert.DoesNotContain(heard, e => RivalryKit.IsRude(e.Emote)); // nothing either side sent classifies Rude
        Assert.NotEmpty(greeted);                                       // non-vacuity: A's greetings did go out to bots
        Assert.NotEmpty(answered);                                      // and the Bs did answer them, with the emote the rules give
        Assert.Contains(greeted, n => RivalryKit.RelationOf(world, botIds[n], aGuid)!.Affinity > starts[(botIds[n], aGuid)]); // the greeting reached a B's memory
    }

    /// <summary>
    /// A time after <paramref name="after"/> and a B such that A's greeting roll to that B alone passes at that time and the rules answer the greeting A will pick
    /// (from the pick roll) with something rather than nothing. Returns the B's name, the time and the expected answer.
    /// </summary>
    private static (string B, long Time, uint Reply) FindRound(ulong aGuid, Dictionary<string, ulong> bs, long after)
    {
        for (long bucket = after / RivalryKit.Bucket + 1; bucket < 100_000; bucket++)
        {
            long t = bucket * RivalryKit.Bucket + 60_000;
            foreach ((string name, ulong guid) in bs)
            {
                if (!RivalryKit.GreetPasses(aGuid, guid, t) || bs.Any(other => other.Value != guid && RivalryKit.GreetPasses(aGuid, other.Value, t))) continue;
                uint greeting = PlayerbotRivalryRules.PickGreeting(PlayerbotStance.Neutral, PlayerbotRivalryRules.PickRoll(aGuid, guid, t));
                uint? reply = PlayerbotRivalryRules.ReplyTo(PlayerbotStance.Rival, greeting, toBot: true, Roll(guid, aGuid, greeting));
                if (reply is { } answer) return (name, t, answer);
            }
        }

        throw new InvalidOperationException("no round gives a greeting and an answer");
    }
}

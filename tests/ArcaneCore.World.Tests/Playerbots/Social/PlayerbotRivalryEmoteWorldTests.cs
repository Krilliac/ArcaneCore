using System.Reflection;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using Xunit;
using Emotes = ArcaneCore.World.Playerbots.BotSocial.PlayerbotSocialEmotes.TextEmotes;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// The cross-faction emote lane (<c>rivalry</c>, P6a, design 4.1 and 4.2) in a real world: greetings to strangers and friends, rude emotes at
/// rivals, and every rule that keeps them polite: the hash roll and its fall-through, the per-pair / per-target / per-bot / per-minute limits,
/// the per-target rude cap, the opt-out, the ignore list, combat, capitals and unknown zones (fail closed, with positive controls), the
/// bot-to-bot visibility rule, the range clamp, the master switches and the War Mode layer rules (7.1).
/// <para>
/// The lane is driven by hand at a chosen world time (<see cref="RivalryKit.RunAsync(SocialWorld, long, uint, Action)"/>) in a frozen world; the
/// tests read what it sent from <c>PlayerbotRivalryLane.Sends</c> (a snapshot of <c>PlayerbotRivalrySend(Bot, Target, TargetIsBot, Emote, Rude)</c>,
/// the last few hundred sends) and, in the first ones, from the real player's client.
/// </para>
/// </summary>
public sealed class PlayerbotRivalryEmoteWorldTests
{
    private static IReadOnlyList<PlayerbotRivalrySend> Sends(SocialWorld world) => RivalryKit.Lane(world).Sends;

    private static async Task<(Guid Bot, ulong BotGuid, WorldTestClient Player, ulong PlayerGuid)> PairAsync(SocialWorld world, string bot, string player, float dx = 3f)
    {
        Guid id = await world.AddBotAsync(bot);
        WorldTestClient client = await world.AddPlayerAsync(player, RivalryKit.Undead);
        await HelpTestKit.StandAsync(world, bot, (player, dx));
        return (id, (await RivalryKit.GuidAsync(world, bot)).Value, client, (await RivalryKit.GuidAsync(world, player)).Value);
    }

    // --- greetings --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AHumanBotGreetsAnUndeadStranger_OnlyWhenItsRollPasses_AndTheClientIsSentTheEmoteNamingIt()
    {
        await using SocialWorld world = await RivalryKit.StartAsync();
        (Guid bot, ulong botGuid, WorldTestClient player, ulong playerGuid) = await PairAsync(world, "Rvgreeter", "Rvundeada");
        await using WorldTestClient keep = player;

        // A time at which the stranger roll fails, then one at which it passes: the same bot, the same player, only the hash differs.
        long failing = RivalryKit.TimeWhere(t => !RivalryKit.GreetPasses(botGuid, playerGuid, t));
        long passing = RivalryKit.TimeWhere(t => RivalryKit.GreetPasses(botGuid, playerGuid, t), afterMs: failing);

        await RivalryKit.RunAsync(world, failing);
        Assert.Empty(Sends(world));
        Assert.Equal(0, world.Log.Count("Rvgreeter", WorldOpcode.CmsgTextEmote));

        await RivalryKit.RunAsync(world, passing);
        PlayerbotRivalrySend send = Assert.Single(Sends(world));
        Assert.Equal(bot, send.Bot);
        Assert.Equal(playerGuid, send.Target);
        Assert.False(send.TargetIsBot);
        Assert.False(send.Rude);
        Assert.Contains(send.Emote, RivalryKit.NeutralGreetings);
        Assert.Equal(1, world.Log.Count("Rvgreeter", WorldOpcode.CmsgTextEmote)); // a direct client packet, as the same-faction wave is

        Heard heard = await RivalryKit.ListenAsync(player);
        Assert.Contains(heard.Emotes, e => e.Emoter == botGuid && e.Emote == send.Emote && e.Target == "Rvundeada");
    }

    [Fact]
    public async Task AFriendIsGreetedAtOnce_WithAFriendlyEmote_WhateverTheRoll()
    {
        await using SocialWorld world = await RivalryKit.StartAsync();
        (Guid bot, _, WorldTestClient player, ulong playerGuid) = await PairAsync(world, "Rvfriendly", "Rvundeadb");
        await using WorldTestClient keep = player;
        await RivalryKit.LoadAsync(world, "Rvfriendly", playerGuid, affinity: 40);
        Assert.Equal(40, RivalryKit.RelationOf(world, bot, playerGuid)!.Affinity); // the seed is what the test says it is

        await RivalryKit.RunAsync(world, 3_600_000);

        PlayerbotRivalrySend send = Assert.Single(Sends(world));
        Assert.Equal(playerGuid, send.Target);
        Assert.False(send.Rude);
        Assert.Contains(send.Emote, RivalryKit.FriendlyGreetings);
    }

    [Fact]
    public async Task ASameFactionPlayer_IsNotGreetedByThisLane()
    {
        await using SocialWorld world = await RivalryKit.StartAsync();
        await world.AddBotAsync("Rvsamebot");
        await using WorldTestClient human = await world.AddPlayerAsync("Rvsameplayer");
        await HelpTestKit.StandAsync(world, "Rvsamebot", ("Rvsameplayer", 3f));

        await RivalryKit.RunAsync(world, 3_600_000);

        Assert.Empty(Sends(world)); // the P1 wave (lane "emotes") is theirs; the cross-faction lane never touches the bot's own faction
    }

    // --- rude emotes and the disclosure ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARivalPlayer_GetsARudeEmote_AndOneDisclosureForTheLogin_NotOneForTheNextTaunt()
    {
        await using SocialWorld world = await RivalryKit.StartAsync();
        (Guid bot, ulong botGuid, WorldTestClient player, ulong playerGuid) = await PairAsync(world, "Rvtaunter", "Rvtaunted");
        await using WorldTestClient keep = player;
        await RivalryKit.SeedAsync(world, "Rvtaunter", "Rvtaunted", killerIsBot: false);
        Assert.True(RivalryKit.RelationOf(world, bot, playerGuid)!.Flags.HasFlag(PlayerbotRelationFlags.Rival), "the seed makes a rival");

        await RivalryKit.RunAsync(world, 3_600_000);

        PlayerbotRivalrySend taunt = Assert.Single(Sends(world));
        Assert.True(taunt.Rude);
        Assert.False(taunt.TargetIsBot);
        Assert.Equal(playerGuid, taunt.Target);
        Assert.Contains(taunt.Emote, RivalryKit.RudeEmotes);
        Heard first = await RivalryKit.ListenAsync(player);
        Assert.Contains(first.Emotes, e => e.Emoter == botGuid && e.Emote == taunt.Emote && e.Target == "Rvtaunted");
        Assert.Equal([RivalryKit.DisclosureText], first.SystemLines);

        // Thirty-one minutes on every rude gap has passed: a second taunt, and no second disclosure.
        await RivalryKit.RunAsync(world, 3_600_000 + 31 * 60_000);
        Assert.Equal(2, Sends(world).Count);
        Heard second = await RivalryKit.ListenAsync(player);
        Assert.Contains(second.Emotes, e => e.Emoter == botGuid && RivalryKit.IsRude(e.Emote));
        Assert.Empty(second.SystemLines);
    }

    [Fact]
    public async Task ASecondBotRivalOfTheSamePlayer_SendsNothingInsideRudePerTargetMinutes_AndTheTargetIsTauntedAgainAfterIt()
    {
        await using SocialWorld world = await RivalryKit.StartAsync();
        (_, _, WorldTestClient player, ulong playerGuid) = await PairAsync(world, "Rvrivalone", "Rvsharedfoe");
        await using WorldTestClient keep = player;
        await world.AddBotAsync("Rvrivaltwo");
        await HelpTestKit.StandAsync(world, "Rvrivalone", ("Rvrivaltwo", 2f));
        await RivalryKit.SeedAsync(world, "Rvrivalone", "Rvsharedfoe", killerIsBot: false);
        await RivalryKit.SeedAsync(world, "Rvrivaltwo", "Rvsharedfoe", killerIsBot: false);
        long t0 = 3_600_000;

        // Both bots hold the player as a rival and are free; together they send one rude emote, not two.
        await RivalryKit.RunAsync(world, t0);
        Assert.Single(Sends(world));

        // Eleven minutes on, the first bot is held by its pair gap (30 min), the second is free of every gap but the target's (30 min): still one.
        await RivalryKit.RunAsync(world, t0 + 11 * 60_000);
        Assert.Single(Sends(world));

        // Thirty-one minutes on the cap is over: one more, never two in the same pass.
        await RivalryKit.RunAsync(world, t0 + 31 * 60_000);
        Assert.Equal(2, Sends(world).Count);
        Assert.All(Sends(world), s => Assert.Equal(playerGuid, s.Target));
    }

    [Fact]
    public async Task TheRudeCap_IsPerTarget_SoADifferentRivalPlayerIsTauntedInTheSamePass()
    {
        await using SocialWorld world = await RivalryKit.StartAsync();
        (_, _, WorldTestClient one, ulong oneGuid) = await PairAsync(world, "Rvcapbot", "Rvcapone");
        await using WorldTestClient keep = one;
        await using WorldTestClient two = await world.AddPlayerAsync("Rvcaptwo", RivalryKit.Undead);
        await world.AddBotAsync("Rvcapbottwo");
        await HelpTestKit.StandAsync(world, "Rvcapbot", ("Rvcaptwo", 2f), ("Rvcapbottwo", 4f));
        ulong twoGuid = (await RivalryKit.GuidAsync(world, "Rvcaptwo")).Value;
        await RivalryKit.SeedAsync(world, "Rvcapbot", "Rvcapone", killerIsBot: false);
        await RivalryKit.SeedAsync(world, "Rvcapbottwo", "Rvcaptwo", killerIsBot: false);

        await RivalryKit.RunAsync(world, 3_600_000);

        // Bot one taunts its rival and bot two its own: two targets, so the per-target cap does not bind.
        Assert.Contains(Sends(world), s => s.Target == oneGuid && s.Rude);
        Assert.Contains(Sends(world), s => s.Target == twoGuid && s.Rude);
    }

    [Fact]
    public async Task AnOptedOutRival_GetsNothing_UntilItWhispersRivalsOn()
    {
        await using SocialWorld world = await RivalryKit.StartAsync();
        (_, _, WorldTestClient player, ulong playerGuid) = await PairAsync(world, "Rvoptbot", "Rvoptplayer");
        await using WorldTestClient keep = player;
        await RivalryKit.SeedAsync(world, "Rvoptbot", "Rvoptplayer", killerIsBot: false);
        int account = await world.Host.PlayerStateAsync("Rvoptplayer", p => p.AccountId);
        PlayerbotSocialRivalryOptions options = world.Options.Social.Rivalry;

        Assert.True(RivalryKit.OptOut(world).Handle(playerGuid, account, "rivals off", 0, options).Consumed);
        await RivalryKit.RunAsync(world, 3_600_000);
        Assert.Empty(Sends(world));

        // The same world, the same rival, the opt-out lifted: the taunt comes (so the silence above was the opt-out, not a lane that never ran).
        Assert.True(RivalryKit.OptOut(world).Handle(playerGuid, account, "rivals on", 60_000, options).Consumed);
        await RivalryKit.RunAsync(world, 3_700_000);
        Assert.Single(Sends(world));
    }

    [Fact]
    public async Task ARecordedOptOut_IsHonoured_EvenAfterAllowOptOutIsSwitchedOff()
    {
        await using SocialWorld world = await RivalryKit.StartAsync();
        (_, _, WorldTestClient player, ulong playerGuid) = await PairAsync(world, "Rvhonbot", "Rvhonplayer");
        await using WorldTestClient keep = player;
        await RivalryKit.SeedAsync(world, "Rvhonbot", "Rvhonplayer", killerIsBot: false);
        int account = await world.Host.PlayerStateAsync("Rvhonplayer", p => p.AccountId);
        RivalryKit.OptOut(world).Handle(playerGuid, account, "rivals off", 0, world.Options.Social.Rivalry);
        world.Options.Social.Rivalry.AllowOptOut = false;

        await RivalryKit.RunAsync(world, 3_600_000);

        Assert.Empty(Sends(world));
    }

    [Fact]
    public async Task APlayerWhoIgnoresTheBot_GetsNothing_UntilItStopsIgnoring()
    {
        await using SocialWorld world = await RivalryKit.StartAsync();
        (_, ulong botGuid, WorldTestClient player, _) = await PairAsync(world, "Rvignored", "Rvignorer");
        await using WorldTestClient keep = player;
        await RivalryKit.SeedAsync(world, "Rvignored", "Rvignorer", killerIsBot: false);
        uint botLow = (uint)(botGuid & 0xFFFFFFFF);
        await world.Host.OnWorldAsync(() => SocialTestKit.Social(world.Host).Context.Friends.Get(RivalryKit.Online(world, "Rvignorer")).Set(botLow, SocialFlags.Ignored, true));

        await RivalryKit.RunAsync(world, 3_600_000);
        Assert.Empty(Sends(world));

        await world.Host.OnWorldAsync(() => SocialTestKit.Social(world.Host).Context.Friends.Get(RivalryKit.Online(world, "Rvignorer")).Set(botLow, SocialFlags.Ignored, false));
        await RivalryKit.RunAsync(world, 3_700_000);
        Assert.Single(Sends(world));
    }

    [Fact]
    public async Task APlayerInCombat_IsNotTaunted_WhileAnotherRivalOutOfCombatIs()
    {
        await using SocialWorld world = await RivalryKit.StartAsync();
        (_, _, WorldTestClient busy, ulong busyGuid) = await PairAsync(world, "Rvcombatbot", "Rvbusyfoe");
        await using WorldTestClient keep = busy;
        await using WorldTestClient idle = await world.AddPlayerAsync("Rvidlefoe", RivalryKit.Undead);
        await HelpTestKit.StandAsync(world, "Rvcombatbot", ("Rvidlefoe", 2f));
        ulong idleGuid = (await RivalryKit.GuidAsync(world, "Rvidlefoe")).Value;
        await RivalryKit.SeedAsync(world, "Rvcombatbot", "Rvbusyfoe", killerIsBot: false);
        await RivalryKit.SeedAsync(world, "Rvcombatbot", "Rvidlefoe", killerIsBot: false);
        await RivalryKit.RunAsync(world, 3_600_000, beforeRun: () =>
        {
            HelpTestKit.Engage(HelpTestKit.SpawnMob(RivalryKit.Online(world, "Rvbusyfoe"), 4f), RivalryKit.Online(world, "Rvbusyfoe"));
            Assert.True(RivalryKit.Online(world, "Rvbusyfoe").Combat.IsInCombat);
        });

        Assert.Equal([idleGuid], Sends(world).Select(s => s.Target).ToArray()); // the lane ran, and only the idle rival was its target
        Assert.DoesNotContain(Sends(world), s => s.Target == busyGuid);
    }

    [Fact]
    public async Task ABotInCombat_SendsNothing()
    {
        await using SocialWorld world = await RivalryKit.StartAsync();
        (_, _, WorldTestClient player, _) = await PairAsync(world, "Rvfighter", "Rvwatched");
        await using WorldTestClient keep = player;
        await RivalryKit.SeedAsync(world, "Rvfighter", "Rvwatched", killerIsBot: false);
        await RivalryKit.RunAsync(world, 3_600_000, beforeRun: () =>
        {
            HelpTestKit.Engage(HelpTestKit.SpawnMob(RivalryKit.Online(world, "Rvfighter"), 4f), RivalryKit.Online(world, "Rvfighter"));
            Assert.True(RivalryKit.Online(world, "Rvfighter").Combat.IsInCombat);
        });

        Assert.Empty(Sends(world));
    }

    // --- capitals, safe places, unknown zones (design 4.2 and 14) -----------------------------------------------------------------------

    [Fact]
    public async Task NoRudeEmoteIsSent_WhenEitherStandsInACapital_OrTheZoneIsZeroOrUnknown_ButAnOrdinaryZoneIsTaunted()
    {
        await using SocialWorld world = await RivalryKit.StartAsync();
        (_, _, WorldTestClient player, _) = await PairAsync(world, "Rvzonebot", "Rvzonefoe");
        await using WorldTestClient keep = player;
        await RivalryKit.SeedAsync(world, "Rvzonebot", "Rvzonefoe", killerIsBot: false);
        long t = 3_600_000;

        // Nothing is sent in any of these, so no limiter is spent and each run is as good as the first.
        await RivalryKit.RunInZonesAsync(world, t += 60_000, ("Rvzonebot", RivalryKit.CapitalZone), ("Rvzonefoe", RivalryKit.OrdinaryZone));
        Assert.Empty(Sends(world)); // the bot stands in a capital
        await RivalryKit.RunInZonesAsync(world, t += 60_000, ("Rvzonebot", RivalryKit.OrdinaryZone), ("Rvzonefoe", RivalryKit.CapitalZone));
        Assert.Empty(Sends(world)); // the target does
        await RivalryKit.RunAsync(world, t += 60_000, zone: 0);
        Assert.Empty(Sends(world)); // zone 0: unknown, fail closed
        await RivalryKit.RunAsync(world, t += 60_000, zone: 4242);
        Assert.Empty(Sends(world)); // a zone the table does not hold

        // The positive control: the very same scenario in an ordinary zone is taunted, so the checks above did run.
        await RivalryKit.RunAsync(world, t += 60_000, zone: RivalryKit.OrdinaryZone);
        Assert.Single(Sends(world));
    }

    [Fact]
    public async Task NoRudeEmoteIsSent_WhenEitherStandsInASafeArea_ButTheSameScenarioWithoutItIsTaunted()
    {
        // The test world has no terrain, so the area under every character is 0 (Map.GetZoneAndAreaId: "(0, 0) when unknown"). Listing area 0 puts every
        // character in a safe area of an ordinary zone, which reaches the lane's area branch (the zone itself is ordinary and not quiet).
        await using SocialWorld world = await RivalryKit.StartAsync();
        (_, _, WorldTestClient player, _) = await PairAsync(world, "Rvareabot", "Rvareafoe");
        await using WorldTestClient keep = player;
        await RivalryKit.SeedAsync(world, "Rvareabot", "Rvareafoe", killerIsBot: false);
        long t = 3_600_000;

        await world.Host.OnWorldAsync(() =>
        {
            Player bot = RivalryKit.Online(world, "Rvareabot");
            Assert.Equal((0u, 0u), bot.Map!.GetZoneAndAreaId(bot.X, bot.Y, bot.Z)); // the premise
            return true;
        });
        world.Options.Social.Rivalry.Safeguards.SafeAreas = "0";
        await RivalryKit.RunAsync(world, t += 60_000);
        Assert.Empty(Sends(world));

        // The positive control: the same run with the default list (area 0 is not in it) is taunted, so the area branch above is what held it back.
        world.Options.Social.Rivalry.Safeguards.SafeAreas = PlayerbotSocialRivalrySafeguardOptions.DefaultSafeAreas;
        await RivalryKit.RunAsync(world, t += 60_000);
        Assert.Single(Sends(world));
    }

    [Fact]
    public async Task TheRoundRobin_MovesOnByTheBotsLookedAt_NeverMoreThanBotsPerPass()
    {
        await using SocialWorld world = await RivalryKit.StartAsync(o => o.MaxBots = 40);
        const int bots = PlayerbotRivalryLane.BotsPerPass + 2;
        for (int i = 0; i < bots; i++) await world.AddBotAsync($"Rvrobin{(char)('a' + i)}");
        long t = 3_600_000;

        Assert.Equal(0, RivalryKit.Lane(world).Cursor);
        await RivalryKit.RunAsync(world, t += 60_000);
        Assert.Equal(PlayerbotRivalryLane.BotsPerPass, RivalryKit.Lane(world).Cursor); // 26 running bots, 24 looked at
        await RivalryKit.RunAsync(world, t += 60_000);
        Assert.Equal((2 * PlayerbotRivalryLane.BotsPerPass) % bots, RivalryKit.Lane(world).Cursor); // wrapped: the bots the first pass skipped come next
    }

    [Fact]
    public async Task AStranger_IsStillGreeted_InACapital_AndWhenTheZoneIsUnknown()
    {
        await using SocialWorld world = await RivalryKit.StartAsync();
        (_, ulong botGuid, WorldTestClient player, ulong playerGuid) = await PairAsync(world, "Rvquietbot", "Rvquietone");
        await using WorldTestClient keep = player;
        long capital = RivalryKit.TimeWhere(t => RivalryKit.GreetPasses(botGuid, playerGuid, t));
        long unknown = RivalryKit.TimeWhere(t => RivalryKit.GreetPasses(botGuid, playerGuid, t), afterMs: capital + RivalryKit.Bucket);

        await RivalryKit.RunAsync(world, capital, zone: RivalryKit.CapitalZone);
        Assert.Single(Sends(world));
        await RivalryKit.RunAsync(world, unknown, zone: 0);
        Assert.Equal(2, Sends(world).Count);
        Assert.All(Sends(world), s => Assert.False(s.Rude));
    }

    // --- bot-to-bot -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task BotsGreetEachOtherAcrossFactions_OnlyWithARealPlayerInRange_AndOnlyWithBotToBotOn()
    {
        await using SocialWorld world = await RivalryKit.StartAsync();
        Guid human = await world.AddBotAsync("Rvbota");
        Guid undead = await world.AddBotAsync("Rvbotc", race: RivalryKit.Undead);
        await HelpTestKit.StandAsync(world, "Rvbota", ("Rvbotc", 3f));
        ulong humanGuid = (await RivalryKit.GuidAsync(world, "Rvbota")).Value;
        ulong undeadGuid = (await RivalryKit.GuidAsync(world, "Rvbotc")).Value;
        bool BothPass(long t) => RivalryKit.GreetPasses(humanGuid, undeadGuid, t) && RivalryKit.GreetPasses(undeadGuid, humanGuid, t);

        // No real player anywhere: no chatter nobody can see, however good the rolls.
        long alone = RivalryKit.TimeWhere(BothPass);
        await RivalryKit.RunAsync(world, alone);
        Assert.Empty(Sends(world));

        // A real player arrives: with BotToBot off the bots still keep to themselves (the player is only a spectator).
        await using WorldTestClient watcher = await world.AddPlayerAsync("Rvwatcher");
        await HelpTestKit.StandAsync(world, "Rvbota", ("Rvwatcher", 4f));
        world.Options.Social.Rivalry.Emotes.BotToBot = false;
        long off = RivalryKit.TimeWhere(BothPass, afterMs: alone + RivalryKit.Bucket);
        await RivalryKit.RunAsync(world, off);
        Assert.DoesNotContain(Sends(world), s => s.TargetIsBot);

        world.Options.Social.Rivalry.Emotes.BotToBot = true;
        long on = RivalryKit.TimeWhere(BothPass, afterMs: off + RivalryKit.Bucket);
        await RivalryKit.RunAsync(world, on);
        Assert.Contains(Sends(world), s => s.Bot == human && s.Target == undeadGuid && s.TargetIsBot && !s.Rude && RivalryKit.NeutralGreetings.Contains(s.Emote));
        Assert.Contains(Sends(world), s => s.Bot == undead && s.Target == humanGuid && s.TargetIsBot && !s.Rude && RivalryKit.NeutralGreetings.Contains(s.Emote));
    }

    // --- range ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheGreetingRange_IsClampedAtRunTime_ToTheTextEmoteListenRange_AndToRangeYards()
    {
        await using SocialWorld world = await RivalryKit.StartAsync();
        (_, ulong botGuid, WorldTestClient player, ulong playerGuid) = await PairAsync(world, "Rvrangebot", "Rvrangeone", dx: 10f);
        await using WorldTestClient keep = player;
        bool Passes(long t) => RivalryKit.GreetPasses(botGuid, playerGuid, t);

        // The player stands ten yards away: inside RangeYards (20) and the default listen range (25), outside a lowered one.
        world.Host.World.Options.ListenRangeTextEmote = 6f;
        long lowered = RivalryKit.TimeWhere(Passes);
        await RivalryKit.RunAsync(world, lowered);
        Assert.Empty(Sends(world));

        world.Host.World.Options.ListenRangeTextEmote = 25f;
        world.Options.Social.Rivalry.Emotes.RangeYards = 5;
        long narrow = RivalryKit.TimeWhere(Passes, afterMs: lowered + RivalryKit.Bucket);
        await RivalryKit.RunAsync(world, narrow);
        Assert.Empty(Sends(world));

        world.Options.Social.Rivalry.Emotes.RangeYards = 20;
        long open = RivalryKit.TimeWhere(Passes, afterMs: narrow + RivalryKit.Bucket);
        await RivalryKit.RunAsync(world, open);
        Assert.Single(Sends(world));
    }

    // --- limits ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheGreetingLimits_PerTarget10Minutes_PerPair30Minutes_AreHonoured_AcrossTwoBotsAndOnePlayer()
    {
        await using SocialWorld world = await RivalryKit.StartAsync();
        Guid one = await world.AddBotAsync("Rvlimone");
        Guid two = await world.AddBotAsync("Rvlimtwo");
        await using WorldTestClient player = await world.AddPlayerAsync("Rvlimplayer", RivalryKit.Undead);
        await HelpTestKit.StandAsync(world, "Rvlimone", ("Rvlimtwo", 2f), ("Rvlimplayer", 4f));
        ulong oneGuid = (await RivalryKit.GuidAsync(world, "Rvlimone")).Value, twoGuid = (await RivalryKit.GuidAsync(world, "Rvlimtwo")).Value;
        ulong playerGuid = (await RivalryKit.GuidAsync(world, "Rvlimplayer")).Value;
        // One bucket in which both bots' rolls pass: the rolls are fixed for 30 minutes, so only the limiters differ between the runs below.
        long t0 = RivalryKit.TimeWhere(t => RivalryKit.GreetPasses(oneGuid, playerGuid, t) && RivalryKit.GreetPasses(twoGuid, playerGuid, t));

        await RivalryKit.RunAsync(world, t0);
        Assert.Single(Sends(world)); // two eligible bots, one target: the target's 10-minute gap keeps it to one
        Guid first = Sends(world)[0].Bot;

        await RivalryKit.RunAsync(world, t0 + 4 * 60_000);
        Assert.Single(Sends(world)); // inside the target's gap (and the sender's 5-minute gap)

        await RivalryKit.RunAsync(world, t0 + 11 * 60_000);
        Assert.Equal(2, Sends(world).Count); // the target's gap is over: the bot that has not greeted yet does
        Assert.NotEqual(first, Sends(world)[1].Bot);
        Assert.Equal(new HashSet<Guid> { one, two }, Sends(world).Select(s => s.Bot).ToHashSet());

        await RivalryKit.RunAsync(world, t0 + 25 * 60_000);
        Assert.Equal(2, Sends(world).Count); // each pair has greeted within the last 30 minutes
    }

    [Fact]
    public async Task EmotesPerMinute_ZeroSilencesTheLane_AndOneAdmitsASingleEmoteAMinuteAcrossAllBots()
    {
        await using SocialWorld world = await RivalryKit.StartAsync();
        (Guid bot, _, WorldTestClient player, ulong playerGuid) = await PairAsync(world, "Rvcapped", "Rvcappedfoe");
        await using WorldTestClient keep = player;
        await RivalryKit.SeedAsync(world, "Rvcapped", "Rvcappedfoe", killerIsBot: false);

        world.Options.Social.Rivalry.Emotes.EmotesPerMinute = 0;
        await RivalryKit.RunAsync(world, 3_600_000);
        Assert.Empty(Sends(world));

        world.Options.Social.Rivalry.Emotes.EmotesPerMinute = 12;
        await RivalryKit.RunAsync(world, 3_700_000);
        PlayerbotRivalrySend send = Assert.Single(Sends(world));
        Assert.Equal(bot, send.Bot);
        Assert.Equal(playerGuid, send.Target);
    }

    [Fact]
    public async Task OneEmotePerMinute_IsSharedByEveryBotAndEveryTarget()
    {
        await using SocialWorld world = await RivalryKit.StartAsync(o => o.Social.Rivalry.Emotes.EmotesPerMinute = 1);
        await world.AddBotAsync("Rvminone");
        await world.AddBotAsync("Rvmintwo");
        await using WorldTestClient p1 = await world.AddPlayerAsync("Rvminplone", RivalryKit.Undead);
        await using WorldTestClient p2 = await world.AddPlayerAsync("Rvminpltwo", RivalryKit.Undead);
        await HelpTestKit.StandAsync(world, "Rvminone", ("Rvmintwo", 2f), ("Rvminplone", 3f), ("Rvminpltwo", 4f));
        ulong[] bots = [(await RivalryKit.GuidAsync(world, "Rvminone")).Value, (await RivalryKit.GuidAsync(world, "Rvmintwo")).Value];
        ulong[] players = [(await RivalryKit.GuidAsync(world, "Rvminplone")).Value, (await RivalryKit.GuidAsync(world, "Rvminpltwo")).Value];
        long t0 = RivalryKit.TimeWhere(t => bots.All(b => players.All(p => RivalryKit.GreetPasses(b, p, t))));

        await RivalryKit.RunAsync(world, t0);
        Assert.Single(Sends(world));
        await RivalryKit.RunAsync(world, t0 + 30_000);
        Assert.Single(Sends(world)); // the other bot and the other player are free, only the minute's budget is spent
        await RivalryKit.RunAsync(world, t0 + 61_000);
        Assert.Equal(2, Sends(world).Count);
    }

    // --- the switches and the wiring -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Rivalry:Enabled")]
    [InlineData("Rivalry:Emotes:Enabled")]
    [InlineData("Rivalry:Emotes:RivalEmotes")]
    public async Task EachSwitchThatSilencesRudeEmotes_SilencesThem_AndTurningItBackOnRestoresThem(string key)
    {
        await using SocialWorld world = await RivalryKit.StartAsync();
        (_, _, WorldTestClient player, _) = await PairAsync(world, "Rvswitchbot", "Rvswitchfoe");
        await using WorldTestClient keep = player;
        await RivalryKit.SeedAsync(world, "Rvswitchbot", "Rvswitchfoe", killerIsBot: false);
        PlayerbotSocialRivalryOptions rivalry = world.Options.Social.Rivalry;
        void Set(bool value)
        {
            switch (key)
            {
                case "Rivalry:Enabled": rivalry.Enabled = value; break;
                case "Rivalry:Emotes:Enabled": rivalry.Emotes.Enabled = value; break;
                case "Rivalry:Emotes:RivalEmotes": rivalry.Emotes.RivalEmotes = value; break;
                default: throw new ArgumentOutOfRangeException(nameof(key));
            }
        }

        Set(false);
        await RivalryKit.RunAsync(world, 3_600_000);
        Assert.Empty(Sends(world));

        Set(true);
        await RivalryKit.RunAsync(world, 3_700_000);
        Assert.Single(Sends(world));
    }

    [Fact]
    public async Task TheSameFactionWaveSwitch_IsAnotherKey_AndDoesNotSilenceTheRivalryLane()
    {
        await using SocialWorld world = await RivalryKit.StartAsync(o => o.Social.Emotes.Enabled = false);
        (_, _, WorldTestClient player, _) = await PairAsync(world, "Rvwavebot", "Rvwavefoe");
        await using WorldTestClient keep = player;
        await RivalryKit.SeedAsync(world, "Rvwavebot", "Rvwavefoe", killerIsBot: false);

        await RivalryKit.RunAsync(world, 3_600_000);

        Assert.Single(Sends(world));
    }

    [Fact]
    public async Task TheLane_RunsInTheFeaturesRealPass_AndSitsBeforePrune()
    {
        await using SocialWorld world = await RivalryKit.StartAsync();
        (_, _, WorldTestClient player, ulong playerGuid) = await PairAsync(world, "Rvpassbot", "Rvpassfoe");
        await using WorldTestClient keep = player;
        await RivalryKit.SeedAsync(world, "Rvpassbot", "Rvpassfoe", killerIsBot: false);
        PlayerbotSocialFeature feature = SocialTestKit.Feature(world.Host);
        int passes = feature.Passes;
        long faults = feature.LaneFaultCount;

        await world.Host.OnWorldAsync(() =>
        {
            foreach (var online in world.Host.World.OnlinePlayers) online.ZoneId = RivalryKit.OrdinaryZone;
            feature.RunPass(1000);
            return true;
        });

        Assert.Contains(Sends(world), s => s.Rude && s.Target == playerGuid); // the feature's own RunLane reached the rivalry case
        Assert.Equal(passes + 1, feature.Passes);
        Assert.Equal(faults, feature.LaneFaultCount);

        string[] names = (string[])typeof(PlayerbotSocialFeature).GetField("LaneNames", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Assert.Equal("prune", names[^1]);          // RunLane's default: case is Prune, so a new lane goes before it
        Assert.Equal("rivalry", names[^2]);
        Assert.Single(names, "rivalry");
    }

    // --- the layers: War Mode and RudeAtBots (design 7.1) -------------------------------------------------------------------------------------

    [Fact]
    public async Task UnderWarMode_AWarOnlyRivalPlayerIsTaunted_ButABotIsStillJudgedByTheNormalLayer()
    {
        await using SocialWorld world = await RivalryKit.StartAsync(o => o.Social.Rivalry.Emotes.RudeAtBots = true);
        Guid bot = await world.AddBotAsync("Rvwarbot");
        await world.AddBotAsync("Rvwarfoebot", race: RivalryKit.Undead);
        await using WorldTestClient player = await world.AddPlayerAsync("Rvwarfoe", RivalryKit.Undead);
        await HelpTestKit.StandAsync(world, "Rvwarbot", ("Rvwarfoebot", 2f), ("Rvwarfoe", 3f));
        ulong playerGuid = (await RivalryKit.GuidAsync(world, "Rvwarfoe")).Value, foeBotGuid = (await RivalryKit.GuidAsync(world, "Rvwarfoebot")).Value;

        // One kill each, noted with War Mode on: the normal layer holds a plain -20 (Disliked), the war layer a x3 rival.
        world.Options.Social.WarMode.Enabled = true;
        await RivalryKit.SeedAsync(world, "Rvwarbot", "Rvwarfoe", killerIsBot: false, kills: 1);
        await RivalryKit.SeedAsync(world, "Rvwarbot", "Rvwarfoebot", killerIsBot: true, kills: 1);
        PlayerbotRelation toPlayer = RivalryKit.RelationOf(world, bot, playerGuid)!;
        Assert.True(RivalryKit.Memory(world).IsRival(toPlayer), "a rival in the war layer");
        Assert.False(toPlayer.Flags.HasFlag(PlayerbotRelationFlags.Rival), "and only there");

        await RivalryKit.RunAsync(world, 3_600_000);
        Assert.Contains(Sends(world), s => s.Bot == bot && s.Target == playerGuid && s.Rude && !s.TargetIsBot); // toward a player the war layer decides
        Assert.DoesNotContain(Sends(world), s => s.Bot == bot && s.Target == foeBotGuid);                      // toward a bot only the normal layer does

        // War Mode off: the same player is just a dislike again, and the bot stays quiet toward both.
        world.Options.Social.WarMode.Enabled = false;
        int before = Sends(world).Count;
        await RivalryKit.RunAsync(world, 3_600_000 + 3 * RivalryKit.Bucket);
        Assert.DoesNotContain(Sends(world).Skip(before), s => s.Bot == bot && (s.Target == playerGuid || s.Target == foeBotGuid));
    }

    [Fact]
    public async Task ANormalLayerRivalBot_IsTauntedOnlyWhenRudeAtBotsIsOn()
    {
        await using SocialWorld world = await RivalryKit.StartAsync();
        Guid bot = await world.AddBotAsync("Rvbotrival");
        await world.AddBotAsync("Rvbotfoe", race: RivalryKit.Undead);
        await using WorldTestClient spectator = await world.AddPlayerAsync("Rvspectator");
        await HelpTestKit.StandAsync(world, "Rvbotrival", ("Rvbotfoe", 2f), ("Rvspectator", 3f));
        ulong foeGuid = (await RivalryKit.GuidAsync(world, "Rvbotfoe")).Value;
        await RivalryKit.SeedAsync(world, "Rvbotrival", "Rvbotfoe", killerIsBot: true);
        Assert.True(RivalryKit.Memory(world).IsRival(RivalryKit.RelationOf(world, bot, foeGuid)!));

        await RivalryKit.RunAsync(world, 3_600_000);
        Assert.DoesNotContain(Sends(world), s => s.Bot == bot && s.Target == foeGuid); // RudeAtBots is off by default

        world.Options.Social.Rivalry.Emotes.RudeAtBots = true;
        await RivalryKit.RunAsync(world, 3_700_000);
        PlayerbotRivalrySend taunt = Assert.Single(Sends(world), s => s.Bot == bot && s.Target == foeGuid);
        Assert.True(taunt.Rude);
        Assert.True(taunt.TargetIsBot);
        Assert.Contains(taunt.Emote, RivalryKit.RudeEmotes);
    }

    // --- RudeRequiresOptIn (decision R11) -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WithRudeRequiresOptIn_ARivalIsTauntedOnlyAfterItWhispersRivalsOn()
    {
        await using SocialWorld world = await RivalryKit.StartAsync(o => o.Social.Rivalry.Emotes.RudeRequiresOptIn = true);
        (_, _, WorldTestClient player, ulong playerGuid) = await PairAsync(world, "Rvoptinbot", "Rvoptinplay");
        await using WorldTestClient keep = player;
        await RivalryKit.SeedAsync(world, "Rvoptinbot", "Rvoptinplay", killerIsBot: false);
        int account = await world.Host.PlayerStateAsync("Rvoptinplay", p => p.AccountId);

        await RivalryKit.RunAsync(world, 3_600_000);
        Assert.Empty(Sends(world));

        Assert.True(RivalryKit.OptOut(world).Handle(playerGuid, account, "rivals on", 0, world.Options.Social.Rivalry).Consumed);
        await RivalryKit.RunAsync(world, 3_700_000);
        Assert.Single(Sends(world));
    }
}

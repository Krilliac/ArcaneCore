using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Teleport;
using ArcaneCore.World.Tests.Playerbots.Chat;
using ArcaneCore.World.Tests.Playerbots.Combat;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// War Mode defense, the report and the announcement (<c>World:Playerbots:Pvp:Defense</c>, design 1.2 and 1.3): a bot attacked by an enemy player reports to the
/// call board only where the rules allow, and the "defense" social lane posts one line per call in the zone's "LocalDefense" channel, under the zone's cooldown,
/// while the board never depends on the line. Each refusal runs the same scene as the report that is accepted, with one thing changed.
/// </summary>
public sealed class PlayerbotDefenseCallWorldTests
{
    public enum Variation { None, DefenseOff, BotInExemptArea, ProvokerInExemptArea, DefendPost }

    [Theory]
    [InlineData(true, true, false, false, false, false, "None")]
    [InlineData(false, true, false, false, false, false, "Off")]
    [InlineData(true, false, false, false, false, false, "NotOpenWorld")]
    [InlineData(true, true, true, false, false, false, "WarModeProtected")]
    [InlineData(true, true, false, true, false, false, "WarModeProtected")]
    [InlineData(true, true, false, false, true, false, "Busy")]
    [InlineData(true, true, false, false, false, true, "Busy")]
    public void TheRefusal_NamesTheFirstReasonAgainstReporting(bool enabled, bool openWorld, bool selfProtected, bool provokerProtected, bool inDuel, bool onPost,
        string expected)
        => Assert.Equal(expected, PlayerbotBrain.DefenseRefusal(enabled, openWorld, selfProtected, provokerProtected, inDuel, onPost).ToString());

    [Fact]
    public void AnInstanceOrBattlegroundMap_IsNeverReportedFrom_EvenWhenEverythingElseAllowsIt()
        => Assert.Equal(DefenseReportRefusal.NotOpenWorld, PlayerbotBrain.DefenseRefusal(enabled: true, openWorld: false, false, false, false, false));

    private static async Task<SocialWorld> StartAsync(bool defense = true, bool announce = true, bool chat = false)
        => await RivalryKit.StartAsync(options =>
        {
            options.Pvp.Defense.Enabled = defense;
            options.Pvp.Defense.Announce = announce;
            options.Pvp.Defense.ZoneAnnounceSeconds = 30;
            if (chat)
            {
                ChatSamples.NoScreeningDeadline(options.Chat);
                options.Chat.Speech.ChannelLinesPerMinute = 60;
            }
        }, chat);

    /// <summary>Runs one report of <paramref name="bot"/> against <paramref name="foe"/> with the variation applied, on the world thread.</summary>
    private static Task<(DefenseReportRefusal Refusal, DefenseReport Report, int OpenCalls)> ReportAsync(SocialWorld world, PlayerbotBrain brain, string bot, string foe, Variation variation)
        => world.Host.OnWorldAsync(() =>
        {
            Player self = RivalryKit.Online(world, bot), enemy = RivalryKit.Online(world, foe);
            self.ZoneId = enemy.ZoneId = RivalryKit.OrdinaryZone;
            PvpAreaState.SetWarModeExempt(self, variation == Variation.BotInExemptArea);
            PvpAreaState.SetWarModeExempt(enemy, variation == Variation.ProvokerInExemptArea);
            if (variation == Variation.DefendPost) brain.Defend = new PlayerbotDefendPost(self.MapId, new System.Numerics.Vector3(self.X, self.Y, self.Z), 40f, 7);
            if (variation == Variation.DefenseOff) world.Options.Pvp.Defense.Enabled = false;

            DefenseReportRefusal refusal = brain.TryReportDefense(self, enemy, out DefenseReport report);
            var open = new List<DefenseCall>();
            brain.DefenseCalls!.OpenCalls(open);
            return (refusal, report, open.Count);
        });

    [Fact]
    public async Task AnAttackedBot_OpensACall_AndTheThreeSecondThrottleHoldsTheNextReport()
    {
        await using SocialWorld world = await StartAsync();
        Guid bot = await world.AddBotAsync("Dfreporter");
        await using WorldTestClient foe = await world.AddPlayerAsync("Dfenemy", RivalryKit.Undead);
        PlayerbotBrain brain = HelpTestKit.BrainOf(world, bot);

        (DefenseReportRefusal first, DefenseReport report, int open) = await ReportAsync(world, brain, "Dfreporter", "Dfenemy", Variation.None);
        Assert.Equal(DefenseReportRefusal.None, first);
        Assert.Equal(DefenseReportOutcome.Opened, report.Outcome);
        Assert.Equal(1, open);

        (DefenseReportRefusal second, _, int stillOpen) = await ReportAsync(world, brain, "Dfreporter", "Dfenemy", Variation.None);
        Assert.Equal(DefenseReportRefusal.Throttled, second); // the same world step: far inside the 3 s
        Assert.Equal(1, stillOpen);
    }

    [Theory]
    [InlineData(Variation.DefenseOff, "Off")]
    [InlineData(Variation.BotInExemptArea, "WarModeProtected")]
    [InlineData(Variation.ProvokerInExemptArea, "WarModeProtected")]
    [InlineData(Variation.DefendPost, "Busy")]
    public async Task TheReport_IsRefused_ForEachGate_AndNoCallOpens(Variation variation, string expected)
    {
        await using SocialWorld world = await StartAsync();
        Guid bot = await world.AddBotAsync("Dfrefused");
        await using WorldTestClient foe = await world.AddPlayerAsync("Dfenemyr", RivalryKit.Undead);
        PlayerbotBrain brain = HelpTestKit.BrainOf(world, bot);

        (DefenseReportRefusal refusal, DefenseReport report, int open) = await ReportAsync(world, brain, "Dfrefused", "Dfenemyr", variation);

        Assert.Equal(expected, refusal.ToString());
        Assert.Equal(default, report);
        Assert.Equal(0, open);
    }

    /// <summary>
    /// The map check of the report itself: a bot standing inside an instance (the Deadmines, map 36, which the test map data marks Instanceable) is refused
    /// with NotOpenWorld and no call opens, while the same bot on the open continent reported in the test above.
    /// </summary>
    [Fact]
    public async Task ABotInsideAnInstance_IsRefusedAsNotOpenWorld_AndNoCallOpens()
    {
        await using SocialWorld world = await StartAsync();
        Guid bot = await world.AddBotAsync("Dfinstance");
        await using WorldTestClient foe = await world.AddPlayerAsync("Dfenemyi", RivalryKit.Undead);
        PlayerbotBrain brain = HelpTestKit.BrainOf(world, bot);

        Assert.True(await world.Host.OnWorldAsync(() =>
            world.Host.WorldServices.GetRequiredService<TeleportFeature>().Teleports.TeleportTo(RivalryKit.Online(world, "Dfinstance"), 36, -16.4f, -383.07f, 61.78f, 0f)),
            "teleport into the Deadmines refused");
        await world.Host.WaitForWorldAsync(() => RivalryKit.Online(world, "Dfinstance") is { IsInWorld: true, MapId: 36 }, "the bot to stand in the Deadmines");

        (DefenseReportRefusal refusal, DefenseReport report, int open) = await world.Host.OnWorldAsync(() =>
        {
            Player self = RivalryKit.Online(world, "Dfinstance"), enemy = RivalryKit.Online(world, "Dfenemyi");
            Assert.True(self.Map!.Template is { Instanceable: true });
            PvpAreaState.SetWarModeExempt(self, false);
            PvpAreaState.SetWarModeExempt(enemy, false);
            DefenseReportRefusal result = brain.TryReportDefense(self, enemy, out DefenseReport made);
            var calls = new List<DefenseCall>();
            brain.DefenseCalls!.OpenCalls(calls);
            return (result, made, calls.Count);
        });

        Assert.Equal(DefenseReportRefusal.NotOpenWorld, refusal);
        Assert.Equal(default, report);
        Assert.Equal(0, open);
    }

    private static async Task<(Guid Bot, WorldTestClient Listener, WorldTestClient Foe)> ChannelSceneAsync(SocialWorld world)
    {
        Guid bot = await world.AddBotAsync("Dfcaller");
        WorldTestClient listener = await world.AddPlayerAsync("Dflistener");
        WorldTestClient foe = await world.AddPlayerAsync("Dffoe", RivalryKit.Undead);
        await HelpTestKit.StandAsync(world, "Dfcaller", ("Dflistener", 3f), ("Dffoe", 6f));
        await world.Host.OnWorldAsync(() =>
        {
            string channel = "LocalDefense - " + RivalryKit.Ordinary.Name;
            foreach (Player player in world.Host.World.OnlinePlayers) player.ZoneId = RivalryKit.OrdinaryZone;
            SocialTestKit.Social(world.Host).Context.Channels.Join(RivalryKit.Online(world, "Dfcaller"), channel, string.Empty);
            SocialTestKit.Social(world.Host).Context.Channels.Join(RivalryKit.Online(world, "Dflistener"), channel, string.Empty);
            return true;
        });
        return (bot, listener, foe);
    }

    private static Task<DefenseReportRefusal> ReportFromChannelSceneAsync(SocialWorld world, Guid bot)
        => world.Host.OnWorldAsync(() =>
        {
            Player self = RivalryKit.Online(world, "Dfcaller"), enemy = RivalryKit.Online(world, "Dffoe");
            PvpAreaState.SetWarModeExempt(self, false);
            PvpAreaState.SetWarModeExempt(enemy, false);
            return HelpTestKit.BrainOf(world, bot).TryReportDefense(self, enemy, out _);
        });

    private static Task RunLaneAsync(SocialWorld world, long nowMs)
        => world.Host.OnWorldAsync(() =>
        {
            SocialTestKit.Feature(world.Host).Defense.Run(SocialTestKit.BotPass(world.Host, nowMs));
            return true;
        });

    [Fact]
    public async Task ACall_IsAnnouncedOnce_InTheZonesLocalDefenseChannel_ToAListener()
    {
        await using SocialWorld world = await StartAsync(chat: true);
        (Guid bot, WorldTestClient listener, WorldTestClient foe) = await ChannelSceneAsync(world);
        await using WorldTestClient keepListener = listener;
        await using WorldTestClient keepFoe = foe;
        Assert.Equal(DefenseReportRefusal.None, await ReportFromChannelSceneAsync(world, bot));
        PlayerbotDefenseLane lane = SocialTestKit.Feature(world.Host).Defense;

        await RunLaneAsync(world, 1_000);
        Assert.Equal(1, lane.Announced);
        await HelpTestKit.UntilAsync(() => world.Log.Count("Dfcaller", WorldOpcode.CmsgMessagechat) == 1, "the call line to be said in the channel");

        // The same call is not announced again, at the next passes or after the zone's cooldown.
        await RunLaneAsync(world, 2_000);
        await RunLaneAsync(world, 100_000);
        Assert.Equal(1, lane.Announced);
        Assert.Equal(1, world.Log.Count("Dfcaller", WorldOpcode.CmsgMessagechat));

        var heard = (await listener.CollectAsync()).Where(p => p.Opcode == WorldOpcode.SmsgMessagechat).Select(p => ChatMessage.Parse(p.Payload))
            .Where(m => m.Type == ChatType.Channel).ToList();
        Assert.Contains(heard, m => m.Text.Contains("is under attack!", StringComparison.Ordinal) && m.Text.Contains(RivalryKit.Ordinary.Name, StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithAnnounceOff_NoLineIsSaid_AndTheCallStaysOnTheBoard()
    {
        await using SocialWorld world = await StartAsync(announce: false, chat: true);
        (Guid bot, WorldTestClient listener, WorldTestClient foe) = await ChannelSceneAsync(world);
        await using WorldTestClient keepListener = listener;
        await using WorldTestClient keepFoe = foe;
        Assert.Equal(DefenseReportRefusal.None, await ReportFromChannelSceneAsync(world, bot));

        await RunLaneAsync(world, 1_000);

        Assert.Equal(0, SocialTestKit.Feature(world.Host).Defense.Announced);
        Assert.Equal(0, world.Log.Count("Dfcaller", WorldOpcode.CmsgMessagechat));
        int open = await world.Host.OnWorldAsync(() =>
        {
            var calls = new List<DefenseCall>();
            HelpTestKit.BrainOf(world, bot).DefenseCalls!.OpenCalls(calls);
            return calls.Count;
        });
        Assert.Equal(1, open); // coordination does not depend on the line
    }

    // --- responders (design 1.4 and 1.5) ---------------------------------------------------------------------------------------------------------

    public enum Assignment { None, OutsideRadius, LevelOutsideWindow, InCombat, HelpPending, NoRespondersAllowed, UnflaggedWarModeOff, JoinerBothSidesOff, JoinerBothSidesOn }

    private static void FlagForGood(Player player)
    {
        player.Flags |= PlayerFlags.PvpDesired; // the world's PvP timer keeps a wanted flag
        PlayerbotPvpTestKit.Flag(player);
    }

    private static bool IsFreeGoal(SocialWorld world, Guid bot)
        => world.Host.OnWorldAsync(() => SocialTestKit.BotPass(world.Host, 0).Bots.First(b => b.Id == bot) is { } entry && PlayerbotHelpLane.IsFree(entry)).Result;

    /// <summary>
    /// The scene: a caller bot (Alliance), its enemy player standing 3 yd east of it and attacking it, and the responder bot <paramref name="responder"/> standing
    /// <paramref name="dx"/> yd east of the caller. Everyone is flagged for PvP (War Mode stays off) except as <paramref name="flagResponder"/> says. The caller reports,
    /// so one call is open on the board. The frozen world runs no social pass: the test drives the defense lane by hand.
    /// </summary>
    private static async Task<WorldTestClient> CallSceneAsync(SocialWorld world, string responder, float dx, byte race = SocialTestKit.Human, bool flagResponder = true)
    {
        await world.AddBotAsync("Rcaller");
        await world.AddBotAsync(responder, race: race);
        WorldTestClient foe = await world.AddPlayerAsync("Rfoe", RivalryKit.Undead);
        await HelpTestKit.StandAsync(world, "Rcaller", ("Rfoe", 3f));
        Guid callerId = world.Bots["Rcaller"], responderId = world.Bots[responder];
        await world.Host.OnWorldAsync(() =>
        {
            Player caller = RivalryKit.Online(world, "Rcaller"), enemy = RivalryKit.Online(world, "Rfoe"), resp = RivalryKit.Online(world, responder);
            resp.Relocate(caller.X + dx, caller.Y, caller.Z, 0f, world.Host.World.NowMs);
            foreach (Player player in world.Host.World.OnlinePlayers) player.ZoneId = RivalryKit.OrdinaryZone;
            FlagForGood(caller);
            FlagForGood(enemy);
            if (flagResponder) FlagForGood(resp);
            PvpAreaState.SetWarModeExempt(caller, false);
            PvpAreaState.SetWarModeExempt(enemy, false);
            PvpAreaState.SetWarModeExempt(resp, false);
            world.Options.Social.WarMode.Enabled = false;
            world.Options.Pvp.Defense.MaxRespondersPerSide = 1;
            // The enemy starts on the caller (the aggressor), so the rule "only the one who struck first" holds for a defender with War Mode off.
            Assert.True(caller.Map!.Combat.Attack(enemy, caller), "the enemy could not attack the caller");
            caller.Map!.Combat.DealDamage(enemy, caller, 1);
            DefenseReportRefusal refusal = HelpTestKit.BrainOf(world, callerId).TryReportDefense(caller, enemy, out DefenseReport report);
            Assert.Equal(DefenseReportRefusal.None, refusal);
            Assert.True(report.Accepted, "the call was not opened");
        });
        _ = responderId;
        return foe;
    }

    private static Task<int> RunDefenseLaneAsync(SocialWorld world)
        => world.Host.OnWorldAsync(() =>
        {
            PlayerbotDefenseLane lane = SocialTestKit.Feature(world.Host).Defense;
            lane.Run(SocialTestKit.BotPass(world.Host, 1_000));
            return lane.Assigned;
        });

    [Theory]
    [InlineData(Assignment.None, true)]
    [InlineData(Assignment.OutsideRadius, false)]
    [InlineData(Assignment.LevelOutsideWindow, false)]
    [InlineData(Assignment.InCombat, false)]
    [InlineData(Assignment.HelpPending, false)]
    [InlineData(Assignment.NoRespondersAllowed, false)]
    [InlineData(Assignment.UnflaggedWarModeOff, false)]
    [InlineData(Assignment.JoinerBothSidesOff, false)]
    [InlineData(Assignment.JoinerBothSidesOn, true)]
    public async Task TheLane_AssignsAFreeBotOnlyWhenEveryRuleHolds(Assignment variation, bool assigned)
    {
        await using SocialWorld world = await StartAsync();
        bool joiner = variation is Assignment.JoinerBothSidesOff or Assignment.JoinerBothSidesOn;
        await using WorldTestClient foe = await CallSceneAsync(world, "Rresp", variation == Assignment.OutsideRadius ? 400f : 60f,
            joiner ? RivalryKit.Undead : SocialTestKit.Human, flagResponder: variation != Assignment.UnflaggedWarModeOff);
        Guid responder = world.Bots["Rresp"];
        PlayerbotBrain brain = HelpTestKit.BrainOf(world, responder);
        await HelpTestKit.UntilAsync(() => IsFreeGoal(world, responder), "the responder to be on a free goal");

        int assignedCount = await world.Host.OnWorldAsync(() =>
        {
            Player resp = RivalryKit.Online(world, "Rresp");
            switch (variation)
            {
                case Assignment.LevelOutsideWindow:
                    resp.Level = 30;
                    break;
                case Assignment.InCombat:
                    HelpTestKit.Engage(HelpTestKit.SpawnMob(resp, -4f), resp);
                    break;
                case Assignment.HelpPending:
                    Assert.True(brain.RequestHelp(PlayerbotHelpKind.Assist, RivalryKit.Online(world, "Rcaller").Guid));
                    break;
                case Assignment.NoRespondersAllowed:
                    world.Options.Pvp.Defense.MaxRespondersPerSide = 0;
                    break;
                case Assignment.JoinerBothSidesOn:
                    world.Options.Pvp.Defense.BothSides = true;
                    break;
            }

            PlayerbotDefenseLane lane = SocialTestKit.Feature(world.Host).Defense;
            lane.Run(SocialTestKit.BotPass(world.Host, 1_000));
            return lane.Assigned;
        });

        Assert.Equal(assigned ? 1 : 0, assignedCount);
        Assert.Equal(assigned, await world.Host.OnWorldAsync(() => brain.Responding));
    }

    [Fact]
    public async Task TheDefenderCap_HoldsWhenTwoBotsAreFree()
    {
        await using SocialWorld world = await StartAsync();
        await using WorldTestClient foe = await CallSceneAsync(world, "Rrespa", 50f);
        await world.AddBotAsync("Rrespb");
        await world.Host.OnWorldAsync(() =>
        {
            Player caller = RivalryKit.Online(world, "Rcaller"), second = RivalryKit.Online(world, "Rrespb");
            second.Relocate(caller.X + 40f, caller.Y, caller.Z, 0f, world.Host.World.NowMs);
            second.ZoneId = RivalryKit.OrdinaryZone;
            FlagForGood(second);
        });
        await HelpTestKit.UntilAsync(() => IsFreeGoal(world, world.Bots["Rrespa"]) && IsFreeGoal(world, world.Bots["Rrespb"]), "both responders to be free");

        await RunDefenseLaneAsync(world);

        // One slot on the side: the nearer bot (Rrespb, 40 yd, added second) takes it over the farther Rrespa (50 yd, added first), so examination order and distance order disagree.
        Assert.True(await world.Host.OnWorldAsync(() => HelpTestKit.BrainOf(world, world.Bots["Rrespb"]).Responding));
        Assert.False(await world.Host.OnWorldAsync(() => HelpTestKit.BrainOf(world, world.Bots["Rrespa"]).Responding));
    }

    /// <summary>
    /// The responder, 60 yd off, is assigned, walks to the call and takes the attacker on for the Defense reason. RED proof: with the <c>TryRespond</c> line removed from
    /// the brain's think nothing ever engages (the bot keeps to its own goal), and this test times out waiting for the engagement.
    /// </summary>
    [Fact]
    public async Task AResponder_WalksToTheCall_AndEngagesTheAttackerOnArrival()
    {
        await using SocialWorld world = await StartAsync();
        await using WorldTestClient foe = await CallSceneAsync(world, "Rwalker", 60f);
        Guid responder = world.Bots["Rwalker"];
        PlayerbotBrain brain = HelpTestKit.BrainOf(world, responder);
        await HelpTestKit.UntilAsync(() => IsFreeGoal(world, responder), "the responder to be on a free goal");

        Assert.Equal(1, await RunDefenseLaneAsync(world));
        Assert.True(await world.Host.OnWorldAsync(() => brain.Responding));

        Player enemy = await world.Host.OnWorldAsync(() => RivalryKit.Online(world, "Rfoe"));
        await HelpTestKit.UntilAsync(() => world.Host.OnWorldAsync(() => ReferenceEquals(brain.EngagedPlayer, enemy) && brain.EngagedReason == PlayerbotEngageReason.Defense).Result,
            "the responder to take the attacker on", seconds: 40);
    }

    [Fact]
    public async Task AResponder_NeverReachesIntoAnExemptArea()
    {
        await using SocialWorld world = await StartAsync();
        await using WorldTestClient foe = await CallSceneAsync(world, "Rexempt", 50f);
        Guid responder = world.Bots["Rexempt"];
        PlayerbotBrain brain = HelpTestKit.BrainOf(world, responder);
        await HelpTestKit.UntilAsync(() => IsFreeGoal(world, responder), "the responder to be on a free goal");
        Assert.Equal(1, await RunDefenseLaneAsync(world));

        // The attacker runs into its capital after the call: the responder may walk to the call, but it never takes the attacker on.
        await world.Host.OnWorldAsync(() => PvpAreaState.SetWarModeExempt(RivalryKit.Online(world, "Rfoe"), true));
        await HelpTestKit.UntilAsync(() => world.Host.OnWorldAsync(() =>
            PlayerbotGeometry.Distance(RivalryKit.Online(world, "Rexempt"), RivalryKit.Online(world, "Rcaller")) <= PlayerbotBrain.DefenseArrivalYards).Result,
            "the responder to reach the call", seconds: 40);

        Assert.Null(await world.Host.OnWorldAsync(() => brain.EngagedPlayer));
        Assert.Null(await world.Host.OnWorldAsync(() => brain.InspectionPvpTarget));
    }

    [Fact]
    public async Task HelpAndDefense_AreMutuallyExclusive_ForOneBot()
    {
        await using SocialWorld world = await StartAsync();
        await using WorldTestClient foe = await CallSceneAsync(world, "Rexcl", 60f);
        Guid responder = world.Bots["Rexcl"];
        PlayerbotBrain brain = HelpTestKit.BrainOf(world, responder);
        await HelpTestKit.UntilAsync(() => IsFreeGoal(world, responder), "the responder to be on a free goal");

        // A responder is no longer free for the help lane, and a bot with a help request pending refuses a defense request.
        Assert.Equal(1, await RunDefenseLaneAsync(world));
        Assert.False(IsFreeGoal(world, responder));
        Assert.False(await world.Host.OnWorldAsync(() => brain.RequestHelp(PlayerbotHelpKind.Assist, RivalryKit.Online(world, "Rcaller").Guid)
            && brain.RequestDefense(1, DefenseSide.Defender)));
    }
}

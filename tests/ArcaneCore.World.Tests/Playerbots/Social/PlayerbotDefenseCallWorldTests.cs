using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Teleport;
using ArcaneCore.World.Tests.Playerbots.Chat;
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
}

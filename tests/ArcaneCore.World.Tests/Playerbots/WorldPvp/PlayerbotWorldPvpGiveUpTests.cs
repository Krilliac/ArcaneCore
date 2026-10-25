using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.OutdoorPvP;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Events;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.WorldPvp.SilithystWorld;

namespace ArcaneCore.World.Tests.Playerbots.WorldPvp;

/// <summary>
/// A Silithyst carry that is given up (no trigger, timeout, stall) must not start again at the next think: the flag (29519) never expires, so
/// without a drop and a back-off the bot would open a new carry and report the same fault at every think. Also: a pickup inside the trigger
/// volume, the carry timer, the run cool-down, the stall give-up and the delivery count (docs/areas/playbots-social.md, World PvP: Silithyst).
/// </summary>
public sealed class PlayerbotWorldPvpGiveUpTests
{
    private const uint Flag = SilithusCatalog.SpellSilithystFlag, Traces = SilithusCatalog.SpellTracesOfSilithyst;

    private static string Key(string code) => PlayerbotAnomalies.Key("world-pvp", code);

    private static WorldPvpStep Step(SilithystWorld world, Guid bot, PlayerbotWorldPvpObjectives run, uint elapsedMs = 200, int budget = 5)
    {
        WorldSession session = world.World.Bots.FindSession(bot)!;
        session.ManagedBudget = new ManagedActionBudget(budget);
        return run.Update(session.Player!, elapsedMs, mayBegin: false);
    }

    private static async Task<(PlayerbotWorldPvpObjectives Run, Func<uint> Clock, Action<uint> Advance)> ControllerAsync(SilithystWorld world, Guid bot)
    {
        uint clock = 1_000_000;
        PlayerbotWorldPvpObjectives run = await world.OnWorldAsync(() =>
            new PlayerbotWorldPvpObjectives(world.World.Bots.FindSession(bot)!, world.Options, null) { Clock = () => clock });
        return (run, () => clock, ms => clock += ms);
    }

    private static Task RemoveTriggersAsync(SilithystWorld world) => world.OnWorldAsync(() =>
    {
        Game.Maps.Templates.WorldMaps maps = Game.Maps.Templates.WorldMaps.Of(world.World.Host.World);
        maps.ReplaceAreaTriggerTables(maps.BuildAreaTriggerTables(GridTerrain.InMemoryMapDataStore.Content with { AreaTriggers = [], AreaTriggerTeleports = [] }));
        return true;
    });

    // --- the give-up does not loop -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("no-trigger", true)]
    [InlineData("carry-timeout", true)]
    [InlineData("no-trigger", false)]
    [InlineData("carry-timeout", false)]
    public async Task AGivenUpCarry_DropsTheFlag_AndStartsNoSecondEpisode_OrSecondAnomaly(string why, bool cancelWorks)
    {
        await using SilithystWorld world = await StartAsync(o => o.WorldPvp.Silithyst.Enabled = false);
        Guid bot = await world.AddBotAsync("Dustgiveup" + (cancelWorks ? "a" : "b") + (why == "no-trigger" ? "t" : "c"), 1);
        Assert.True(await world.CastOnSelfAsync(bot, Flag));
        if (why == "no-trigger") await RemoveTriggersAsync(world);
        using AnomalyCapture capture = await world.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(world.PlayerOf(bot)));
        (PlayerbotWorldPvpObjectives run, _, Action<uint> advance) = await ControllerAsync(world, bot);
        string code = why == "no-trigger" ? "silithyst-unreachable-trigger" : "silithyst-carry-timeout";
        int budget = cancelWorks ? 5 : 0;

        if (why == "carry-timeout")
        {
            Assert.True((await world.OnWorldAsync(() => Step(world, bot, run))).Busy, "the carrier was not taken up");
            await world.OnWorldAsync(() => Step(world, bot, run, PlayerbotWorldPvpObjectives.CarryMaxMs + 1, budget));
        }
        else
        {
            await world.OnWorldAsync(() => Step(world, bot, run, budget: budget));
        }

        Assert.Equal(1, capture.Count(Key(code)));
        Assert.Equal(why, run.LastOutcome);
        Assert.False(run.Active);
        Assert.Equal(!cancelWorks, await world.OnWorldAsync(() => world.HasAura(world.PlayerOf(bot), Flag)));

        // Step again, over and over, as the brain does at every think: no new episode, no second report, whether the flag went or not.
        for (int think = 0; think < 20; think++)
        {
            advance(200);
            WorldPvpStep again = await world.OnWorldAsync(() => Step(world, bot, run, budget: budget));
            Assert.False(again.Busy, $"think {think}: the bot took the flag up again");
            Assert.False(run.Active, $"think {think}: a new episode opened");
        }

        Assert.Equal(1, capture.Count(Key(code)));
        Assert.Equal(0, run.Deliveries);
    }

    [Fact]
    public async Task AFlagThatCannotBeCancelled_IsTakenUpAgainAfterTheBackoff_WithoutReportingTheSameFaultAgain()
    {
        await using SilithystWorld world = await StartAsync(o => o.WorldPvp.Silithyst.Enabled = false);
        Guid bot = await world.AddBotAsync("Dustbackoff", 1);
        Assert.True(await world.CastOnSelfAsync(bot, Flag));
        await RemoveTriggersAsync(world);
        using AnomalyCapture capture = await world.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(world.PlayerOf(bot)));
        (PlayerbotWorldPvpObjectives run, _, Action<uint> advance) = await ControllerAsync(world, bot);

        await world.OnWorldAsync(() => Step(world, bot, run, budget: 0));
        Assert.Equal(1, capture.Count(Key("silithyst-unreachable-trigger")));
        advance(PlayerbotWorldPvpObjectives.GiveUpBackoffMs + 1);

        // The same flag is carried again (nothing took it off), the trigger is still missing: the fault was reported already.
        await world.OnWorldAsync(() => Step(world, bot, run, budget: 0));
        Assert.Equal(1, capture.Count(Key("silithyst-unreachable-trigger")));
        Assert.False(run.Active);
    }

    // --- a pickup inside the trigger volume ------------------------------------------------------------------------

    [Fact]
    public async Task AFlagPickedUpInsideTheTriggerVolume_IsDelivered_ByWalkingOutAndIn()
    {
        await using SilithystWorld world = await StartAsync();
        Guid bot = await world.AddBotAsync("Dustinside", 1, at: AllianceTrigger + new Vector3(1f, 0f, 0f));
        using AnomalyCapture capture = await world.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(world.PlayerOf(bot)));
        Assert.True(await world.CastOnSelfAsync(bot, Flag));

        Assert.True(await world.RunUntilAsync(120_000, () => world.BrainOf(bot).WorldPvp.Deliveries > 0),
            "the bot never delivered; " + await world.OnWorldAsync(() => world.BrainOf(bot).WorldPvp.Description));
        Assert.Equal(1UL, await world.OnWorldAsync(() => world.Zone.TurnInsTotal));
        Assert.False(capture.Any(Key("silithyst-carry-timeout")) || capture.Any(Key("silithyst-unreachable-trigger")), capture.Describe());
    }

    // --- the carry timer ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheCarryTimer_CountsOnlyTheTimeTheCarryRungRuns()
    {
        await using SilithystWorld world = await StartAsync(o => o.WorldPvp.Silithyst.Enabled = false);
        Guid bot = await world.AddBotAsync("Dustrest", 1);
        Assert.True(await world.CastOnSelfAsync(bot, Flag));
        using AnomalyCapture capture = await world.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(world.PlayerOf(bot)));
        (PlayerbotWorldPvpObjectives run, _, Action<uint> advance) = await ControllerAsync(world, bot);

        // No action budget in these steps: the walk never starts (a started route goes on by itself and would deliver in the background).
        Assert.True((await world.OnWorldAsync(() => Step(world, bot, run, budget: 0))).Busy);
        // Ten minutes of rest and combat pass without the rung running: the next step is 200 ms of carry, no timeout.
        advance(PlayerbotWorldPvpObjectives.CarryMaxMs * 2);
        Assert.True((await world.OnWorldAsync(() => Step(world, bot, run, budget: 0))).Busy);
        Assert.True(run.IsCarrying);
        Assert.False(capture.Any(Key("silithyst-carry-timeout")), capture.Describe());

        // The same limit counted in carry time still fires.
        await world.OnWorldAsync(() => Step(world, bot, run, PlayerbotWorldPvpObjectives.CarryMaxMs));
        Assert.True(capture.Any(Key("silithyst-carry-timeout")), capture.Describe());
    }

    // --- the stall watch ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AStalledWorldPvpGoal_EndsTheRun_AndSetsTheGeyserAside()
    {
        await using SilithystWorld world = await StartAsync();
        Guid bot = await world.AddBotAsync("Duststall", 1);
        Assert.True(await world.RunUntilAsync(30_000, () => world.BrainOf(bot).WorldPvp.Active), "the bot never went for the geyser");
        ulong key = GuidOf(Geyser, GeyserSpawn).Value;

        await world.OnWorldAsync(() =>
        {
            PlayerbotBrain brain = world.BrainOf(bot);
            brain.HoldGoalForTests(PlayerbotGoalKind.WorldPvp, Geyser, 0);
            brain.GiveUpStalledGoal(world.PlayerOf(bot));
            Assert.False(brain.WorldPvp.Active, "the run goes on after the stall give-up");
            Assert.Equal("stalled", brain.WorldPvp.LastOutcome);
            Assert.True(brain.WorldPvp.Ledger.IsDeferred(key, world.World.Host.World.NowMs), "the geyser was not set aside");
            return true;
        });

        // The next thinks do not take the geyser again (set aside and cooling down).
        Assert.False(await world.RunUntilAsync(20_000, () => world.BrainOf(bot).WorldPvp.Active), "the bot took the stalled geyser up again");
    }

    [Fact]
    public async Task AStalledCarry_DropsTheFlag_AndDoesNotCarryItAgain()
    {
        await using SilithystWorld world = await StartAsync();
        Guid bot = await world.AddBotAsync("Dustcarstall", 1);
        Assert.True(await world.RunUntilAsync(60_000, () => world.BrainOf(bot).WorldPvp.IsCarrying), "the bot never picked the flag up");

        await world.OnWorldAsync(() =>
        {
            PlayerbotBrain brain = world.BrainOf(bot);
            brain.HoldGoalForTests(PlayerbotGoalKind.WorldPvp, 0, 0);
            world.World.Bots.FindSession(bot)!.ManagedBudget = new ManagedActionBudget(5);
            brain.GiveUpStalledGoal(world.PlayerOf(bot));
            Assert.False(brain.WorldPvp.Active);
            Assert.False(world.HasAura(world.PlayerOf(bot), Flag), "the flag is still on after the give-up");
            return true;
        });

        Assert.False(await world.RunUntilAsync(30_000, () => world.BrainOf(bot).WorldPvp.Active), "the bot started a run again");
    }

    // --- the cool-down --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AfterADelivery_TheBotStartsNoNewRun_UntilTheCooldownHasPassed()
    {
        await using SilithystWorld world = await StartAsync(mound: true);
        Guid bot = await world.AddBotAsync("Dustchain", 1);
        Assert.True(await world.RunUntilAsync(180_000, () => world.BrainOf(bot).WorldPvp.Deliveries > 0), "the bot never delivered");

        // The second object (mound or geyser) is ready and the roll is 1, but one participation window has not passed.
        Assert.False(await world.RunUntilAsync(PlayerbotWorldPvpObjectives.RunCooldownMs - 30_000, () => world.BrainOf(bot).WorldPvp.Active),
            "the bot chained a second run inside the cool-down");
        Assert.Equal(1, await world.OnWorldAsync(() => world.BrainOf(bot).WorldPvp.Pickups));
        Assert.True(await world.RunUntilAsync(60_000, () => world.BrainOf(bot).WorldPvp.Active), "the bot never went for the second object after the cool-down");
    }

    // --- the delivery count -------------------------------------------------------------------------------------------

    [Fact]
    public async Task AStaleTracesAura_IsNotCountedAsADelivery()
    {
        await using SilithystWorld world = await StartAsync(o => o.WorldPvp.Silithyst.Enabled = false);
        Guid bot = await world.AddBotAsync("Duststale", 1, at: AllianceTrigger + new Vector3(6f, 0f, 0f));
        Assert.True(await world.CastOnSelfAsync(bot, Traces));
        Assert.True(await world.CastOnSelfAsync(bot, Flag));
        (PlayerbotWorldPvpObjectives run, _, Action<uint> advance) = await ControllerAsync(world, bot);

        Assert.True((await world.OnWorldAsync(() => Step(world, bot, run))).Busy);
        Assert.True(run.IsCarrying);
        // The flag goes near the trigger without the trigger having fired: Traces of Silithyst is the one from before.
        await world.OnWorldAsync(() => { world.RemoveAura(world.PlayerOf(bot), Flag); return true; });
        for (int i = 0; i < 4 && run.Active; i++)
        {
            advance(1_000);
            await world.OnWorldAsync(() => Step(world, bot, run));
        }

        Assert.False(run.Active);
        Assert.Equal(0, run.Deliveries);
        Assert.Equal("lost", run.LastOutcome);
    }

    [Fact]
    public async Task AFlagAppliedTwice_IsOnePickup()
    {
        await using SilithystWorld world = await StartAsync(o => o.WorldPvp.Silithyst.Enabled = false);
        Guid bot = await world.AddBotAsync("Dusttwice", 1);
        Assert.True(await world.CastOnSelfAsync(bot, Flag));
        Assert.True(await world.CastOnSelfAsync(bot, Flag));

        Assert.Equal(1UL, await world.OnWorldAsync(() => world.Zone.PickupsTotal));
    }

    // --- the escape ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARogueCarryingSilithyst_DoesNotChooseVanish(bool carries)
    {
        var rogue = new EscapeSituation(Class.Rogue, 30f, true, AttackersWithin8: 2, AttackersWithin10: 2, VictimInMelee: true, HasVictim: true, InCatForm: false,
            CarriesSilithyst: carries);

        Assert.Equal(carries ? PlayerbotEscapes.Gouge : PlayerbotEscapes.Vanish, PlayerbotEscapes.Choose(rogue, _ => true)?.Spell);
        Assert.Equal(carries, !PlayerbotEscapes.Candidates(rogue).Any(a => a.Spell == PlayerbotEscapes.Vanish));
    }
}

using ArcaneCore.Game.GameObjects;
using ArcaneCore.World.Playerbots.Combat;
using Microsoft.Extensions.DependencyInjection;
using ArcaneCore.World.Spells;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Spells;
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

    /// <summary>Only the Horde turn-in trigger (4168) stays: an Alliance carrier has no trigger and gives up, a Horde one delivers.</summary>
    private static Task KeepOnlyHordeTriggerAsync(SilithystWorld world) => world.OnWorldAsync(() =>
    {
        Game.Maps.Templates.WorldMaps maps = Game.Maps.Templates.WorldMaps.Of(world.World.Host.World);
        maps.ReplaceAreaTriggerTables(maps.BuildAreaTriggerTables(GridTerrain.InMemoryMapDataStore.Content with
        {
            AreaTriggers = [new ArcaneCore.Kernel.WorldData.AreaTriggerTemplate(SilithusCatalog.AreaTriggerHorde, 1, HordeTrigger.X, HordeTrigger.Y, HordeTrigger.Z, 4f, 0, 0, 0, 0, "Silithyst Horde")],
            AreaTriggerTeleports = [],
        }));
        return true;
    });

    /// <summary>
    /// The real give-up of the brain's own controller (the option may be off: a held flag is taken up anyway): no trigger, one report, CMSG_CANCEL_AURA,
    /// and the server's drop (29533) puts a Silithyst Mound at the bot's feet. Returns that mound.
    /// </summary>
    private static async Task<GameObject> GiveUpAndDropAsync(SilithystWorld world, Guid bot, AnomalyCapture capture)
    {
        Assert.True(await world.CastOnSelfAsync(bot, Flag));
        Assert.True(await world.RunUntilAsync(10_000, () => world.BrainOf(bot).WorldPvp.LastOutcome == "no-trigger"),
            "the carry was not given up; " + await world.OnWorldAsync(() => world.BrainOf(bot).WorldPvp.Description));
        Assert.Equal(1, capture.Count(Key("silithyst-unreachable-trigger")));
        Assert.False(await world.OnWorldAsync(() => world.HasAura(world.PlayerOf(bot), Flag)), "the flag was not cancelled");
        GameObject mound = await world.OnWorldAsync(() =>
            world.PlayerOf(bot).Map!.FindUpdater<GameObjectMapSystem>()!.GameObjects.Single(g => g.Entry == Mound));
        await world.OnWorldAsync(() =>
        {
            Assert.True(mound.IsSpawned && mound.LootState == GameObjectLootState.Ready && (mound.Flags & GameObjectFlags.InUse) == 0, "the dropped mound is not ready");
            Assert.True(Vector3.Distance(new Vector3(mound.X, mound.Y, mound.Z), Start) <= 2.5f, $"the mound is not at the bot's feet: {mound.X},{mound.Y},{mound.Z}");
            return true;
        });
        return mound;
    }

    private static async Task<(GameObject Mound, AnomalyCapture Capture, uint T0)> DroppedMoundSetupAsync(SilithystWorld world, Guid bot)
    {
        await KeepOnlyHordeTriggerAsync(world);
        AnomalyCapture capture = await world.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(world.PlayerOf(bot)));
        GameObject mound = await GiveUpAndDropAsync(world, bot, capture);
        await world.OnWorldAsync(() => { world.Options.WorldPvp.Silithyst.Enabled = true; return true; });
        uint t0 = await world.OnWorldAsync(() => world.World.Host.World.NowMs);
        return (mound, capture, t0);
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
    public async Task AFlagThatCannotBeCancelled_IsTakenUpAgainAfterTheBackoff_AndReportsAtMostOncePerBackoff()
    {
        await using SilithystWorld world = await StartAsync(o => o.WorldPvp.Silithyst.Enabled = false);
        Guid bot = await world.AddBotAsync("Dustbackoff", 1);
        Assert.True(await world.CastOnSelfAsync(bot, Flag));
        await RemoveTriggersAsync(world);
        using AnomalyCapture capture = await world.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(world.PlayerOf(bot)));
        (PlayerbotWorldPvpObjectives run, _, Action<uint> advance) = await ControllerAsync(world, bot);

        await world.OnWorldAsync(() => Step(world, bot, run, budget: 0));
        Assert.Equal(1, capture.Count(Key("silithyst-unreachable-trigger")));

        // Inside the back-off the flag is not taken up; just after it the same flag is carried again (nothing took it off) and the fault is a new one.
        advance(PlayerbotWorldPvpObjectives.GiveUpBackoffMs - 1);
        await world.OnWorldAsync(() => Step(world, bot, run, budget: 0));
        Assert.Equal(1, capture.Count(Key("silithyst-unreachable-trigger")));
        Assert.False(run.Active);
        advance(2);
        await world.OnWorldAsync(() => Step(world, bot, run, budget: 0));
        Assert.Equal(2, capture.Count(Key("silithyst-unreachable-trigger")));
        Assert.False(run.Active);

        for (int think = 0; think < 20; think++)
        {
            advance(200);
            WorldPvpStep again = await world.OnWorldAsync(() => Step(world, bot, run, budget: 0));
            Assert.False(again.Busy, $"think {think}: the bot took the flag up again");
            Assert.False(run.Active, $"think {think}: a new episode opened");
        }

        Assert.Equal(2, capture.Count(Key("silithyst-unreachable-trigger")));
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

    /// <summary>
    /// Stalled() starts the plain run cool-down after the give-up's ten-minute hold-off; the cool-down only ever extends. Without that, a stalled carry
    /// would be seeking again after two minutes, while its own dropped mound (180 s) still stands at its feet.
    /// </summary>
    [Fact]
    public async Task AStalledCarry_HoldsTheBotOffLongerThanTheRunCooldown_SoItDoesNotTakeItsOwnMoundUp()
    {
        await using SilithystWorld world = await StartAsync();
        Guid bot = await world.AddBotAsync("Dustcarhold", 1);
        Assert.True(await world.RunUntilAsync(60_000, () => world.BrainOf(bot).WorldPvp.IsCarrying), "the bot never picked the flag up");
        Assert.Equal(1, await world.OnWorldAsync(() => world.BrainOf(bot).WorldPvp.Pickups));

        await world.OnWorldAsync(() =>
        {
            PlayerbotBrain brain = world.BrainOf(bot);
            brain.HoldGoalForTests(PlayerbotGoalKind.WorldPvp, 0, 0);
            world.World.Bots.FindSession(bot)!.ManagedBudget = new ManagedActionBudget(5);
            brain.GiveUpStalledGoal(world.PlayerOf(bot));
            Assert.False(world.HasAura(world.PlayerOf(bot), Flag), "the flag is still on after the give-up");
            return true;
        });

        Assert.False(await world.RunUntilAsync(PlayerbotWorldPvpObjectives.RunCooldownMs + 20_000,
            () => world.BrainOf(bot).WorldPvp.Active || world.BrainOf(bot).WorldPvp.Pickups > 1), "the bot sought again inside the give-up hold-off");
    }

    /// <summary>The negative side: a stall before the carry (no flag held) only costs the plain cool-down, not the ten-minute hold-off.</summary>
    [Fact]
    public async Task AStalledGoalBeforeTheCarry_StartsNoTenMinuteHoldOff()
    {
        await using SilithystWorld world = await StartAsync();
        Guid bot = await world.AddBotAsync("Dustnohold", 1);
        Assert.True(await world.RunUntilAsync(30_000, () => world.BrainOf(bot).WorldPvp.Active), "the bot never went for the geyser");
        Assert.False(world.BrainOf(bot).WorldPvp.IsCarrying);
        await world.OnWorldAsync(() =>
        {
            world.BrainOf(bot).HoldGoalForTests(PlayerbotGoalKind.WorldPvp, Geyser, 0);
            world.BrainOf(bot).GiveUpStalledGoal(world.PlayerOf(bot));
            return true;
        });

        // The geyser is set aside (60 s to a minute), the cool-down is two minutes: a run is back well inside the ten-minute hold-off a carry give-up costs.
        Assert.True(await world.RunUntilAsync(PlayerbotWorldPvpObjectives.GiveUpBackoffMs - 60_000, () => world.BrainOf(bot).WorldPvp.Active),
            "a give-up before the carry held the bot off like a carry give-up");
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

    // --- the geyser cache -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task AGeyserInAGridThatLoadsAfterTheFirstSeek_IsFoundWithoutWaitingForACacheRenewal()
    {
        await using SilithystWorld world = await StartAsync(farGeyser: true);
        Guid bot = await world.AddBotAsync("Dustfar", 1);
        ulong near = GuidOf(Geyser, GeyserSpawn).Value;
        await world.OnWorldAsync(() => { world.BrainOf(bot).WorldPvp.Ledger.Defer(near, world.World.Host.World.NowMs); return true; });

        // The first seek reads the geysers of the map: the near one is set aside, the far one's grid is not loaded.
        Assert.False(await world.RunUntilAsync(10_000, () => world.BrainOf(bot).WorldPvp.Active), "the bot went for a geyser it should not have");
        Assert.Null(await world.OnWorldAsync(() => world.PlayerOf(bot).Map!.FindObject(GuidOf(Geyser, FarGeyserSpawn))));

        await world.MoveBotAsync(bot, FarGeyserAt - new Vector3(30f, 0f, 0f));
        Assert.True(await world.RunUntilAsync(30_000, () => world.BrainOf(bot).WorldPvp.ActiveKey == GuidOf(Geyser, FarGeyserSpawn).Value || world.BrainOf(bot).WorldPvp.Pickups > 0),
            "the bot never found the geyser whose grid loaded");
    }

    // --- a turn-in the server never takes -----------------------------------------------------------------------------

    [Fact]
    public async Task ATurnInTheServerRefuses_IsReportedOnce_AfterThreeEntries_AndTheFlagIsCancelled()
    {
        await using SilithystWorld world = await StartAsync();
        // Not in the zone script: the area trigger handler ignores the bot, so the flag is never taken.
        Guid bot = await world.AddBotAsync("Dustrefuse", 1);
        // In zone 1377 (so the bot is watched) but taken out of the script: it left the script's zone without the player's zone field changing.
        await world.OnWorldAsync(() => { world.PlayerOf(bot).ZoneId = SilithusCatalog.SilithusZone; return true; });
        await world.RunUntilAsync(1_000, () => false); // the zone updater notices the zone and enters the bot into the script
        await world.OnWorldAsync(() => { world.Pvp.OnZoneChanged(world.PlayerOf(bot), SilithusCatalog.SilithusZone, 17, 0, null); return true; });
        Assert.True(await world.CastOnSelfAsync(bot, Flag));
        using AnomalyCapture capture = await world.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(world.PlayerOf(bot)));

        Assert.True(await world.RunUntilAsync(180_000, () => world.BrainOf(bot).WorldPvp.LastOutcome == "no-turn-in"),
            "the refused turn-in was not given up; " + await world.OnWorldAsync(() => world.BrainOf(bot).WorldPvp.Description + " last=" + world.BrainOf(bot).WorldPvp.LastOutcome + " pickups=" + world.BrainOf(bot).WorldPvp.Pickups) + " " + capture.Describe());
        Assert.Equal(1, capture.Count(Key("silithyst-unreachable-trigger")));
        Assert.Equal(0, await world.OnWorldAsync(() => world.BrainOf(bot).WorldPvp.Deliveries));
        Assert.Equal(0UL, await world.OnWorldAsync(() => world.Zone.TurnInsTotal));
        Assert.False(await world.OnWorldAsync(() => world.HasAura(world.PlayerOf(bot), Flag)), "the flag was not cancelled");

        await world.RunUntilAsync(20_000, () => false);
        Assert.Equal(1, capture.Count(Key("silithyst-unreachable-trigger")));
        Assert.False(await world.OnWorldAsync(() => world.BrainOf(bot).WorldPvp.Active));
    }

    // --- the flag on the spell rows of the real data ---------------------------------------------------------------

    [Fact]
    public async Task AGivenUpCarry_CancelsTheFlag_OnTheSpellRowsOfTheRealData()
    {
        await using SilithystWorld world = await StartAsync(o => o.WorldPvp.Silithyst.Enabled = false, realFlag: true);
        Guid bot = await world.AddBotAsync("Dustreal", 1);
        Assert.True(await world.CastOnSelfAsync(bot, Flag));
        Assert.True(await world.OnWorldAsync(() => world.HasAura(world.PlayerOf(bot), Flag)), "the real flag rows did not apply");
        await RemoveTriggersAsync(world);
        (PlayerbotWorldPvpObjectives run, _, Action<uint> advance) = await ControllerAsync(world, bot);

        await world.OnWorldAsync(() => Step(world, bot, run));
        Assert.Equal("no-trigger", run.LastOutcome);
        Assert.False(await world.OnWorldAsync(() => world.HasAura(world.PlayerOf(bot), Flag)), "the flag of the real rows cannot be cancelled");
        advance(200);
        Assert.False((await world.OnWorldAsync(() => Step(world, bot, run))).Busy);
    }

    // --- a flag with no controller ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ABotHoldingTheFlagWithTheOptionOff_IsStillTakenUp_AndDelivers()
    {
        await using SilithystWorld world = await StartAsync(o => o.WorldPvp.Silithyst.Enabled = false);
        Guid bot = await world.AddBotAsync("Dustgm", 1);
        Assert.True(await world.CastOnSelfAsync(bot, Flag));

        Assert.True(await world.RunUntilAsync(120_000, () => world.Zone.TurnInsTotal > 0), "a flag the bot holds with the option off was never turned in");
    }

    // --- the retreat's view of the bot -------------------------------------------------------------------------------------

    [Fact]
    public async Task TheRetreatsSituation_SaysARogueCarriesTheFlag_AndItsEscapeSkipsVanish()
    {
        await using SilithystWorld world = await StartAsync(o => o.WorldPvp.Silithyst.Enabled = false);
        Guid bot = await world.AddBotAsync("Dustrogue", 1, cls: 4);
        Assert.True(await world.CastOnSelfAsync(bot, Flag));

        await world.OnWorldAsync(() =>
        {
            Player player = world.PlayerOf(bot);
            EscapeSituation carrying = PlayerbotRetreat.SituationOf(player, [], out _) with { InCombat = true, HasVictim = true, VictimInMelee = true };
            Assert.Equal(Class.Rogue, carrying.Class);
            Assert.True(carrying.CarriesSilithyst);
            Assert.NotEqual(PlayerbotEscapes.Vanish, PlayerbotEscapes.Choose(carrying, _ => true)?.Spell);
            world.RemoveAura(player, Flag);
            EscapeSituation free = PlayerbotRetreat.SituationOf(player, [], out _) with { InCombat = true, HasVictim = true, VictimInMelee = true };
            Assert.False(free.CarriesSilithyst);
            Assert.Equal(PlayerbotEscapes.Vanish, PlayerbotEscapes.Choose(free, _ => true)?.Spell);
            return true;
        });
    }

    // --- the per-think aura view ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ABotHoldingTheFlagOutsideTheZone_IsNotTakenUp_AndWalksNoAuras()
    {
        await using SilithystWorld world = await StartAsync(o => o.WorldPvp.Silithyst.Enabled = false);
        Guid bot = await world.AddBotAsync("Dustoutside", 1, inZone: false);
        Assert.True(await world.CastOnSelfAsync(bot, Flag));

        await world.RunUntilAsync(5_000, () => false);
        Assert.Equal(0, await world.OnWorldAsync(() => world.BrainOf(bot).SilithystScans));
        Assert.NotEqual(PlayerbotGoalKind.WorldPvp, await world.OnWorldAsync(() => world.BrainOf(bot).Goal));
        Assert.Equal(0UL, await world.OnWorldAsync(() => world.Zone.TurnInsTotal));

        long thinksAtSwitch = await world.OnWorldAsync(() =>
        {
            world.RemoveAura(world.PlayerOf(bot), Flag); // no flag in the zone, so neither rung takes the bot up and both reach the view
            world.PlayerOf(bot).ZoneId = SilithusCatalog.SilithusZone;
            return world.BrainOf(bot).ThinkCount;
        });
        await world.RunUntilAsync(5_000, () => false);
        await world.OnWorldAsync(() =>
        {
            PlayerbotBrain brain = world.BrainOf(bot);
            Assert.True(brain.SilithystScans > 0, "no aura walk in the zone");
            // ThinkCount ticks every update (50 ms); a think is every 200 ms (ThinkIntervalMs): 25 in 5 s, one walk each, shared by both rungs (two would be ~50).
            Assert.InRange(brain.SilithystScans, 20, 26);
            Assert.True(brain.ThinkCount > thinksAtSwitch);
            return true;
        });
    }

    [Fact]
    public async Task Scan_FindsTheFlagAndTheLiveTracesInOnePass()
    {
        await using SilithystWorld world = await StartAsync(o => o.WorldPvp.Silithyst.Enabled = false);
        Guid bot = await world.AddBotAsync("Dustscan", 1);
        Assert.True(await world.CastOnSelfAsync(bot, Flag));
        Assert.True(await world.CastOnSelfAsync(bot, Traces));

        await world.OnWorldAsync(() =>
        {
            WorldSession session = world.World.Bots.FindSession(bot)!;
            SilithystAuras both = PlayerbotWorldPvpObjectives.Scan(session, world.PlayerOf(bot));
            Assert.True(both.Carries);
            Assert.Equal(Traces, both.Traces?.Spell.Id);
            world.RemoveAura(world.PlayerOf(bot), Flag);
            SilithystAuras onlyTraces = PlayerbotWorldPvpObjectives.Scan(session, world.PlayerOf(bot));
            Assert.False(onlyTraces.Carries);
            Assert.Equal(Traces, onlyTraces.Traces?.Spell.Id);
            return true;
        });
    }

    // --- a mount cast the budget cannot cancel ----------------------------------------------------------------------------

    [Fact]
    public async Task AMountCastTheBudgetCannotCancel_IsCancelledAtTheNextThink()
    {
        await using SilithystWorld world = await StartAsync(o => o.WorldPvp.Silithyst.Enabled = false);
        Guid bot = await world.AddBotAsync("Dustmount", 1);
        (PlayerbotWorldPvpObjectives run, _, Action<uint> advance) = await ControllerAsync(world, bot);

        await world.OnWorldAsync(() =>
        {
            const uint summonWarhorse = 13819;
            SpellFeature feature = world.World.Services.GetRequiredService<SpellFeature>();
            feature.System.Store = new SpellStore([.. feature.System.Store.All.Where(s => s.Id != summonWarhorse), new SpellInfo
            {
                Id = summonWarhorse,
                Name = "Summon Warhorse",
                CastTime = new SpellCastTime(3000, 0, 3000),
                Duration = new SpellDuration(-1, 0, -1),
                Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.Mounted, MiscValue = 97199, TargetA = SpellImplicitTarget.UnitCaster }, new(), new()],
            }], [], []);
            Player player = world.PlayerOf(bot);
            WorldSession session = world.World.Bots.FindSession(bot)!;
            Assert.True(feature.Spellbook.LearnSpell(player, summonWarhorse));
            var options = new PlayerbotOptions { Enabled = true };
            var route = new PlayerbotRoute([new Vector3(player.X, player.Y, player.Z), new Vector3(player.X + 200f, player.Y, player.Z)], 200f);

            session.ManagedBudget = new ManagedActionBudget(5);
            Assert.True(PlayerbotMount.BeforeStep(session, player, route, options));
            Assert.True(PlayerbotMount.IsBusy(session), "the mount-up cast did not start");

            // The flag landed during the cast and no action is left this think: the cast must stay known and be cancelled at the next think.
            session.ManagedBudget = new ManagedActionBudget(0);
            Assert.True(run.Update(player, 200, false, new SilithystAuras(true, null)).Busy);
            Assert.True(PlayerbotMount.IsBusy(session), "the mount-up cast was forgotten without being cancelled");
            Assert.True(feature.System.GetState(player.Guid)?.CurrentCast is { State: SpellCastState.Preparing or SpellCastState.Casting });

            session.ManagedBudget = new ManagedActionBudget(5);
            advance(200);
            run.Update(player, 200, false, new SilithystAuras(true, null));
            Assert.False(PlayerbotMount.IsBusy(session));
            feature.System.Update(3000);
            Assert.False(MountService.IsMounted(player), "the cancelled mount-up cast still landed");
            return true;
        });
    }

    [Fact]
    public async Task ANewFlagAfterAGiveUp_IsNotTakenUpInsideTheBackoff_AndReportsAtMostOnceMoreAfterIt()
    {
        await using SilithystWorld world = await StartAsync(o => o.WorldPvp.Silithyst.Enabled = false);
        Guid bot = await world.AddBotAsync("Dustagain", 1);
        Assert.True(await world.CastOnSelfAsync(bot, Flag));
        await RemoveTriggersAsync(world);
        using AnomalyCapture capture = await world.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(world.PlayerOf(bot)));
        (PlayerbotWorldPvpObjectives run, _, Action<uint> advance) = await ControllerAsync(world, bot);

        await world.OnWorldAsync(() => Step(world, bot, run));
        Assert.Equal(1, capture.Count(Key("silithyst-unreachable-trigger")));
        Assert.False(await world.OnWorldAsync(() => world.HasAura(world.PlayerOf(bot), Flag)), "the flag was not cancelled");

        // A new flag after the run cool-down (a mound the give-up dropped): still inside the back-off, so it is not taken up and nothing is reported.
        advance(PlayerbotWorldPvpObjectives.RunCooldownMs + 1);
        await world.OnWorldAsync(() => Step(world, bot, run));
        Assert.True(await world.CastOnSelfAsync(bot, Flag));
        await world.OnWorldAsync(() => Step(world, bot, run));
        Assert.Equal(1, capture.Count(Key("silithyst-unreachable-trigger")));
        Assert.False(run.Active);

        // After the back-off the flag is carried again, and the fault is reported once more.
        advance(PlayerbotWorldPvpObjectives.GiveUpBackoffMs);
        await world.OnWorldAsync(() => Step(world, bot, run));
        Assert.Equal(2, capture.Count(Key("silithyst-unreachable-trigger")));
        Assert.False(run.Active);
        Assert.False(await world.OnWorldAsync(() => world.HasAura(world.PlayerOf(bot), Flag)), "the flag was not cancelled");
    }

    // --- the mound a give-up drops -----------------------------------------------------------------------------------------

    [Fact]
    public async Task AGiveUpThatDroppedAMound_DoesNotTakeItUpAgain_WithinTheBackoff()
    {
        await using SilithystWorld world = await StartAsync(o => o.WorldPvp.Silithyst.Enabled = false);
        Guid bot = await world.AddBotAsync("Dustleaver", 1);
        (GameObject mound, AnomalyCapture capture, _) = await DroppedMoundSetupAsync(world, bot);
        using AnomalyCapture owned = capture;

        Assert.False(await world.RunUntilAsync(PlayerbotWorldPvpObjectives.RunCooldownMs + 20_000,
            () => world.BrainOf(bot).WorldPvp.Active || world.BrainOf(bot).WorldPvp.Pickups > 0), "the bot took its own mound up again");
        Assert.True(await world.OnWorldAsync(() => mound.IsSpawned && mound.LootState == GameObjectLootState.Ready), "the mound was gone before the cool-down ended");
        Assert.Equal(1, capture.Count(Key("silithyst-unreachable-trigger")));
        Assert.Equal(1UL, await world.OnWorldAsync(() => world.Zone.PickupsTotal));
    }

    [Fact]
    public async Task AfterTheBackoff_ACarryFaultIsReportedAtMostOnceMore()
    {
        await using SilithystWorld world = await StartAsync(o => o.WorldPvp.Silithyst.Enabled = false);
        Guid bot = await world.AddBotAsync("Dustwaiter", 1);
        (_, AnomalyCapture capture, uint t0) = await DroppedMoundSetupAsync(world, bot);
        using AnomalyCapture owned = capture;

        // The dropped mound stands 3 minutes, so it is gone long before the back-off ends: the bot is idle for all of it (nothing to take up).
        Assert.False(await world.RunUntilAsync(PlayerbotWorldPvpObjectives.GiveUpBackoffMs,
            () => world.BrainOf(bot).WorldPvp.Active || capture.Count(Key("silithyst-unreachable-trigger")) > 1), "the bot started a carry inside the back-off; " + capture.Describe());

        // A new flag after the back-off is carried again and reports the fault once more.
        Assert.True(await world.CastOnSelfAsync(bot, Flag));
        Assert.True(await world.RunUntilAsync(60_000,
            () => capture.Count(Key("silithyst-unreachable-trigger")) >= 2), "no second report after the back-off; " + capture.Describe());
        uint t1 = await world.OnWorldAsync(() => world.World.Host.World.NowMs);
        Assert.True(unchecked(t1 - t0) >= PlayerbotWorldPvpObjectives.GiveUpBackoffMs, $"the second report came {t1 - t0} ms after the give-up, inside the back-off");
        Assert.Equal(2, capture.Count(Key("silithyst-unreachable-trigger")));
        Assert.Equal("no-trigger", await world.OnWorldAsync(() => world.BrainOf(bot).WorldPvp.LastOutcome));
        Assert.Equal(0, await world.OnWorldAsync(() => world.BrainOf(bot).WorldPvp.Pickups));
        Assert.False(await world.RunUntilAsync(PlayerbotWorldPvpObjectives.RunCooldownMs + 20_000,
            () => world.BrainOf(bot).WorldPvp.Active || capture.Count(Key("silithyst-unreachable-trigger")) > 2), "a third carry inside the new back-off");
        Assert.Equal(2, capture.Count(Key("silithyst-unreachable-trigger")));
    }

    [Fact]
    public async Task AnotherBot_MayTakeTheMoundADroppedCarryLeft()
    {
        await using SilithystWorld world = await StartAsync(o => o.WorldPvp.Silithyst.Enabled = false);
        Guid bot = await world.AddBotAsync("Dustleaver", 1);
        (GameObject mound, AnomalyCapture capture, _) = await DroppedMoundSetupAsync(world, bot);
        using AnomalyCapture owned = capture;

        Guid taker = await world.AddBotAsync("Dusttaker", 2, at: Start + new Vector3(-20f, 0f, 0f));
        Assert.True(await world.RunUntilAsync(30_000, () => world.BrainOf(taker).WorldPvp.ActiveKey == mound.Guid.Value || world.BrainOf(taker).WorldPvp.Pickups > 0),
            "the other bot never went for the dropped mound");
        Assert.True(await world.RunUntilAsync(180_000, () => world.BrainOf(taker).WorldPvp.Deliveries > 0),
            "the other bot never delivered; " + await world.OnWorldAsync(() => world.BrainOf(taker).WorldPvp.Description));
        Assert.Equal(1UL, await world.OnWorldAsync(() => world.Zone.TurnInsTotal));
        Assert.Equal(1u, await world.OnWorldAsync(() => world.Zone.GatheredHorde));
        Assert.False(await world.RunUntilAsync(PlayerbotWorldPvpObjectives.RunCooldownMs + 20_000,
            () => world.BrainOf(bot).WorldPvp.Active || world.BrainOf(bot).WorldPvp.Pickups > 0), "the dropper started a run inside its back-off");
        Assert.Equal(1, capture.Count(Key("silithyst-unreachable-trigger")));
    }

    // --- a carrier outside the zone ------------------------------------------------------------------------------------------

    [Fact]
    public async Task ACarrierOutsideZone1377_MayRide_AndIsNotCancelledAtEveryThink()
    {
        await using SilithystWorld world = await StartAsync(o => o.WorldPvp.Silithyst.Enabled = false);
        Guid bot = await world.AddBotAsync("Dustride", 1, at: SilithystWorld.Start + new Vector3(250f, 0f, 0f)); // a long way from the trigger (the mount-up needs 80 yards of route)
        Assert.True(await world.CastOnSelfAsync(bot, Flag));
        (PlayerbotWorldPvpObjectives run, _, Action<uint> advance) = await ControllerAsync(world, bot);

        await world.OnWorldAsync(() =>
        {
            const uint summonWarhorse = 13819;
            SpellFeature feature = world.World.Services.GetRequiredService<SpellFeature>();
            feature.System.Store = new SpellStore([.. feature.System.Store.All.Where(s => s.Id != summonWarhorse), new SpellInfo
            {
                Id = summonWarhorse,
                Name = "Summon Warhorse",
                CastTime = new SpellCastTime(3000, 0, 3000),
                Duration = new SpellDuration(-1, 0, -1),
                Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.Mounted, MiscValue = 97199, TargetA = SpellImplicitTarget.UnitCaster }, new(), new()],
            }], [], []);
            Player player = world.PlayerOf(bot);
            WorldSession session = world.World.Bots.FindSession(bot)!;
            Assert.True(feature.Spellbook.LearnSpell(player, summonWarhorse));

            Assert.True(Step(world, bot, run).Busy, "the carrier was not taken up"); // in zone 1377: no mount, the server would drop the dust
            player.ZoneId = 17; // the carry goes on elsewhere on the map: the server drops nothing there, so the carrier may ride
            string log = "";
            int casts = 0;
            object? lastCast = null;
            for (int think = 0; think < 14; think++) // the mount-up cast takes 3 s: 15 thinks of 200 ms
            {
                advance(200);
                Assert.True(Step(world, bot, run).Busy, $"think {think}: the carry ended ({run.LastOutcome})");
                feature.System.Update(200);
                object? cast = feature.System.GetState(player.Guid)?.CurrentCast;
                if (cast is not null && !ReferenceEquals(cast, lastCast)) casts++; // a cancel and a recast show as a new cast
                lastCast = cast ?? lastCast;
                log += cast is not null ? "B" : ".";
            }

            Assert.True(casts == 1, "the carrier's mount-up cast was started " + casts + " times (cancelled and recast); log=" + log);
            Assert.True(log.Count(c => c == 'B') >= 13, "the carrier did not stay in its mount-up cast; log=" + log);
            return true;
        });
    }

    [Fact]
    public async Task ACarryEndedByAMapChange_EndsQuietly_AndTheFlagIsCancelledOnce()
    {
        await using SilithystWorld world = await StartAsync(o => o.WorldPvp.Silithyst.Enabled = false);
        Guid bot = await world.AddBotAsync("Dustmapchg", 1);
        Assert.True(await world.CastOnSelfAsync(bot, Flag));
        using AnomalyCapture capture = await world.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(world.PlayerOf(bot)));
        (PlayerbotWorldPvpObjectives run, _, Action<uint> advance) = await ControllerAsync(world, bot);

        Assert.True((await world.OnWorldAsync(() => Step(world, bot, run, budget: 0))).Busy, "the carrier was not taken up");
        Assert.True(run.NeedsAuras);
        // The brain skips the think of a map change; the first think on the other map finds the run still open.
        await world.OnWorldAsync(() => { world.PlayerOf(bot).MapId = 0; return true; });
        advance(200);
        Assert.False((await world.OnWorldAsync(() => Step(world, bot, run))).Busy);
        Assert.False(run.Active);
        Assert.Equal("ended", run.LastOutcome);
        Assert.Equal(0, capture.Count(Key("silithyst-unreachable-trigger")));
        Assert.False(await world.OnWorldAsync(() => world.HasAura(world.PlayerOf(bot), Flag)), "the flag was not cancelled");

        // Nothing is left to watch: the brain stops its aura walks.
        advance(200);
        Assert.False((await world.OnWorldAsync(() => Step(world, bot, run))).Busy);
        Assert.False(run.NeedsAuras);
        Assert.Equal(0, capture.Count(Key("silithyst-unreachable-trigger")));
    }

    // --- the motion blacklist ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Unblacklist_LiftsOnlyThePlaceItIsGiven_AndAllocatesNothing()
    {
        await using SilithystWorld world = await StartAsync(o => o.WorldPvp.Silithyst.Enabled = false);
        Guid bot = await world.AddBotAsync("Dustblack", 1);

        await world.OnWorldAsync(() =>
        {
            Player player = world.PlayerOf(bot);
            var a = new Vector3(100f, 100f, 0f);
            var b = new Vector3(120f, 100f, 0f);
            PlayerbotMotion.Blacklist(player, [a, b], world.World.Host.World.NowMs);
            Assert.True(PlayerbotMotion.IsBlacklisted(player, a) && PlayerbotMotion.IsBlacklisted(player, b));
            PlayerbotMotion.Unblacklist(player, a);
            Assert.False(PlayerbotMotion.IsBlacklisted(player, a));
            Assert.True(PlayerbotMotion.IsBlacklisted(player, b));

            PlayerbotMotion.Unblacklist(player, a);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) PlayerbotMotion.Unblacklist(player, a);
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
            return true;
        });
    }

    // --- the combat view -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1377, true, true)]
    [InlineData(17, true, false)]
    [InlineData(1377, false, false)]
    public async Task TheCombatView_SaysNoStealth_OnlyWhileCarryingInZone1377(uint zone, bool holdsFlag, bool expected)
    {
        await using SilithystWorld world = await StartAsync(o => o.WorldPvp.Silithyst.Enabled = false);
        Guid bot = await world.AddBotAsync("Dustview" + (zone == 1377 ? "a" : "b") + (holdsFlag ? "f" : "n"), 1, cls: 4);
        if (holdsFlag) Assert.True(await world.CastOnSelfAsync(bot, Flag));

        await world.OnWorldAsync(() =>
        {
            Player player = world.PlayerOf(bot);
            player.ZoneId = zone;
            RotationState view = PlayerbotCombatView.Build(world.World.Bots.FindSession(bot)!, world.World.Services.GetRequiredService<SpellFeature>(), player,
                null, PlayerbotAbilities.Empty, PlayerbotRole.Healer, static _ => false);
            Assert.Equal(expected, view.NoStealth);
            return true;
        });
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

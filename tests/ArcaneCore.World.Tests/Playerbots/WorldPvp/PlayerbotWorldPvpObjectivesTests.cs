using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Locomotion;
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
/// The Silithyst run of a bot (<see cref="PlayerbotWorldPvpObjectives"/>) in a world with the real outdoor PvP script, goober use, spell
/// scripts and area trigger handler (docs/areas/playbots-social.md, World PvP: Silithyst): a bot in Silithus at level 55 walks to the geyser, uses it for
/// the flag (29519), walks to its faction's turn-in trigger, and the server counts the delivery and leaves Traces of Silithyst (29534).
/// </summary>
public sealed class PlayerbotWorldPvpObjectivesTests
{
    private const uint Flag = SilithusCatalog.SpellSilithystFlag, Traces = SilithusCatalog.SpellTracesOfSilithyst;

    private static string Key(string code) => PlayerbotAnomalies.Key("world-pvp", code);

    private static async Task<bool> RunUntilDeliveredAsync(SilithystWorld world, Guid bot, uint maxMs = 180_000)
        => await world.RunUntilAsync(maxMs, () => world.BrainOf(bot).WorldPvp.Deliveries > 0);

    // --- the run ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData((byte)1)]
    [InlineData((byte)2)]
    public async Task ABotNearAGeyser_PicksUpTheFlag_AndDeliversItToItsOwnCamp(byte race)
    {
        await using SilithystWorld world = await StartAsync();
        Guid bot = await world.AddBotAsync(race == 1 ? "Dustrunnera" : "Dustrunnerh", race);
        using AnomalyCapture capture = await world.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(world.PlayerOf(bot)));

        Assert.True(await RunUntilDeliveredAsync(world, bot), "the bot never delivered; " + await world.OnWorldAsync(() => world.BrainOf(bot).WorldPvp.Description));

        bool alliance = race == 1;
        Assert.Equal(alliance ? 1u : 0u, await world.OnWorldAsync(() => world.Zone.GatheredAlliance));
        Assert.Equal(alliance ? 0u : 1u, await world.OnWorldAsync(() => world.Zone.GatheredHorde));
        Assert.Equal(1UL, await world.OnWorldAsync(() => world.Zone.TurnInsTotal));
        Assert.Equal(1UL, await world.OnWorldAsync(() => world.Zone.PickupsTotal));
        await world.OnWorldAsync(() =>
        {
            Player player = world.PlayerOf(bot);
            Assert.True(world.HasAura(player, Traces), "Traces of Silithyst was not applied");
            Assert.False(world.HasAura(player, Flag), "the flag is still on after the turn-in");
            Assert.False(world.BrainOf(bot).WorldPvp.Active);
            Assert.Equal("delivered", world.BrainOf(bot).WorldPvp.LastOutcome);
            return true;
        });

        // The goal ends: the next thinks are the bot's own again.
        await world.RunUntilAsync(2_000, () => false);
        Assert.NotEqual(PlayerbotGoalKind.WorldPvp, await world.OnWorldAsync(() => world.BrainOf(bot).Goal));
        Assert.False(capture.Any(Key("silithyst-use-no-aura")) || capture.Any(Key("silithyst-carry-timeout")) || capture.Any(Key("silithyst-unreachable-trigger")),
            capture.Describe());
    }

    [Fact]
    public async Task TheCarrier_NeverMounts_WhileItHoldsTheFlag()
    {
        await using SilithystWorld world = await StartAsync(o => o.WorldPvp.Silithyst.Enabled = false);
        Guid bot = await world.AddBotAsync("Dustwalker", 1);
        bool before = await world.OnWorldAsync(() => PlayerbotMount.CanMountHere(world.PlayerOf(bot)));
        Assert.True(before, "the bot could not mount here even without the flag: the check below proves nothing");

        Assert.True(await world.CastOnSelfAsync(bot, Flag));
        Assert.True(await world.OnWorldAsync(() => world.HasAura(world.PlayerOf(bot), Flag)));
        Assert.False(await world.OnWorldAsync(() => PlayerbotMount.CanMountHere(world.PlayerOf(bot))), "a carrier may mount");

        await world.OnWorldAsync(() => { world.RemoveAura(world.PlayerOf(bot), Flag); return true; });
        Assert.True(await world.OnWorldAsync(() => PlayerbotMount.CanMountHere(world.PlayerOf(bot))), "the bot cannot mount again after losing the flag");
    }

    // --- sharing a geyser ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task TwoBots_OneGeyser_OneClaim()
    {
        await using SilithystWorld world = await StartAsync();
        Guid first = await world.AddBotAsync("Dustone", 1);
        Guid second = await world.AddBotAsync("Dusttwo", 1);
        ulong key = GuidOf(Geyser, GeyserSpawn).Value;

        Assert.True(await world.RunUntilAsync(30_000, () => world.BrainOf(first).WorldPvp.Active || world.BrainOf(second).WorldPvp.Active), "no bot went for the geyser");
        // A few more thinks: the other bot has had its chance to go for it too.
        await world.RunUntilAsync(1_000, () => false);
        await world.OnWorldAsync(() =>
        {
            int onIt = (world.BrainOf(first).WorldPvp.ActiveKey == key ? 1 : 0) + (world.BrainOf(second).WorldPvp.ActiveKey == key ? 1 : 0);
            int pickedUp = world.BrainOf(first).WorldPvp.Pickups + world.BrainOf(second).WorldPvp.Pickups;
            Assert.True(onIt + pickedUp <= 1, $"{onIt} bots on the geyser and {pickedUp} flags picked up");
            if (onIt == 1) Assert.Equal(1, world.BrainOf(first).Claims.ClaimantsOf(key, world.World.Host.World.NowMs));
            return true;
        });
    }

    // --- who takes part -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("out-of-zone")]
    [InlineData("option-off")]
    [InlineData("too-low")]
    [InlineData("chance-zero")]
    public async Task AnIneligibleBot_IgnoresTheGeyser(string why)
    {
        await using SilithystWorld world = await StartAsync(o =>
        {
            if (why == "option-off") o.WorldPvp.Silithyst.Enabled = false;
            if (why == "chance-zero") o.WorldPvp.Silithyst.ParticipationChance = 0f;
        });
        Guid bot = await world.AddBotAsync(why switch { "out-of-zone" => "Idleaway", "option-off" => "Idleoff", "too-low" => "Idlelow", _ => "Idlezero" }, 1, level: (byte)(why == "too-low" ? 54 : 60), inZone: why != "out-of-zone");

        bool took = await world.RunUntilAsync(40_000, () => world.BrainOf(bot).WorldPvp.Active || world.BrainOf(bot).WorldPvp.Pickups > 0);
        Assert.False(took, why + ": the bot went for the geyser");
        Assert.Equal(0u, await world.OnWorldAsync(() => world.Zone.GatheredAlliance));
        Assert.Equal(0UL, await world.OnWorldAsync(() => world.Zone.PickupsTotal));
        Assert.Equal(GameObjectLootState.Ready, await world.OnWorldAsync(() => ((GameObject)world.PlayerOf(bot).Map!.FindObject(GuidOf(Geyser, GeyserSpawn))!).LootState));
    }

    [Fact]
    public async Task WarMode_RaisesTheChanceToTakePart()
    {
        // A chance of 0 never takes part; the War Mode chance of 1 does, with the same bot.
        await using SilithystWorld world = await StartAsync(o =>
        {
            o.WorldPvp.Silithyst.ParticipationChance = 0f;
            o.WorldPvp.Silithyst.WarModeParticipationChance = 1f;
            o.Social.WarMode.Enabled = true;
        });
        Guid bot = await world.AddBotAsync("Dustwar", 1);
        Assert.True(await RunUntilDeliveredAsync(world, bot), "the bot did not take part under War Mode");
    }

    // --- giving up --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ALostFlag_AbandonsTheRun_WithNoAnomaly_AndNoDelivery()
    {
        await using SilithystWorld world = await StartAsync();
        Guid bot = await world.AddBotAsync("Dustlost", 1);
        using AnomalyCapture capture = await world.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(world.PlayerOf(bot)));

        Assert.True(await world.RunUntilAsync(60_000, () => world.BrainOf(bot).WorldPvp.IsCarrying), "the bot never picked the flag up");
        await world.RunUntilAsync(600, () => false); // a few steps on the way
        await world.OnWorldAsync(() => { world.RemoveAura(world.PlayerOf(bot), Flag); return true; });

        Assert.True(await world.RunUntilAsync(10_000, () => !world.BrainOf(bot).WorldPvp.Active), "the run did not end after the flag was lost");
        await world.OnWorldAsync(() =>
        {
            Assert.Equal("lost", world.BrainOf(bot).WorldPvp.LastOutcome);
            Assert.Equal(0, world.BrainOf(bot).WorldPvp.Deliveries);
            Assert.Equal(0UL, world.Zone.TurnInsTotal);
            Assert.False(world.HasAura(world.PlayerOf(bot), Traces));
            return true;
        });
        Assert.False(capture.Any(Key("silithyst-use-no-aura")) || capture.Any(Key("silithyst-carry-timeout")) || capture.Any(Key("silithyst-unreachable-trigger")),
            capture.Describe());
    }

    [Fact]
    public async Task AGeyserThatGivesNoFlag_IsReported_AndSetAside()
    {
        await using SilithystWorld world = await StartAsync(brokenClick: true);
        Guid bot = await world.AddBotAsync("Dustnone", 1);
        using AnomalyCapture capture = await world.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(world.PlayerOf(bot)));

        Assert.True(await world.RunUntilAsync(60_000, () => capture.Any(Key("silithyst-use-no-aura"))), "the missing flag was not reported");
        await world.OnWorldAsync(() =>
        {
            PlayerbotWorldPvpObjectives run = world.BrainOf(bot).WorldPvp;
            Assert.Equal("no-aura", run.LastOutcome);
            Assert.True(run.Ledger.IsDeferred(GuidOf(Geyser, GeyserSpawn).Value, world.World.Host.World.NowMs), "the geyser was not set aside");
            Assert.Equal(0, run.Pickups);
            return true;
        });
    }

    [Fact]
    public async Task ACarryThatRunsPastItsLimit_IsReported()
    {
        // Direct control of the controller (the brain's own run is off): a bot already holding the flag, and a clock that jumps past the limit.
        await using SilithystWorld world = await StartAsync(o => o.WorldPvp.Silithyst.Enabled = false);
        Guid bot = await world.AddBotAsync("Dustslow", 1);
        Assert.True(await world.CastOnSelfAsync(bot, Flag));
        uint clock = 1_000_000;
        using AnomalyCapture capture = await world.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(world.PlayerOf(bot)));
        PlayerbotWorldPvpObjectives run = await world.OnWorldAsync(() =>
        {
            WorldSession session = world.World.Bots.FindSession(bot)!;
            return new PlayerbotWorldPvpObjectives(session, world.Options, null) { Clock = () => clock };
        });

        Assert.True((await world.OnWorldAsync(() => Step(world, bot, run))).Busy, "the carrier was not taken up");
        Assert.True(run.IsCarrying);
        clock += PlayerbotWorldPvpObjectives.CarryMaxMs + 1;
        await world.OnWorldAsync(() => Step(world, bot, run));

        Assert.True(capture.Any(Key("silithyst-carry-timeout")), capture.Describe());
        Assert.False(run.Active);
        Assert.Equal("carry-timeout", run.LastOutcome);
    }

    [Fact]
    public async Task ACarrier_WithNoTurnInTrigger_ReportsItUnreachable()
    {
        await using SilithystWorld world = await StartAsync(o => o.WorldPvp.Silithyst.Enabled = false);
        Guid bot = await world.AddBotAsync("Dustnowhere", 2);
        Assert.True(await world.CastOnSelfAsync(bot, Flag));
        await world.OnWorldAsync(() =>
        {
            Game.Maps.Templates.WorldMaps maps = Game.Maps.Templates.WorldMaps.Of(world.World.Host.World);
            maps.ReplaceAreaTriggerTables(maps.BuildAreaTriggerTables(GridTerrain.InMemoryMapDataStore.Content with { AreaTriggers = [], AreaTriggerTeleports = [] }));
            return true;
        });
        using AnomalyCapture capture = await world.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(world.PlayerOf(bot)));
        PlayerbotWorldPvpObjectives run = await world.OnWorldAsync(() => new PlayerbotWorldPvpObjectives(world.World.Bots.FindSession(bot)!, world.Options, null));

        await world.OnWorldAsync(() => Step(world, bot, run));

        Assert.True(capture.Any(Key("silithyst-unreachable-trigger")), capture.Describe());
        Assert.False(run.Active);
    }

    private static WorldPvpStep Step(SilithystWorld world, Guid bot, PlayerbotWorldPvpObjectives run)
    {
        WorldSession session = world.World.Bots.FindSession(bot)!;
        session.ManagedBudget = new ManagedActionBudget(5);
        return run.Update(session.Player!, 200, mayBegin: false);
    }
}

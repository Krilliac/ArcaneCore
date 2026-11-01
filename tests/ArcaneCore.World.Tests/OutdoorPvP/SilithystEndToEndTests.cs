using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.OutdoorPvP;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.WorldPvp;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.WorldPvp.SilithystWorld;

namespace ArcaneCore.World.Tests.OutdoorPvP;

/// <summary>
/// The server half of Silithyst with the packets a client sends and no bot goal in the way: CMSG_GAMEOBJ_USE on the geyser (goober spell 29518,
/// whose script gives the flag aura 29519), then CMSG_AREATRIGGER of the turn-in trigger from inside it (the real handler, outdoor PvP feature and
/// spell system). The Alliance's trigger 4162 takes an Alliance carrier's flag and leaves 29534 Traces of Silithyst; a Horde carrier standing
/// in it keeps the flag (the other camp's trigger is swallowed).
/// </summary>
public sealed class SilithystEndToEndTests
{
    private const uint Flag = SilithusCatalog.SpellSilithystFlag, Traces = SilithusCatalog.SpellTracesOfSilithyst;

    private static async Task<Guid> BotAtAsync(SilithystWorld world, string name, byte race, Vector3 at)
    {
        Guid bot = await world.AddBotAsync(name, race);
        await world.OnWorldAsync(() =>
        {
            Player player = world.PlayerOf(bot);
            player.Relocate(at.X, at.Y, at.Z, 0f, world.World.Host.World.NowMs);
            return true;
        });
        return bot;
    }

    private static Task SendAsync(SilithystWorld world, Guid bot, WorldOpcode opcode, byte[] payload) => world.OnWorldAsync(() =>
    {
        WorldSession session = world.World.Bots.FindSession(bot)!;
        session.ManagedBudget = new ManagedActionBudget(5);
        Assert.True(session.TryManagedAction(opcode, payload), opcode + " was refused");
        return true;
    });

    private static async Task UseGeyserAsync(SilithystWorld world, Guid bot)
    {
        await SendAsync(world, bot, WorldOpcode.CmsgGameobjUse, BitConverter.GetBytes(GuidOf(Geyser, GeyserSpawn).Value));
        Assert.True(await world.RunUntilAsync(5_000, () => world.HasAura(world.PlayerOf(bot), Flag)), "using the geyser gave no Silithyst");
    }

    [Fact]
    public async Task UsingTheGeyser_GivesTheFlag_AndWalkingIntoTheOwnTrigger_CountsItAndLeavesTraces()
    {
        // The brain's own run is off: only the packets below move this bot.
        await using SilithystWorld world = await StartAsync(o => o.WorldPvp.Silithyst.Enabled = false);
        Guid bot = await BotAtAsync(world, "Silithuser", 1, GeyserAt + new Vector3(-2f, 0f, 0f));
        Assert.False(await world.OnWorldAsync(() => world.HasAura(world.PlayerOf(bot), Flag)));

        await UseGeyserAsync(world, bot);
        Assert.Equal(1UL, await world.OnWorldAsync(() => world.Zone.PickupsTotal));
        Assert.Equal(0u, await world.OnWorldAsync(() => world.Zone.GatheredAlliance));

        await world.OnWorldAsync(() =>
        {
            world.PlayerOf(bot).Relocate(AllianceTrigger.X + 1f, AllianceTrigger.Y, AllianceTrigger.Z, 0f, world.World.Host.World.NowMs);
            return true;
        });
        await SendAsync(world, bot, WorldOpcode.CmsgAreatrigger, BitConverter.GetBytes(SilithusCatalog.AreaTriggerAlliance));
        Assert.True(await world.RunUntilAsync(2_000, () => world.Zone.GatheredAlliance == 1), "the turn-in was not counted");

        await world.OnWorldAsync(() =>
        {
            Player player = world.PlayerOf(bot);
            Assert.Equal(1u, world.Zone.GatheredAlliance);
            Assert.Equal(0u, world.Zone.GatheredHorde);
            Assert.Equal(1UL, world.Zone.TurnInsTotal);
            Assert.Equal(1u, world.Zone.LastTurnInCount);
            Assert.True(world.HasAura(player, Traces), "Traces of Silithyst was not applied");
            Assert.False(world.HasAura(player, Flag), "the flag survived the turn-in");
            return true;
        });
    }

    [Fact]
    public async Task AHordeCarrier_InTheAlliancesTrigger_KeepsTheFlag()
    {
        await using SilithystWorld world = await StartAsync(o => o.WorldPvp.Silithyst.Enabled = false);
        Guid bot = await BotAtAsync(world, "Silithhorde", 2, GeyserAt + new Vector3(-2f, 0f, 0f));
        await UseGeyserAsync(world, bot);

        await world.OnWorldAsync(() =>
        {
            world.PlayerOf(bot).Relocate(AllianceTrigger.X + 1f, AllianceTrigger.Y, AllianceTrigger.Z, 0f, world.World.Host.World.NowMs);
            return true;
        });
        await SendAsync(world, bot, WorldOpcode.CmsgAreatrigger, BitConverter.GetBytes(SilithusCatalog.AreaTriggerAlliance));
        await world.RunUntilAsync(1_000, () => false);

        await world.OnWorldAsync(() =>
        {
            Player player = world.PlayerOf(bot);
            Assert.True(world.HasAura(player, Flag), "the Horde carrier lost the flag in the Alliance's trigger");
            Assert.False(world.HasAura(player, Traces));
            Assert.Equal(0u, world.Zone.GatheredAlliance);
            Assert.Equal(0u, world.Zone.GatheredHorde);
            Assert.Equal(0UL, world.Zone.TurnInsTotal);
            return true;
        });
    }
}

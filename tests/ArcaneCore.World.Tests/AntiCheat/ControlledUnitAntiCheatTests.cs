using System.Collections.Concurrent;
using ArcaneCore.Game;
using ArcaneCore.Game.AntiCheat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.AntiCheat;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Pets;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.AntiCheat;

/// <summary>
/// The anticheat paths of a unit the player controls (docs/areas/anticheat.md, "A unit the player controls"), through the real handlers:
/// the time skip the client reports for the possessed mover, the terrain checks, and a controller that is exempt while it moves the unit.
/// Each test fails on the code before the change that it pins (the skip reached only the controller's own checks, controlled units got no
/// terrain, and an exempt controller's block left the unit's baseline in place).
/// </summary>
public sealed class ControlledUnitAntiCheatTests
{
    private const int NoBaselineGapMs = 600_000;
    private const float Blink = 80f;

    private sealed class NoLiquidTerrain : IAntiCheatTerrain
    {
        public bool? IsInLiquid(float x, float y, float z) => false;

        public float? FloorHeight(float x, float y, float z) => null;

        public bool? IsInLineOfSight(float x1, float y1, float z1, float x2, float y2, float z2) => null;
    }

    private static AntiCheatFeature FeatureOf(WorldTestHost host) => host.WorldServices.GetRequiredService<AntiCheatFeature>();

    private static async Task<ConcurrentQueue<(string Player, AntiCheatFinding Finding)>> ArmAsync(WorldTestHost host, AntiCheatOptions? options = null)
    {
        options ??= new AntiCheatOptions();
        options.BaselineGapMs = NoBaselineGapMs;
        await host.OnWorldAsync(() => FeatureOf(host).ApplyOptions(options));
        var seen = new ConcurrentQueue<(string, AntiCheatFinding)>();
        FeatureOf(host).FindingRecorded += (player, finding, _, _) => seen.Enqueue((player.Name, finding));
        return seen;
    }

    private static byte[] Block(uint time, float x, float y, float z, MovementFlags flags = MovementFlags.Forward)
    {
        var writer = new PacketWriter(48);
        new MovementInfo { Flags = flags, Time = time, X = x, Y = y, Z = z }.Write(writer);
        return writer.ToArray();
    }

    private static byte[] Guid64(ulong guid) => BitConverter.GetBytes(guid);

    private static byte[] Skip(ulong guid, uint lagMs)
    {
        var writer = new PacketWriter(12);
        writer.WriteUInt64(guid);
        writer.WriteUInt32(lagMs);
        return writer.ToArray();
    }

    private static async Task<(ulong Guid, float X, float Y, float Z, Creature Mob)> PossessMobAsync(WorldTestHost host, Player player, uint entry)
    {
        return await host.OnWorldAsync(() =>
        {
            var template = new CreatureTemplate { Entry = entry, Name = "Possessed mob", MinLevel = 1, MaxLevel = 1, DisplayIds = [903], Faction = 14, AIName = "NullAI" };
            Creature mob = host.WorldServices.GetRequiredService<CreatureWorldFeature>().GetOrCreateSystem(player.Map!)
                .SpawnTemporary(template, player.X + 5, player.Y, player.Z, 0);
            host.WorldServices.GetRequiredService<PetsFeature>().Service.Charms.ModPossess(player, mob, apply: true, AuraRemoveMode.Default, spell: null);
            Assert.Same(mob, player.GetMover());
            return (mob.Guid.Value, mob.X, mob.Y, mob.Z, mob);
        });
    }

    /// <summary>A run of five 100 ms steps of 0.7 yd by the possessed unit, a 500 ms silence, a skip of 400 ms naming the unit, and a 4.5 yd step stamped 50 ms on.</summary>
    private static async Task<ConcurrentQueue<(string Player, AntiCheatFinding Finding)>> SkipAfterSilenceAsync(WorldTestHost host, string account, string name, uint entry, bool otherPacketAfterSilence)
    {
        ConcurrentQueue<(string Player, AntiCheatFinding Finding)> seen = await ArmAsync(host);
        await using WorldTestClient client = await host.EnterWorldAsync(account, name);
        await client.CollectAsync();
        Player player = await host.PlayerAsync(name);
        (ulong guid, float x, float y, float z, _) = await PossessMobAsync(host, player, entry);

        await client.SendAsync(WorldOpcode.CmsgSetActiveMover, Guid64(guid));
        uint time = 100_000;
        await client.SendAsync(WorldOpcode.MsgMoveStartForward, Block(time, x, y, z));
        for (int i = 0; i < 5; i++)
        {
            await Task.Delay(100);
            time += 100;
            x += 0.7f;
            await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, Block(time, x, y, z));
        }

        await client.CollectThroughBarrierAsync();
        Assert.Empty(seen);

        await Task.Delay(500);
        if (otherPacketAfterSilence)
        {
            // A full barrier (its own CMSG_PLAYED_TIME, answered before this returns): the extra packet is handled and its answer consumed here,
            // so the final barrier below can only be answered after the skip and the heartbeat, and the finding is recorded by then.
            await client.CollectThroughBarrierAsync();
        }

        await client.SendAsync(WorldOpcode.CmsgMoveTimeSkipped, Skip(guid, 400));
        time += 50;
        x += 4.5f;
        await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, Block(time, x, y, z));
        await client.CollectThroughBarrierAsync();
        return seen;
    }

    [Fact]
    [Trait("Category", "Timing")] // the forged arm needs the 500 ms silence to read as at least 500 ms on the receive clock
    public async Task ATimeSkipNamingThePossessedCreature_BuysTheStepItsSilenceCovers_ButNotAfterAnotherWorldPacketClosedTheSilence()
    {
        // Control: the 500 ms of silence backs the 400 ms skip, which reaches the possessed creature's checks (not the controller's own).
        await using (WorldTestHost control = WorldTestHost.Start())
        {
            ConcurrentQueue<(string Player, AntiCheatFinding Finding)> seen = await SkipAfterSilenceAsync(control, "CSKIPC", "Cskipcontrol", 991_731, otherPacketAfterSilence: false);
            Assert.Empty(seen);
        }

        // Same bytes, but a stamped world packet arrived just before the skip: the silence is gone, the skip buys nothing.
        await using WorldTestHost host = WorldTestHost.Start();
        ConcurrentQueue<(string Player, AntiCheatFinding Finding)> forged = await SkipAfterSilenceAsync(host, "CSKIPF", "Cskipforged", 991_732, otherPacketAfterSilence: true);
        Assert.Contains(forged, s => s.Player == "Cskipforged" && s.Finding.Type == AntiCheatViolation.Speed && s.Finding.Detail.StartsWith("controlled unit", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Timing")] // real-time paced packets
    public async Task ASwimmingPossessedPlayer_WhereTheTerrainHoldsNoWater_IsScoredByTheTerrainChecks()
    {
        await using var host = WorldTestHost.Start();
        ConcurrentQueue<(string Player, AntiCheatFinding Finding)> seen = await ArmAsync(host);
        FeatureOf(host).TerrainProbe = _ => new NoLiquidTerrain();
        await using WorldTestClient client = await host.EnterWorldAsync("TERRCTRL", "Terrctrl");
        await using WorldTestClient victimClient = await host.EnterWorldAsync("TERRVICT", "Terrvict");
        await client.CollectAsync();
        await victimClient.CollectAsync();
        Player player = await host.PlayerAsync("Terrctrl");
        Player victim = await host.PlayerAsync("Terrvict");
        (ulong guid, float x, float y, float z) = await host.OnWorldAsync(() =>
        {
            host.WorldServices.GetRequiredService<PetsFeature>().Service.Charms.ModPossess(player, victim, apply: true, AuraRemoveMode.Default, spell: null);
            Assert.Same(victim, player.GetMover());
            return (victim.Guid.Value, victim.X, victim.Y, victim.Z);
        });

        // The swimming flag with no water under the unit: the first block is judged (seeded from the server position), so the fourth judged block is the fourth in a row that claims water where there is none.
        await client.SendAsync(WorldOpcode.CmsgSetActiveMover, Guid64(guid));
        uint time = 100_000;
        MovementFlags swimming = MovementFlags.Forward | MovementFlags.Swimming;
        await client.SendAsync(WorldOpcode.MsgMoveStartForward, Block(time, x, y, z, swimming));
        for (int i = 0; i < 6; i++)
        {
            await Task.Delay(100);
            time += 100;
            x += 0.7f;
            await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, Block(time, x, y, z, swimming));
        }

        await client.CollectThroughBarrierAsync();

        Assert.Contains(seen, s => s.Player == "Terrctrl" && s.Finding.Type == AntiCheatViolation.Physics && s.Finding.Detail.Contains("swimming out of water", StringComparison.Ordinal));
        Assert.DoesNotContain(seen, s => s.Player == "Terrvict");
    }

    [Fact]
    [Trait("Category", "Timing")] // real-time paced packets
    public async Task AGameMasterMovingAPossessedCreatureFarWhileExempt_IsNotScoredAFalseTeleportAfterLeavingGmMode()
    {
        await using var host = WorldTestHost.Start();
        ConcurrentQueue<(string Player, AntiCheatFinding Finding)> seen = await ArmAsync(host, new AntiCheatOptions { ExemptSecurity = AccountSecurity.Administrator });
        await using WorldTestClient client = await host.EnterWorldAsync("CTRLGM", "Ctrlgm", AccountSecurity.GameMaster);
        await client.CollectAsync();
        Player player = await host.PlayerAsync("Ctrlgm");
        (ulong guid, float x, float y, float z, Creature mob) = await PossessMobAsync(host, player, 991_733);

        // Judged first: the unit's checks hold a baseline.
        await client.SendAsync(WorldOpcode.CmsgSetActiveMover, Guid64(guid));
        uint time = 100_000;
        await client.SendAsync(WorldOpcode.MsgMoveStartForward, Block(time, x, y, z));
        await Task.Delay(100);
        time += 100;
        x += 0.7f;
        await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, Block(time, x, y, z));
        await client.CollectThroughBarrierAsync();
        Assert.Empty(seen);

        // GM mode exempts the controller: the unit is carried 80 yards, stored unjudged.
        await client.SendChatAsync(ChatType.Say, Language.Common, ".gm on");
        await client.ReadChatAsync();
        await Task.Delay(100);
        time += 100;
        x += Blink;
        await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, Block(time, x, y, z));
        await client.CollectThroughBarrierAsync();
        Assert.Equal(x, await host.OnWorldAsync(() => mob.X));
        Assert.Empty(seen);

        // Back to being checked: the next block is a fresh baseline, not a step from where the unit was before the exemption.
        await client.SendChatAsync(ChatType.Say, Language.Common, ".gm off");
        await client.ReadChatAsync();
        await Task.Delay(100);
        time += 100;
        x += 0.7f;
        await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, Block(time, x, y, z));
        await client.CollectThroughBarrierAsync();
        Assert.Empty(seen);

        // The checks are live again.
        await Task.Delay(100);
        time += 100;
        x += Blink;
        await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, Block(time, x, y, z));
        await client.CollectThroughBarrierAsync();
        Assert.Contains(seen, s => s.Player == "Ctrlgm" && s.Finding.Type == AntiCheatViolation.Teleport);
    }
}

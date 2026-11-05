using System.Collections.Concurrent;
using ArcaneCore.Game;
using ArcaneCore.Game.AntiCheat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.AntiCheat;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Pets;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.AntiCheat;

/// <summary>
/// A blink whose client stamp is behind the previous block's (but inside <c>ClientTimeRegressionMs</c>, so the timestamp check stays quiet) must
/// still score as a teleport through the real handlers: such a block spans no client time, so its step is judged against the teleport slack alone
/// (docs/areas/anticheat.md). Covers the paths outside HandleMovement that store a client block:
/// <c>CMSG_MOVE_NOT_ACTIVE_MOVER</c> naming the player itself (AntiCheatFeature.BeforeMovement) and a possessed creature moved by its controller
/// (AntiCheatFeature.BeforeControlledMovement), and CMSG_MOVE_NOT_ACTIVE_MOVER naming a unit the player controlled (same method). The time skip and terrain
/// paths of controlled units are covered in ControlledUnitAntiCheatTests; the session trace shadow in SessionTraceAntiCheatTests.
/// </summary>
public sealed class BehindStampedBlinkTests
{
    private const int NoBaselineGapMs = 600_000;
    private const float Blink = 80f;
    private const uint Behind = 200;

    private static AntiCheatFeature FeatureOf(WorldTestHost host) => host.WorldServices.GetRequiredService<AntiCheatFeature>();

    private static async Task<ConcurrentQueue<(string Player, AntiCheatFinding Finding)>> ArmAsync(WorldTestHost host)
    {
        await host.OnWorldAsync(() => FeatureOf(host).ApplyOptions(new AntiCheatOptions { BaselineGapMs = NoBaselineGapMs }));
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

    private static byte[] NotActiveMover(ulong guid, uint time, float x, float y, float z)
    {
        var writer = new PacketWriter(56);
        writer.WriteUInt64(guid);
        new MovementInfo { Flags = MovementFlags.None, Time = time, X = x, Y = y, Z = z }.Write(writer);
        return writer.ToArray();
    }

    private static byte[] Guid64(ulong guid) => BitConverter.GetBytes(guid);

    [Fact]
    [Trait("Category", "Timing")] // real-time paced packets
    public async Task ABehindStampedBlink_InANotActiveMoverNamingThePlayerItself_ScoresTeleport()
    {
        await using var host = WorldTestHost.Start();
        ConcurrentQueue<(string Player, AntiCheatFinding Finding)> seen = await ArmAsync(host);
        await using WorldTestClient client = await host.EnterWorldAsync("BEHINDSELF", "Behindself");
        await client.CollectAsync();
        Player player = await host.PlayerAsync("Behindself");
        (ulong guid, float x, float y, float z) = await host.OnWorldAsync(() => (player.Guid.Value, player.X, player.Y, player.Z));

        uint time = 100_000;
        await client.SendAsync(WorldOpcode.MsgMoveStartForward, Block(time, x, y, z));
        await Task.Delay(100);
        time += 100;
        await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, Block(time, x + 0.7f, y, z));
        await client.CollectThroughBarrierAsync();
        Assert.Empty(seen);

        await Task.Delay(100);
        await client.SendAsync(WorldOpcode.CmsgMoveNotActiveMover, NotActiveMover(guid, time - Behind, x + 0.7f + Blink, y, z));
        await client.CollectThroughBarrierAsync();

        Assert.Contains(seen, s => s.Player == "Behindself" && s.Finding.Type == AntiCheatViolation.Teleport);
        Assert.DoesNotContain(seen, s => s.Finding.Type == AntiCheatViolation.PacketTiming);
    }

    [Fact]
    [Trait("Category", "Timing")] // real-time paced packets
    public async Task ABehindStampedBlink_OfAPossessedCreature_ScoresTeleportAgainstTheController()
    {
        await using var host = WorldTestHost.Start();
        ConcurrentQueue<(string Player, AntiCheatFinding Finding)> seen = await ArmAsync(host);
        await using WorldTestClient client = await host.EnterWorldAsync("BEHINDPOSS", "Behindposs");
        await client.CollectAsync();
        Player player = await host.PlayerAsync("Behindposs");
        (ulong guid, float x, float y, float z) = await host.OnWorldAsync(() =>
        {
            var template = new CreatureTemplate { Entry = 991_721, Name = "Possessed mob", MinLevel = 1, MaxLevel = 1, DisplayIds = [903], Faction = 14, AIName = "NullAI" };
            Creature mob = host.WorldServices.GetRequiredService<CreatureWorldFeature>().GetOrCreateSystem(player.Map!)
                .SpawnTemporary(template, player.X + 5, player.Y, player.Z, 0);
            host.WorldServices.GetRequiredService<PetsFeature>().Service.Charms.ModPossess(player, mob, apply: true, AuraRemoveMode.Default, spell: null);
            Assert.Same(mob, player.GetMover());
            return (mob.Guid.Value, mob.X, mob.Y, mob.Z);
        });

        await client.SendAsync(WorldOpcode.CmsgSetActiveMover, Guid64(guid));
        uint time = 100_000;
        await client.SendAsync(WorldOpcode.MsgMoveStartForward, Block(time, x, y, z));
        await Task.Delay(100);
        time += 100;
        await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, Block(time, x + 0.7f, y, z));
        await client.CollectThroughBarrierAsync();
        Assert.Empty(seen);

        await Task.Delay(100);
        await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, Block(time - Behind, x + 0.7f + Blink, y, z));
        await client.CollectThroughBarrierAsync();

        Assert.Contains(seen, s => s.Player == "Behindposs" && s.Finding.Type == AntiCheatViolation.Teleport);
        Assert.DoesNotContain(seen, s => s.Finding.Type == AntiCheatViolation.PacketTiming);
    }

    [Fact]
    [Trait("Category", "Timing")] // real-time paced packets
    public async Task ABehindStampedBlink_OfThePlayer_ThroughMovementPackets_ScoresTeleport()
    {
        await using var host = WorldTestHost.Start();
        ConcurrentQueue<(string Player, AntiCheatFinding Finding)> seen = await ArmAsync(host);
        await using WorldTestClient client = await host.EnterWorldAsync("BEHINDOWN", "Behindown");
        await client.CollectAsync();
        Player player = await host.PlayerAsync("Behindown");
        (float x, float y, float z) = await host.OnWorldAsync(() => (player.X, player.Y, player.Z));

        uint time = 100_000;
        await client.SendAsync(WorldOpcode.MsgMoveStartForward, Block(time, x, y, z));
        await Task.Delay(100);
        time += 100;
        await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, Block(time, x + 0.7f, y, z));
        await client.CollectThroughBarrierAsync();
        Assert.Empty(seen);

        await Task.Delay(100);
        await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, Block(time - Behind, x + 0.7f + Blink, y, z));
        await client.CollectThroughBarrierAsync();

        Assert.Contains(seen, s => s.Player == "Behindown" && s.Finding.Type == AntiCheatViolation.Teleport);
    }

    [Fact]
    [Trait("Category", "Timing")] // real-time paced packets
    public async Task ABehindStampedBlink_InANotActiveMoverNamingAnExControlledCreature_ScoresTeleportAgainstTheSender()
    {
        await using var host = WorldTestHost.Start();
        ConcurrentQueue<(string Player, AntiCheatFinding Finding)> seen = await ArmAsync(host);
        await using WorldTestClient client = await host.EnterWorldAsync("BEHINDEX", "Behindex");
        await client.CollectAsync();
        Player player = await host.PlayerAsync("Behindex");
        (ulong guid, float x, float y, float z, Creature mob) = await host.OnWorldAsync(() =>
        {
            var template = new CreatureTemplate { Entry = 991_722, Name = "Possessed mob", MinLevel = 1, MaxLevel = 1, DisplayIds = [903], Faction = 14, AIName = "NullAI" };
            Creature unit = host.WorldServices.GetRequiredService<CreatureWorldFeature>().GetOrCreateSystem(player.Map!)
                .SpawnTemporary(template, player.X + 5, player.Y, player.Z, 0);
            host.WorldServices.GetRequiredService<PetsFeature>().Service.Charms.ModPossess(player, unit, apply: true, AuraRemoveMode.Default, spell: null);
            Assert.Same(unit, player.GetMover());
            return (unit.Guid.Value, unit.X, unit.Y, unit.Z, unit);
        });

        // The possession gives the unit's checks a judged baseline, then ends; the client names the unit as the mover it gives up.
        await client.SendAsync(WorldOpcode.CmsgSetActiveMover, Guid64(guid));
        uint time = 100_000;
        await client.SendAsync(WorldOpcode.MsgMoveStartForward, Block(time, x, y, z));
        await Task.Delay(100);
        time += 100;
        await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, Block(time, x + 0.7f, y, z));
        await client.CollectThroughBarrierAsync();
        Assert.Empty(seen);
        await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<PetsFeature>().Service.Charms.ModPossess(player, mob, apply: false, AuraRemoveMode.Default, spell: null));
        Assert.NotSame(mob, await host.OnWorldAsync(() => player.GetMover()));

        await Task.Delay(100);
        await client.SendAsync(WorldOpcode.CmsgMoveNotActiveMover, NotActiveMover(guid, time - Behind, x + 0.7f + Blink, y, z));
        await client.CollectThroughBarrierAsync();

        Assert.Contains(seen, s => s.Player == "Behindex" && s.Finding.Type == AntiCheatViolation.Teleport);
        Assert.DoesNotContain(seen, s => s.Finding.Type == AntiCheatViolation.PacketTiming);
    }
}

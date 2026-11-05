using System.Collections.Concurrent;
using ArcaneCore.Game;
using ArcaneCore.Game.AntiCheat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using ArcaneCore.World.AntiCheat;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.AntiCheat;

/// <summary>
/// The movement block of a solicited acknowledgement (speed, flag, root, knockback, turn rate) is judged before it is stored, like a movement
/// packet. Before, the handlers stored it and only advanced the stored-position marker (AfterMovement), so a client could carry any
/// position or flag in the ack of an order the server really sent, and the next block was then measured from there. vmangos runs the
/// cheat tests on the block before relocating (HandleForceSpeedChangeAckOpcodes, MovementHandler.cpp:489-494).
/// </summary>
public sealed class SolicitedAckJudgementTests
{
    private const uint StartTime = 100_000;

    private sealed class Scene(WorldTestHost host, WorldTestClient client, Player player, ConcurrentQueue<AntiCheatFinding> seen, ulong guid, float x, float y, float z) : IAsyncDisposable
    {
        public WorldTestHost Host { get; } = host;
        public WorldTestClient Client { get; } = client;
        public Player Player { get; } = player;
        public ConcurrentQueue<AntiCheatFinding> Seen { get; } = seen;
        public ulong Guid { get; } = guid;
        public float X { get; } = x;
        public float Y { get; } = y;
        public float Z { get; } = z;
        public uint Time { get; set; } = StartTime;

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Host.DisposeAsync();
        }

        public byte[] Block(MovementFlags flags, float dx)
        {
            var writer = new PacketWriter(48);
            new MovementInfo { Flags = flags, Time = Time, X = X + dx, Y = Y, Z = Z }.Write(writer);
            return writer.ToArray();
        }

        /// <summary>Two ordinary movement packets that give the checks a baseline; nothing is found.</summary>
        public async Task BaselineAsync()
        {
            await Client.SendAsync(WorldOpcode.MsgMoveStartForward, Block(MovementFlags.Forward, 0f));
            await Task.Delay(100);
            Time += 100;
            await Client.SendAsync(WorldOpcode.MsgMoveHeartbeat, Block(MovementFlags.Forward, 0.7f));
            await Client.CollectThroughBarrierAsync();
            Assert.Empty(Seen);
        }

        /// <summary>The counter of the order the server sent last (every counter already sent is below Player.MovementCounter).</summary>
        public Task<uint> LastCounterAsync() => Host.OnWorldAsync(() => Player.MovementCounter - 1);

        public byte[] Ack(uint counter, MovementFlags flags, float dx, Action<PacketWriter> tail)
        {
            var writer = new PacketWriter(64);
            writer.WriteUInt64(Guid);
            writer.WriteUInt32(counter);
            new MovementInfo { Flags = flags, Time = Time, X = X + dx, Y = Y, Z = Z }.Write(writer);
            tail(writer);
            return writer.ToArray();
        }
    }

    private static async Task<Scene> StartAsync(string account, string name)
    {
        WorldTestHost host = WorldTestHost.Start();
        AntiCheatFeature feature = host.WorldServices.GetRequiredService<AntiCheatFeature>();
        await host.OnWorldAsync(() => feature.ApplyOptions(new AntiCheatOptions { BaselineGapMs = 600_000 }));
        var seen = new ConcurrentQueue<AntiCheatFinding>();
        feature.FindingRecorded += (_, finding, _, _) => seen.Enqueue(finding);
        WorldTestClient client = await host.EnterWorldAsync(account, name);
        await client.CollectAsync();
        Player player = await host.PlayerAsync(name);
        (ulong guid, float x, float y, float z) = await host.OnWorldAsync(() => (player.Guid.Value, player.X, player.Y, player.Z));
        return new Scene(host, client, player, seen, guid, x, y, z);
    }

    private static MovementFlags FlagOf(MovementChangeType type)
        => type switch
        {
            MovementChangeType.WaterWalk => MovementFlags.WaterWalking,
            MovementChangeType.Hover => MovementFlags.Hover,
            _ => MovementFlags.SafeFall,
        };

    [Fact]
    public async Task ASolicitedSpeedAck_CarryingABlinkPosition_IsScored()
    {
        await using Scene scene = await StartAsync("SPEEDJUDGE", "Speedjudge");
        await scene.BaselineAsync();
        float target = UnitSpeed.BaseSpeed(MoveType.Run) * 1.4f;
        await scene.Host.OnWorldAsync(() => UnitSpeed.SetRate(scene.Player, MoveType.Run, 1.4f));
        await scene.Client.ReadUntilAsync(SpeedPackets.ForceOpcode(MoveType.Run));

        await Task.Delay(100);
        scene.Time += 100;
        uint counter = await scene.LastCounterAsync();
        await scene.Client.SendAsync(SpeedPackets.AckOpcode(MoveType.Run), scene.Ack(counter, MovementFlags.Forward, 80.7f, w => w.WriteSingle(target)));
        await scene.Client.CollectThroughBarrierAsync();

        // The order was real and the ack matched it; the block 80 yards away is still a teleport. Without the judgement nothing is recorded.
        Assert.Contains(scene.Seen, f => f.Type == AntiCheatViolation.Teleport);
    }

    [Theory]
    [InlineData(MovementChangeType.WaterWalk, WorldOpcode.CmsgMoveWaterWalkAck)]
    [InlineData(MovementChangeType.Hover, WorldOpcode.CmsgMoveHoverAck)]
    [InlineData(MovementChangeType.FeatherFall, WorldOpcode.CmsgMoveFeatherFallAck)]
    public async Task ASolicitedFlagAck_CarryingABlinkPosition_IsScored(MovementChangeType type, WorldOpcode ackOpcode)
    {
        await using Scene scene = await StartAsync("FLAGJUDGE", "Flagjudge");
        await scene.BaselineAsync();
        await scene.Host.OnWorldAsync(() => MovementControl.Request(scene.Player, type, apply: true));
        await scene.Client.CollectThroughBarrierAsync();

        await Task.Delay(100);
        scene.Time += 100;
        uint counter = await scene.LastCounterAsync();
        await scene.Client.SendAsync(ackOpcode, scene.Ack(counter, MovementFlags.Forward | FlagOf(type), 80.7f, w => w.WriteUInt32(1)));
        await scene.Client.CollectThroughBarrierAsync();

        Assert.Contains(scene.Seen, f => f.Type == AntiCheatViolation.Teleport);
        // The flag is the order's own, so it is never "without a grant".
        Assert.DoesNotContain(scene.Seen, f => f.Type == AntiCheatViolation.Flag);
    }

    [Theory]
    [InlineData(MovementChangeType.WaterWalk, WorldOpcode.CmsgMoveWaterWalkAck)]
    [InlineData(MovementChangeType.Hover, WorldOpcode.CmsgMoveHoverAck)]
    [InlineData(MovementChangeType.FeatherFall, WorldOpcode.CmsgMoveFeatherFallAck)]
    public async Task AHonestSolicitedFlagAck_WithAnAppliedOrder_IsNotScored(MovementChangeType type, WorldOpcode ackOpcode)
    {
        await using Scene scene = await StartAsync("FLAGHONEST", "Flaghonest");
        await scene.BaselineAsync();
        await scene.Host.OnWorldAsync(() => MovementControl.Request(scene.Player, type, apply: true));
        await scene.Client.CollectThroughBarrierAsync();

        await Task.Delay(100);
        scene.Time += 100;
        uint counter = await scene.LastCounterAsync();
        await scene.Client.SendAsync(ackOpcode, scene.Ack(counter, MovementFlags.Forward | FlagOf(type), 1.4f, w => w.WriteUInt32(1)));
        await scene.Client.CollectThroughBarrierAsync();

        Assert.Empty(scene.Seen);
    }

    [Fact]
    public async Task ASolicitedRootAck_CarryingABlinkPosition_IsScored()
    {
        await using Scene scene = await StartAsync("ROOTJUDGE", "Rootjudge");
        await scene.BaselineAsync();
        await scene.Host.OnWorldAsync(() => MovementControl.Request(scene.Player, MovementChangeType.Root, apply: true));
        await scene.Client.CollectThroughBarrierAsync();

        await Task.Delay(100);
        scene.Time += 100;
        uint counter = await scene.LastCounterAsync();
        await scene.Client.SendAsync(WorldOpcode.CmsgForceMoveRootAck, scene.Ack(counter, MovementFlags.Root, 80.7f, _ => { }));
        await scene.Client.CollectThroughBarrierAsync();

        Assert.Contains(scene.Seen, f => f.Type == AntiCheatViolation.Teleport);
    }

    [Fact]
    public async Task ASolicitedKnockbackAck_CarryingAForgedFlag_IsScored()
    {
        await using Scene scene = await StartAsync("KNOCKJUDGE", "Knockjudge");
        await scene.BaselineAsync();
        SpellSystem spells = scene.Host.WorldServices.GetRequiredService<SpellFeature>().System;
        await scene.Host.OnWorldAsync(() => KnockbackService.KnockBack(spells, scene.Player, 0f, 7.5f, 5f));
        await scene.Client.ReadUntilAsync(WorldOpcode.SmsgMoveKnockBack);

        await Task.Delay(100);
        scene.Time += 100;
        var writer = new PacketWriter(64);
        writer.WriteUInt64(scene.Guid);
        writer.WriteUInt32(0);
        new MovementInfo
        {
            Flags = MovementFlags.Jumping | MovementFlags.Levitating, Time = scene.Time, X = scene.X + 0.7f, Y = scene.Y, Z = scene.Z,
            JumpCosAngle = 1f, JumpSinAngle = 0f, JumpXySpeed = 7.5f, JumpZSpeed = -5f,
        }.Write(writer);
        await scene.Client.SendAsync(WorldOpcode.CmsgMoveKnockBackAck, writer.ToArray());
        await scene.Client.CollectThroughBarrierAsync();

        // Levitating is never granted here; the knockback re-baselines the position checks, but the flag checks still run on the block.
        Assert.Contains(scene.Seen, f => f.Type == AntiCheatViolation.Flag);
    }

    /// <summary>Roots the player through a real order and an honest ack, so the server holds the Root flag when the next block arrives.</summary>
    private static async Task RootAsync(Scene scene)
    {
        await scene.Host.OnWorldAsync(() => scene.Player.SetRooted(true));
        await scene.Client.CollectThroughBarrierAsync();
        await Task.Delay(100);
        scene.Time += 100;
        uint counter = await scene.LastCounterAsync();
        await scene.Client.SendAsync(WorldOpcode.CmsgForceMoveRootAck, scene.Ack(counter, MovementFlags.Root, 0.7f, _ => { }));
        await scene.Client.CollectThroughBarrierAsync();
        Assert.Empty(scene.Seen);
        // IsRooted is what the rooted-while-moving check reads (AntiCheatFeature.cs:175); only Player.SetRooted sets it.
        Assert.True(await scene.Host.OnWorldAsync(() => scene.Player.IsRooted && scene.Player.Movement.HasFlag(MovementFlags.Root)));
    }

    [Fact]
    public async Task ASolicitedTurnRateAck_CarryingABlinkPosition_IsScored()
    {
        await using Scene scene = await StartAsync("TURNJUDGE", "Turnjudge");
        await scene.BaselineAsync();
        float rate = await scene.Host.OnWorldAsync(() =>
        {
            UnitSpeed.SetTurnRate(scene.Player, 1.5f);
            return scene.Player.TurnRate;
        });
        await scene.Client.ReadUntilAsync(WorldOpcode.SmsgForceTurnRateChange);

        await Task.Delay(100);
        scene.Time += 100;
        uint counter = await scene.LastCounterAsync();
        await scene.Client.SendAsync(WorldOpcode.CmsgForceTurnRateChangeAck, scene.Ack(counter, MovementFlags.Forward, 80.7f, w => w.WriteSingle(rate)));
        await scene.Client.CollectThroughBarrierAsync();

        Assert.Contains(scene.Seen, f => f.Type == AntiCheatViolation.Teleport);
    }

    [Fact]
    public async Task AHonestSolicitedSpeedAck_IsNotScored()
    {
        await using Scene scene = await StartAsync("SPEEDHONEST", "Speedhonest");
        await scene.BaselineAsync();
        float target = UnitSpeed.BaseSpeed(MoveType.Run) * 1.4f;
        await scene.Host.OnWorldAsync(() => UnitSpeed.SetRate(scene.Player, MoveType.Run, 1.4f));
        await scene.Client.ReadUntilAsync(SpeedPackets.ForceOpcode(MoveType.Run));

        await Task.Delay(100);
        scene.Time += 100;
        uint counter = await scene.LastCounterAsync();
        await scene.Client.SendAsync(SpeedPackets.AckOpcode(MoveType.Run), scene.Ack(counter, MovementFlags.Forward, 1.4f, w => w.WriteSingle(target)));
        await scene.Client.CollectThroughBarrierAsync();

        Assert.Empty(scene.Seen);
    }

    [Fact]
    public async Task AHonestSolicitedRootAck_IsNotScored()
    {
        await using Scene scene = await StartAsync("ROOTHONEST", "Roothonest");
        await scene.BaselineAsync();
        await RootAsync(scene);
    }

    [Fact]
    public async Task AnUnrootAck_CarryingAMovingBlock_IsNotScoredAsMovingWhileRooted()
    {
        await using Scene scene = await StartAsync("UNROOTHONEST", "Unroothonest");
        await scene.BaselineAsync();
        await RootAsync(scene);
        // Request, not SetRooted(false): IsRooted stays true, the state in which the unroot exemption decides the result.
        await scene.Host.OnWorldAsync(() => MovementControl.Request(scene.Player, MovementChangeType.Root, apply: false));
        await scene.Client.CollectThroughBarrierAsync();

        await Task.Delay(100);
        scene.Time += 100;
        uint counter = await scene.LastCounterAsync();
        await scene.Client.SendAsync(WorldOpcode.CmsgForceMoveUnrootAck, scene.Ack(counter, MovementFlags.Forward, 1.4f, _ => { }));
        await scene.Client.CollectThroughBarrierAsync();

        // The only ack the rooted-while-moving check exempts (vmangos MovementAnticheat.cpp:777-780). Without the exemption this block scores Physics.
        Assert.Empty(scene.Seen);
        // The ack was matched and judged, not dropped: its block was stored and the order applied.
        (float x, bool root, bool stillRooted) = await scene.Host.OnWorldAsync(() => (scene.Player.X, scene.Player.Movement.HasFlag(MovementFlags.Root), scene.Player.IsRooted));
        Assert.Equal(scene.X + 1.4f, x, 0.01f);
        Assert.False(root);
        Assert.True(stillRooted);
    }

    [Fact]
    public async Task AFlagAck_UnderARoot_CarryingAMovingBlock_IsScoredAsMovingWhileRooted()
    {
        await using Scene scene = await StartAsync("ROOTEDFLAG", "Rootedflag");
        await scene.BaselineAsync();
        await RootAsync(scene);
        await scene.Host.OnWorldAsync(() => MovementControl.Request(scene.Player, MovementChangeType.Hover, apply: true));
        await scene.Client.CollectThroughBarrierAsync();

        await Task.Delay(100);
        scene.Time += 100;
        uint counter = await scene.LastCounterAsync();
        await scene.Client.SendAsync(WorldOpcode.CmsgMoveHoverAck, scene.Ack(counter, MovementFlags.Forward | MovementFlags.Root | MovementFlags.Hover, 0.7f, w => w.WriteUInt32(1)));
        await scene.Client.CollectThroughBarrierAsync();

        Assert.Contains(scene.Seen, f => f.Type == AntiCheatViolation.Physics);
    }

    [Fact]
    public async Task ASpeedAck_UnderARoot_CarryingAMovingBlock_IsScoredAsMovingWhileRooted()
    {
        await using Scene scene = await StartAsync("ROOTEDSPEED", "Rootedspeed");
        await scene.BaselineAsync();
        await RootAsync(scene);
        float target = UnitSpeed.BaseSpeed(MoveType.Run) * 1.4f;
        await scene.Host.OnWorldAsync(() => UnitSpeed.SetRate(scene.Player, MoveType.Run, 1.4f));
        await scene.Client.ReadUntilAsync(SpeedPackets.ForceOpcode(MoveType.Run));

        await Task.Delay(100);
        scene.Time += 100;
        uint counter = await scene.LastCounterAsync();
        await scene.Client.SendAsync(SpeedPackets.AckOpcode(MoveType.Run), scene.Ack(counter, MovementFlags.Forward | MovementFlags.Root, 0.7f, w => w.WriteSingle(target)));
        await scene.Client.CollectThroughBarrierAsync();

        Assert.Contains(scene.Seen, f => f.Type == AntiCheatViolation.Physics);
    }
}

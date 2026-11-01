using System.Buffers.Binary;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Tests.PacketHeuristics;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// World-thread audit lane 7: a bot's movement packet is written into a reused writer and copied into one exact-length buffer its session keeps
/// (<c>WorldSession.TryManagedActionSpan</c>) instead of a new writer plus <c>ToArray()</c> every step. These tests pin the packet bytes (golden),
/// the buffer reuse, and the cases where a fresh array must be used because something could keep the bytes.
/// </summary>
public sealed class PlayerbotMovementPacketReuseTests
{
    private static byte[] ExpectedHeartbeat(uint flags, uint time, float x, float y, float z, float o, uint fall)
    {
        var bytes = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), time);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(8), x);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(12), y);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(16), z);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(20), o);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), fall);
        return bytes;
    }

    [Fact]
    public void ReusedWriter_ProducesTheSameBytesAsAFreshWriterEveryStep_AcrossLengthChanges()
    {
        var reused = new PacketWriter(64);
        var plain = new MovementInfo { Flags = MovementFlags.Forward, Time = 1234, X = 1.5f, Y = -2.25f, Z = 30f, Orientation = 0.75f, FallTime = 0 };
        var swimming = plain;
        swimming.Flags |= MovementFlags.Swimming;
        swimming.Pitch = 0.5f;
        var jumping = plain;
        jumping.Flags |= MovementFlags.Jumping;
        jumping.JumpZSpeed = 7f;
        jumping.JumpCosAngle = 1f;
        jumping.JumpSinAngle = 0f;
        jumping.JumpXySpeed = 7f;

        foreach (MovementInfo step in new[] { plain, swimming, plain, jumping, plain, plain })
        {
            var fresh = new PacketWriter(64);
            step.Write(fresh);
            reused.Reset();
            step.Write(reused);
            Assert.Equal(fresh.ToArray(), reused.AsSpan().ToArray());
        }

        reused.Reset();
        plain.Write(reused);
        Assert.Equal(ExpectedHeartbeat((uint)MovementFlags.Forward, 1234, 1.5f, -2.25f, 30f, 0.75f, 0), reused.AsSpan().ToArray());
    }

    private static byte[] HeartbeatOf(Player player)
    {
        var writer = new PacketWriter(64);
        MovementInfo movement = player.Movement;
        movement.Write(writer);
        return writer.ToArray();
    }

    [Fact]
    public async Task SpanDispatch_ReusesOneBufferForEqualLengthPackets_AndDispatchesEachOne()
    {
        await using var host = WorldTestHost.Start();
        WorldSession bot = await PacketHeuristicsTestKit.EnterBotAsync(host, "MPRBOTA", "Mprbotaone");
        try
        {
            int dispatched = 0;
            bot.ManagedDispatchObserver = _ => dispatched++;
            (byte[]? first, byte[]? second, byte[] expected, bool both) = await host.World.InvokeAsync(() =>
            {
                byte[] bytes = HeartbeatOf(bot.Player!);
                bool a = bot.TryManagedActionSpan(WorldOpcode.MsgMoveHeartbeat, bytes);
                byte[]? one = bot.ManagedScratch;
                bool b = bot.TryManagedActionSpan(WorldOpcode.MsgMoveHeartbeat, bytes);
                return (one, bot.ManagedScratch, bytes, a && b);
            });

            Assert.NotNull(first);
            Assert.Same(first, second);
            Assert.Equal(expected, first);
            Assert.Equal(2, dispatched);
            Assert.True(both);
        }
        finally
        {
            bot.Kick("test over");
            await bot.ManagedClosed;
        }
    }

    [Fact]
    public async Task SpanDispatch_ANewLengthGetsAnExactLengthBuffer()
    {
        await using var host = WorldTestHost.Start();
        WorldSession bot = await PacketHeuristicsTestKit.EnterBotAsync(host, "MPRBOTB", "Mprbotbone");
        try
        {
            (int shortLength, int longLength) = await host.World.InvokeAsync(() =>
            {
                byte[] bytes = HeartbeatOf(bot.Player!);
                bot.TryManagedActionSpan(WorldOpcode.MsgMoveHeartbeat, bytes);
                int a = bot.ManagedScratch!.Length;
                byte[] longer = new byte[bytes.Length + 4];
                bytes.CopyTo(longer, 0);
                bot.TryManagedActionSpan(WorldOpcode.MsgMoveHeartbeat, longer);
                return (a, bot.ManagedScratch!.Length);
            });

            Assert.Equal(28, shortLength);
            Assert.Equal(32, longLength);
        }
        finally
        {
            bot.Kick("test over");
            await bot.ManagedClosed;
        }
    }

    [Fact]
    public async Task SpanDispatch_WithAPacketObserverAttached_NeverTouchesTheReusedBuffer()
    {
        await using var host = WorldTestHost.Start();
        WorldSession bot = await PacketHeuristicsTestKit.EnterBotAsync(host, "MPRBOTC", "Mprbotcone");
        try
        {
            bot.ManagedPacketObserver = (_, _) => { };
            byte[]? scratch = await host.World.InvokeAsync(() =>
            {
                bot.TryManagedActionSpan(WorldOpcode.MsgMoveHeartbeat, HeartbeatOf(bot.Player!));
                return bot.ManagedScratch;
            });

            Assert.Null(scratch);
        }
        finally
        {
            bot.ManagedPacketObserver = null;
            bot.Kick("test over");
            await bot.ManagedClosed;
        }
    }

    [Fact]
    public async Task SpanDispatch_OffTheWorldThread_RefusesLikeTheArrayOverloadAndKeepsNoBuffer()
    {
        await using var host = WorldTestHost.Start();
        WorldSession bot = await PacketHeuristicsTestKit.EnterBotAsync(host, "MPRBOTD", "Mprbotdone");
        try
        {
            byte[] bytes = await host.World.InvokeAsync(() => HeartbeatOf(bot.Player!));
            Assert.False(bot.TryManagedActionSpan(WorldOpcode.MsgMoveHeartbeat, bytes));
            Assert.Null(bot.ManagedScratch);
        }
        finally
        {
            bot.Kick("test over");
            await bot.ManagedClosed;
        }
    }
}

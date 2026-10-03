using System.Collections.Concurrent;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests.Locomotion;

/// <summary>The movement handler runs the locomotion observers around the stored block (vmangos MovementHandler.cpp:333-344).</summary>
public sealed class ObserverSeamTests
{
    private sealed class Spy : IClientMovementObserver
    {
        public ConcurrentQueue<string> Log { get; } = new();

        public void BeforeApply(MovementObserverContext context, in MovementInfo previous, ref MovementInfo incoming)
        {
            Log.Enqueue($"before {context.Opcode} prev.z={previous.Z} in.z={incoming.Z} stored.z={context.Player.Movement.Z}");
            if (incoming.Z == 50f)
            {
                incoming.Z = 51f; // an observer may correct the block before it is stored
            }
        }

        public void AfterApply(MovementObserverContext context, in MovementInfo previous)
            => Log.Enqueue($"after prev.z={previous.Z} stored.z={context.Player.Movement.Z}");
    }

    private static byte[] Block(float z, MovementFlags flags = MovementFlags.None)
    {
        var writer = new PacketWriter(48);
        new MovementInfo { Flags = flags, Time = 100, X = -8949.95f, Y = -132.493f, Z = z, Orientation = 1 }.Write(writer);
        return writer.ToArray();
    }

    [Fact]
    public async Task Observers_RunBeforeAndAfterTheBlockIsStored_AndMayCorrectIt()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("MOVER", "Mover");
        await client.CollectAsync();
        var spy = new Spy();
        await host.OnWorldAsync(() => MovementObservers.Register(host.World, spy));

        await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, Block(40f));
        await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, Block(50f));
        await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, Block(60f));
        await host.WaitForWorldAsync(() => spy.Log.Count >= 6, "six observer calls");

        string[] log = [.. spy.Log];
        Assert.Contains("before MsgMoveHeartbeat prev.z=40 in.z=50 stored.z=40", log); // previous is the last stored block
        Assert.Contains("after prev.z=40 stored.z=51", log);                         // the corrected value was stored
        Assert.Contains("before MsgMoveHeartbeat prev.z=51 in.z=60 stored.z=51", log);
        Assert.Equal(60f, await host.PlayerStateAsync("Mover", p => p.Movement.Z));
    }

    [Fact]
    public async Task AFailingObserver_DoesNotStopTheMovement()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("MOVER", "Mover");
        await client.CollectAsync();
        await host.OnWorldAsync(() => MovementObservers.Register(host.World, new Thrower()));

        await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, Block(70f));
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Mover")!.Movement.Z == 70f, "the block to be stored");
    }

    private sealed class Thrower : IClientMovementObserver
    {
        public void BeforeApply(MovementObserverContext context, in MovementInfo previous, ref MovementInfo incoming) => throw new InvalidOperationException("boom");

        public void AfterApply(MovementObserverContext context, in MovementInfo previous) => throw new InvalidOperationException("boom");
    }
}

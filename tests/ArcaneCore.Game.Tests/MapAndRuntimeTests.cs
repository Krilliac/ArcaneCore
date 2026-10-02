using System.Buffers.Binary;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests;

/// <summary>Map visibility and the world runtime, driven tick by tick without sockets.</summary>
public sealed class MapAndRuntimeTests
{
    [Fact]
    public void AddPlayer_SendsSelfCreate_ThenCreatesWithPlayersInRange()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        var sa = new FakeSession(1);
        var sb = new FakeSession(2);
        world.AddPlayer(TestWorld.CreatePlayer(1, 0, 0, sa));
        world.AddPlayer(TestWorld.CreatePlayer(2, 10, 0, sb));

        // Each self create goes out at once, as its own packet.
        Assert.Equal(WorldOpcode.SmsgUpdateObject, sa.Next().Opcode);
        Assert.Equal(WorldOpcode.SmsgUpdateObject, sb.Next().Opcode);
        Assert.True(sa.Sent.IsEmpty);

        // B's visibility pass queued creates both ways; the next tick flushes them.
        world.RunTick(50);
        Assert.Equal((byte)ObjectUpdateType.CreateObject, sa.Next().Payload[5]);
        Assert.Equal((byte)ObjectUpdateType.CreateObject, sb.Next().Payload[5]);
    }

    [Fact]
    public void PlayersBeyondRange_DoNotSeeEachOther()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        var sa = new FakeSession(1);
        var sb = new FakeSession(2);
        world.AddPlayer(TestWorld.CreatePlayer(1, 0, 0, sa));
        world.AddPlayer(TestWorld.CreatePlayer(2, 500, 0, sb));
        sa.Clear();
        sb.Clear();

        world.RunTick(50);

        Assert.True(sa.Sent.IsEmpty);
        Assert.True(sb.Sent.IsEmpty);
    }

    [Fact]
    public void RemovePlayer_DestroysForObservers_AndQueuesASave()
    {
        var saves = new RecordingSaveQueue();
        using WorldRuntime world = TestWorld.CreateRuntime(saves);
        var sa = new FakeSession(1);
        var sb = new FakeSession(2);
        Player a = TestWorld.CreatePlayer(1, 0, 0, sa);
        Player b = TestWorld.CreatePlayer(2, 5, 0, sb);
        world.AddPlayer(a);
        world.AddPlayer(b);
        world.RunTick(50);
        sa.Clear();

        world.RemovePlayer(b);

        (WorldOpcode op, byte[] payload) = sa.Next();
        Assert.Equal(WorldOpcode.SmsgDestroyObject, op);
        Assert.Equal(2ul, BinaryPrimitives.ReadUInt64LittleEndian(payload));
        Assert.False(world.IsOnline(b.Guid));
        Assert.Null(b.Map);
        Assert.Equal(2, Assert.Single(saves.Saved).Id);
    }

    [Fact]
    public void Autosave_SnapshotsEveryOnlinePlayerAfterTheInterval()
    {
        var saves = new RecordingSaveQueue();
        using WorldRuntime world = TestWorld.CreateRuntime(saves, autosaveMs: 1000);
        world.AddPlayer(TestWorld.CreatePlayer(1, 0, 0, new FakeSession(1)));
        world.AddPlayer(TestWorld.CreatePlayer(2, 0, 0, new FakeSession(2)));

        world.RunTick(600);
        Assert.Empty(saves.Saved);

        world.RunTick(600);
        Assert.Equal([1, 2], saves.Saved.Select(s => s.Id).Order());
    }

    [Fact]
    public void AddPlayer_WhenAlreadyOnline_Throws()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        world.AddPlayer(TestWorld.CreatePlayer(1, 0, 0, new FakeSession(1)));
        Assert.Throws<InvalidOperationException>(() => world.AddPlayer(TestWorld.CreatePlayer(1, 0, 0, new FakeSession(1))));
    }

    [Fact]
    public async Task InvokeAsync_RunsAtTheNextTick()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Task<int> pending = world.InvokeAsync(() => 42);
        Assert.False(pending.IsCompleted);

        world.RunTick(50);

        Assert.Equal(42, await pending);
    }

    [Fact]
    public void VisibilityDistance_IsTwoDimensional_WithGreyHysteresis()
    {
        // Range for two default players: 100 + 0.389 * 2 = 100.778 yards (+1 grey once visible).
        Player viewer = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());

        Assert.True(Map.IsWithinVisibilityDistance(viewer, At(100.5f, 0), alreadyVisible: false));
        Assert.False(Map.IsWithinVisibilityDistance(viewer, At(101.0f, 0), alreadyVisible: false));
        Assert.True(Map.IsWithinVisibilityDistance(viewer, At(101.0f, 0), alreadyVisible: true));
        Assert.False(Map.IsWithinVisibilityDistance(viewer, At(102.0f, 0), alreadyVisible: true));

        // Height is ignored (vmangos IsWithinDistInMap(..., is3D = false)).
        Player high = At(50f, 0);
        high.Z = 500f;
        Assert.True(Map.IsWithinVisibilityDistance(viewer, high, alreadyVisible: false));

        static Player At(float x, float y) => TestWorld.CreatePlayer(2, x, y, new FakeSession(2));
    }
}

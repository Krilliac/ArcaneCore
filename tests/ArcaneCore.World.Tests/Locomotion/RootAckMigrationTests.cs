using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests.Locomotion;

/// <summary>
/// The root ack on the pending-movement-change ledger (vmangos WorldSession::HandleMoveRootAck,
/// MovementHandler.cpp:646-745: an ack is honoured only when it matches a change the server sent).
/// </summary>
public sealed class RootAckMigrationTests
{
    private static byte[] RootAck(uint counter, MovementFlags flags)
    {
        var info = new MovementInfo { Flags = flags, Time = 500, X = -8949.95f, Y = -132.493f, Z = 83.5312f };
        var writer = new PacketWriter(48);
        writer.WriteUInt64(1);
        writer.WriteUInt32(counter);
        info.Write(writer);
        return writer.ToArray();
    }

    private static byte[] Heartbeat()
    {
        var info = new MovementInfo { Flags = MovementFlags.None, Time = 600, X = -8949.95f, Y = -132.493f, Z = 83.5312f };
        var writer = new PacketWriter(48);
        info.Write(writer);
        return writer.ToArray();
    }

    /// <summary>Read the watcher's packets up to a heartbeat marker and return how many MSG_MOVE_ROOT arrived.</summary>
    private static async Task<int> CountRootRelaysUntilHeartbeat(WorldTestClient watcher)
    {
        int roots = 0;
        while (true)
        {
            (WorldOpcode opcode, _) = await watcher.ReadAsync();
            if (opcode == WorldOpcode.MsgMoveRoot)
            {
                roots++;
            }
            else if (opcode == WorldOpcode.MsgMoveHeartbeat)
            {
                return roots;
            }
        }
    }

    [Fact]
    public async Task RootAck_WithACounterThatWasNeverIssued_IsIgnored_AndCounted()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient rooted = await host.EnterWorldAsync("ROOTED", "Rooted");
        await using WorldTestClient watcher = await host.EnterWorldAsync("WATCHER", "Watcher");
        await rooted.CollectAsync();
        await watcher.CollectAsync();

        // The logout request roots the player with movement counter 0.
        await rooted.SendAsync(WorldOpcode.CmsgLogoutRequest, []);
        await rooted.ReadUntilAsync(WorldOpcode.SmsgLogoutResponse);

        await rooted.SendAsync(WorldOpcode.CmsgForceMoveRootAck, RootAck(counter: 99, MovementFlags.Root)); // never issued
        await rooted.SendAsync(WorldOpcode.CmsgForceMoveRootAck, RootAck(counter: 0, MovementFlags.Root));
        await rooted.SendAsync(WorldOpcode.MsgMoveHeartbeat, Heartbeat());

        Assert.Equal(1, await CountRootRelaysUntilHeartbeat(watcher));
    }

    [Fact]
    public async Task RootAck_IsAcceptedOnlyOnce_PerIssuedChange()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient rooted = await host.EnterWorldAsync("ROOTED", "Rooted");
        await using WorldTestClient watcher = await host.EnterWorldAsync("WATCHER", "Watcher");
        await rooted.CollectAsync();
        await watcher.CollectAsync();

        await rooted.SendAsync(WorldOpcode.CmsgLogoutRequest, []);
        await rooted.ReadUntilAsync(WorldOpcode.SmsgLogoutResponse);

        await rooted.SendAsync(WorldOpcode.CmsgForceMoveRootAck, RootAck(counter: 0, MovementFlags.Root));
        await rooted.SendAsync(WorldOpcode.CmsgForceMoveRootAck, RootAck(counter: 0, MovementFlags.Root)); // replay
        await rooted.SendAsync(WorldOpcode.MsgMoveHeartbeat, Heartbeat());

        Assert.Equal(1, await CountRootRelaysUntilHeartbeat(watcher));
    }
}

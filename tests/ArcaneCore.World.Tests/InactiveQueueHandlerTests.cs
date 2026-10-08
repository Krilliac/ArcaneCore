using System.Buffers.Binary;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests;

public sealed class InactiveQueueHandlerTests
{
    [Fact]
    public void StatusPollsAreDiscoveredAsWorldHandlersWithoutRegisteringQueueActions()
    {
        OpcodeTable table = WorldServiceCollectionExtensions.BuildOpcodeTable();
        foreach (WorldOpcode opcode in new[] { WorldOpcode.CmsgBattlefieldStatus, WorldOpcode.CmsgMeetingstoneInfo })
        {
            Assert.True(table.TryGet(opcode, out OpcodeHandler handler), $"missing {opcode} handler");
            Assert.NotNull(handler.World);
            Assert.Null(handler.Session);
            Assert.Equal(SessionStates.InWorld, handler.AllowedStates);
            Assert.True(handler.AllowsState(SessionState.InWorld));
            Assert.False(handler.AllowsState(SessionState.CharacterSelect));
            Assert.False(handler.AllowsState(SessionState.LoggingIn));
        }

        foreach (WorldOpcode opcode in new[] { WorldOpcode.CmsgMeetingstoneJoin, WorldOpcode.CmsgMeetingstoneLeave })
        {
            Assert.False(table.TryGet(opcode, out _));
        }

        // The battleground queue actions belong to the battleground handlers (docs/areas/battlegrounds.md).
        foreach (WorldOpcode opcode in new[] { WorldOpcode.CmsgBattlemasterJoin, WorldOpcode.CmsgBattlefieldPort })
        {
            Assert.True(table.TryGet(opcode, out OpcodeHandler handler), $"missing {opcode} handler");
            Assert.NotNull(handler.World);
        }
    }

    [Theory]
    [InlineData(WorldOpcode.CmsgBattlefieldStatus)]
    [InlineData(WorldOpcode.CmsgMeetingstoneInfo)]
    public async Task StatusPollBeforeAuthenticationDisconnects(WorldOpcode opcode)
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.ConnectAsync();
        Assert.Equal(WorldOpcode.SmsgAuthChallenge, (await client.ReadAsync()).Opcode);

        await client.SendAsync(opcode, []);

        Assert.True(await client.IsClosedByServerAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task CharacterScreenPollsAreDroppedAndDoNotLeakIntoLogin(int bodyLength)
    {
        await using WorldTestHost host = WorldTestHost.Start();
        byte[] key = await host.AddAccountAsync("QUEUESELECT");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("QUEUESELECT", key);

        // Body validation belongs to the in-world handler, which does not run here.
        await client.SendAsync(WorldOpcode.CmsgBattlefieldStatus, new byte[bodyLength]);
        await client.SendAsync(WorldOpcode.CmsgMeetingstoneInfo, new byte[bodyLength]);
        await client.SendAsync(WorldOpcode.CmsgCharEnum, []);
        (WorldOpcode opcode, byte[] payload) = await client.ReadAsync();
        Assert.Equal(WorldOpcode.SmsgCharEnum, opcode);
        Assert.Equal(new byte[] { 0 }, payload);

        await client.CreateCharacterAsync("Queueselect");
        await client.LoginAsync(1);
        Assert.DoesNotContain(client.LastLoginPackets, p => p.Opcode is
            WorldOpcode.SmsgMeetingstoneSetqueue or WorldOpcode.SmsgBattlefieldStatus);

        await client.SendAsync(WorldOpcode.CmsgPlayedTime, []);
        await ReadPlayedTimeWithoutQueuePacketsAsync(client);
        Assert.Equal(SessionState.InWorld, await host.PlayerStateAsync("Queueselect", p => ((WorldSession)p.Session).State));
    }

    [Theory]
    [InlineData(WorldOpcode.CmsgBattlefieldStatus, 1)]
    [InlineData(WorldOpcode.CmsgBattlefieldStatus, 8)]
    [InlineData(WorldOpcode.CmsgMeetingstoneInfo, 1)]
    [InlineData(WorldOpcode.CmsgMeetingstoneInfo, 8)]
    public async Task InWorldStatusPollWithSurplusBodyDisconnects(WorldOpcode opcode, int length)
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("QUEUEBADBODY", "Queuebad");
        await client.CollectAsync();

        await client.SendAsync(opcode, new byte[length]);

        Assert.True(await client.IsClosedByServerAsync());
    }

    [Fact]
    public async Task QueuedStatusPollCannotRunAgainstAnotherSessionsPlayer()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient owner = await host.EnterWorldAsync("QUEUEOWNER", "Queueowner");
        await using WorldTestClient other = await host.EnterWorldAsync("QUEUEOTHER", "Queueother");
        await owner.CollectAsync();
        await other.CollectAsync();
        WorldSession session = await host.PlayerStateAsync("Queueowner", p => (WorldSession)p.Session);
        Player foreignPlayer = await host.PlayerAsync("Queueother");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();

        // Hold this host's map update until its independent network task has queued the poll.
        Task dispatch = host.OnWorldAsync(() =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("timed out releasing the ownership probe");
            }

            session.ProcessWorldPackets(foreignPlayer);
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await owner.SendAsync(WorldOpcode.CmsgMeetingstoneInfo, []);
            await owner.SendAsync(WorldOpcode.CmsgPing, [0xAA, 0x55, 0, 0, 0, 0, 0, 0]);
            (WorldOpcode opcode, byte[] payload) = await owner.ReadAsync();
            Assert.Equal(WorldOpcode.SmsgPong, opcode);
            Assert.Equal(0x55AAu, BinaryPrimitives.ReadUInt32LittleEndian(payload));
        }
        finally
        {
            release.Set();
            await dispatch;
        }

        // A same-session map operation is a barrier after the mismatched dispatch dropped the poll.
        await owner.SendAsync(WorldOpcode.CmsgPlayedTime, []);
        await ReadPlayedTimeWithoutQueuePacketsAsync(owner);
        await owner.SendAsync(WorldOpcode.CmsgMeetingstoneInfo, []);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 5 }, await owner.ReadUntilAsync(WorldOpcode.SmsgMeetingstoneSetqueue));
        Assert.DoesNotContain(await other.CollectAsync(), p => p.Opcode is
            WorldOpcode.SmsgMeetingstoneSetqueue or WorldOpcode.SmsgBattlefieldStatus);
    }

    [Fact]
    public async Task MeetingstoneIdleQueryPreservesAnExistingNormalGroup()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient leader = await host.EnterWorldAsync("QUEUEPARTY", "Queueparty");
        await using WorldTestClient member = await host.EnterWorldAsync("QUEUEMEMBER", "Queuemember");
        var invite = new PacketWriter(16);
        invite.WriteCString("Queuemember");

        await leader.SendAsync(WorldOpcode.CmsgGroupInvite, invite.ToArray());
        byte[] invited = await member.ReadUntilAsync(WorldOpcode.SmsgGroupInvite);
        Assert.Equal("Queueparty", new PacketReader(invited).ReadCString());
        await member.SendAsync(WorldOpcode.CmsgGroupAccept, []);
        await ReadFormedPartyListAsync(leader);
        await ReadFormedPartyListAsync(member);
        GroupSnapshot beforeLeader = await GroupSnapshotAsync(host, "Queueparty");
        GroupSnapshot beforeMember = await GroupSnapshotAsync(host, "Queuemember");
        Assert.True(beforeLeader.IsCreated);
        Assert.False(beforeLeader.IsRaid);
        Assert.NotEqual(0u, beforeLeader.Id);
        Assert.Equal(1ul, beforeLeader.Leader);
        Assert.Equal(new ulong[] { 1, 2 }, beforeLeader.Members);
        AssertSameGroup(beforeLeader, beforeMember);

        await member.SendAsync(WorldOpcode.CmsgMeetingstoneInfo, []);

        Assert.Equal(new byte[] { 0, 0, 0, 0, 5 }, await member.ReadUntilAsync(WorldOpcode.SmsgMeetingstoneSetqueue));
        AssertSameGroup(beforeLeader, await GroupSnapshotAsync(host, "Queueparty"));
        AssertSameGroup(beforeMember, await GroupSnapshotAsync(host, "Queuemember"));
        Assert.False(host.Opcodes.TryGet(WorldOpcode.CmsgMeetingstoneJoin, out _));
    }

    private static async Task ReadFormedPartyListAsync(WorldTestClient client)
    {
        for (int i = 0; i < 32; i++)
        {
            byte[] list = await client.ReadUntilAsync(WorldOpcode.SmsgGroupList);
            if (list.Length >= 6 && BinaryPrimitives.ReadUInt32LittleEndian(list.AsSpan(2)) == 1)
            {
                Assert.Equal(0, list[0]);
                return;
            }
        }

        throw new Xunit.Sdk.XunitException("formed two-player party list did not arrive within 32 lists");
    }

    private static Task<GroupSnapshot> GroupSnapshotAsync(WorldTestHost host, string name)
        => host.PlayerStateAsync(name, player =>
        {
            Group group = ((WorldSession)player.Session).Services.GetRequiredService<SocialFeature>()
                .Context.Groups.GetGroup(player.Guid) ?? throw new InvalidOperationException($"{name} has no group");
            return new GroupSnapshot(group.Id, group.LeaderGuid.Value, group.IsCreated, group.IsRaid,
                group.Members.Select(m => m.Guid.Value).Order().ToArray());
        });

    private static void AssertSameGroup(GroupSnapshot expected, GroupSnapshot actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Leader, actual.Leader);
        Assert.Equal(expected.IsCreated, actual.IsCreated);
        Assert.Equal(expected.IsRaid, actual.IsRaid);
        Assert.Equal(expected.Members, actual.Members);
    }

    private sealed record GroupSnapshot(uint Id, ulong Leader, bool IsCreated, bool IsRaid, ulong[] Members);

    private static async Task ReadPlayedTimeWithoutQueuePacketsAsync(WorldTestClient client)
    {
        for (int i = 0; i < 32; i++)
        {
            (WorldOpcode opcode, _) = await client.ReadAsync();
            Assert.NotEqual(WorldOpcode.SmsgMeetingstoneSetqueue, opcode);
            Assert.NotEqual(WorldOpcode.SmsgBattlefieldStatus, opcode);
            if (opcode == WorldOpcode.SmsgPlayedTime)
            {
                return;
            }
        }

        throw new Xunit.Sdk.XunitException("played-time barrier did not arrive within 32 frames");
    }
}

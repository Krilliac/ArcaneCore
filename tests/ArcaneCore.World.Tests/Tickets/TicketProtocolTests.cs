using System.Buffers.Binary;
using ArcaneCore.Protocol;
using ArcaneCore.World;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Tickets;
using ArcaneCore.World.Tests;
using Xunit;

namespace ArcaneCore.World.Tests.Tickets;

/// <summary>
/// The ticket queue status (CMSG_GMTICKET_SYSTEMSTATUS). The ticket requests themselves are the GM audit lane's (TicketTests); since that
/// lane accepts tickets, the queue reports enabled.
/// </summary>
public sealed class TicketProtocolTests
{
    [Fact]
    public void HandlerGroupsRegisterTheStatusAndTheCreateRequests()
    {
        OpcodeTable table = WorldServiceCollectionExtensions.BuildOpcodeTable();

        Assert.True(table.TryGet(WorldOpcode.CmsgGmticketSystemstatus, out OpcodeHandler systemStatus));
        Assert.NotNull(systemStatus.World);
        Assert.True(table.TryGet(WorldOpcode.CmsgGmticketCreate, out OpcodeHandler create));
        Assert.NotNull(create.World);
    }

    [Fact]
    public void SystemStatusReplyIsAFourByteQueueStatus()
    {
        byte[] payload = TicketPackets.BuildSystemStatus(TicketPackets.QueueEnabled);

        Assert.Equal(4, payload.Length);
        Assert.Equal(TicketPackets.QueueEnabled, BinaryPrimitives.ReadUInt32LittleEndian(payload));
    }

    [Fact]
    public async Task LoggedInClientGetsEnabledSystemStatusFromRealHandler()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("TICKET_STATUS", "TicketStatus");

        await client.SendAsync(WorldOpcode.CmsgGmticketSystemstatus, []);
        byte[] response = await client.ReadUntilAsync(WorldOpcode.SmsgGmticketSystemstatus);

        Assert.Equal(4, response.Length);
        Assert.Equal(TicketPackets.QueueEnabled, BinaryPrimitives.ReadUInt32LittleEndian(response));
    }

    [Fact]
    public async Task MalformedStatusRequestProducesNoProtocolReply()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("TICKET_BAD", "TicketBad");

        await client.SendAsync(WorldOpcode.CmsgGmticketSystemstatus, [1]);

        Assert.Empty(await client.CollectAsync(TimeSpan.FromMilliseconds(250)));
    }
}

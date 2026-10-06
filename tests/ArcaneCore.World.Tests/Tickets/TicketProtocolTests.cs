using System.Buffers.Binary;
using ArcaneCore.Protocol;
using ArcaneCore.World;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Tickets;
using ArcaneCore.World.Tests;
using Xunit;

namespace ArcaneCore.World.Tests.Tickets;

public sealed class TicketProtocolTests
{
    [Fact]
    public void HandlerGroupRegistersOnlySupportedDisabledQueueRequests()
    {
        OpcodeTable table = WorldServiceCollectionExtensions.BuildOpcodeTable();

        Assert.True(table.TryGet(WorldOpcode.CmsgGmticketSystemstatus, out OpcodeHandler systemStatus));
        Assert.NotNull(systemStatus.World);
        Assert.True(table.TryGet(WorldOpcode.CmsgGmticketCreate, out OpcodeHandler create));
        Assert.NotNull(create.World);
    }

    [Fact]
    public void SystemStatusReplyIsFourByteDisabledQueueStatus()
    {
        byte[] payload = TicketPackets.BuildDisabledSystemStatus();

        Assert.Equal(4, payload.Length);
        Assert.Equal(TicketPackets.QueueDisabled, BinaryPrimitives.ReadUInt32LittleEndian(payload));
    }

    [Fact]
    public void CreateReplyIsFourByteUnavailableErrorAndEnvelopeBoundsAreSourceDefined()
    {
        byte[] payload = TicketPackets.BuildCreateError();

        Assert.Equal(4, payload.Length);
        Assert.Equal(TicketPackets.CreateError, BinaryPrimitives.ReadUInt32LittleEndian(payload));
        Assert.False(TicketPackets.IsCreatePayloadLengthValid(TicketPackets.CreateMinimumPayload - 1));
        Assert.True(TicketPackets.IsCreatePayloadLengthValid(TicketPackets.CreateMinimumPayload));
        Assert.True(TicketPackets.IsCreatePayloadLengthValid(TicketPackets.CreateMaximumPayload));
        Assert.False(TicketPackets.IsCreatePayloadLengthValid(TicketPackets.CreateMaximumPayload + 1));
    }

    [Fact]
    public async Task LoggedInClientGetsDisabledSystemStatusFromRealHandler()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("TICKET_STATUS", "TicketStatus");

        await client.SendAsync(WorldOpcode.CmsgGmticketSystemstatus, []);
        byte[] response = await client.ReadUntilAsync(WorldOpcode.SmsgGmticketSystemstatus);

        Assert.Equal(4, response.Length);
        Assert.Equal(TicketPackets.QueueDisabled, BinaryPrimitives.ReadUInt32LittleEndian(response));
    }

    [Fact]
    public async Task ObservedFortySixByteCreateGetsUnavailableErrorWithoutTicketPersistence()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("TICKET_CREATE", "TicketCreate");

        // The observed frame is 46 bytes. The disabled handler validates only the source-defined
        // envelope and deliberately never decodes this body or its text fields.
        await client.SendAsync(WorldOpcode.CmsgGmticketCreate, new byte[46]);
        byte[] response = await client.ReadUntilAsync(WorldOpcode.SmsgGmticketCreate);

        Assert.Equal(4, response.Length);
        Assert.Equal(TicketPackets.CreateError, BinaryPrimitives.ReadUInt32LittleEndian(response));
        Assert.Empty(await client.CollectAsync(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task MalformedTicketRequestsProduceNoProtocolReply()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("TICKET_BAD", "TicketBad");

        await client.SendAsync(WorldOpcode.CmsgGmticketSystemstatus, [1]);
        await client.SendAsync(WorldOpcode.CmsgGmticketCreate, new byte[TicketPackets.CreateMinimumPayload - 1]);

        Assert.Empty(await client.CollectAsync(TimeSpan.FromMilliseconds(250)));
    }
}

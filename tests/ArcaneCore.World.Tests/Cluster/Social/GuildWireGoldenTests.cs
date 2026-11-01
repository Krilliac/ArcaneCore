using System.Text;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Protocol;
using ArcaneCore.World.Cluster.Social;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Social;

/// <summary>
/// Golden bytes of the guild operations carried over the peer link (<see cref="PresenceOp.GuildForward"/>, <see cref="PresenceOp.GuildDrain"/>). Like
/// <see cref="GroupWireGoldenTests"/>, the expected arrays are written out by hand (little-endian integers, 7-bit length-prefixed UTF-8 strings, one byte per bool), so a
/// change made to both the encoder and the decoder, a renumbered op, event kind or <see cref="GuildRequestKind"/> fails here instead of breaking a mixed-version cluster.
/// </summary>
public sealed class GuildWireGoldenTests
{
    private static byte[] Hex(string hex) => Convert.FromHexString(hex);

    private static byte[] Encode(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            write(w);
        }

        return stream.ToArray();
    }

    private static BinaryReader Open(byte[] bytes) => new(new MemoryStream(bytes), Encoding.UTF8);

    // PetitionData: id 1, owner 5, charter item 500, "Ab", one signature (player 6, account 106).
    private const string PetitionHex = "01000000" + "05000000" + "F4010000" + "024162" + "01000000" + "06000000" + "6A000000";

    private static PetitionData SamplePetition() => new(1, 5, 500, "Ab", [new PetitionSignatureData(6, 106)]);

    // GuildData: id 4, "Ab", leader 5, motd "M", info "", created 1000, emblem 1..5, one rank (0, "Gm", 255), one member (5, rank 0, "p", "o", level 10, zone 12, logout 0).
    private const string GuildHex =
        "04000000" + "024162" + "05000000" + "014D" + "00" + "E803000000000000" +
        "01000000" + "02000000" + "03000000" + "04000000" + "05000000" +
        "01000000" + "00" + "02476D" + "FF000000" +
        "01000000" + "05000000" + "00" + "0170" + "016F" + "0A" + "0C000000" + "0000000000000000";

    private static GuildData SampleGuild() => new(
        4, "Ab", 5, "M", string.Empty, 1000, 1, 2, 3, 4, 5, [new GuildRankData(0, "Gm", 255)], [new GuildMemberData(5, 0, "p", "o", 10, 12, 0)]);

    // Events.
    private const string DeliverHex = "01" + "0900000000000000" + "8300" + "02000000" + "0102";
    private const string GuildEventHex = "02" + "04000000" + "01" + GuildHex;
    private const string GuildGoneHex = "02" + "04000000" + "00";
    private const string PetitionEventHex = "03" + "01000000" + "01" + PetitionHex;
    private const string PetitionGoneHex = "03" + "01000000" + "00";
    private const string CompletedHex = "04" + "4D00000000000000" + "01";

    [Fact]
    public void GuildPresenceOps_ArePinned()
    {
        Assert.Equal(23, (byte)PresenceOp.GuildForward);
        Assert.Equal(24, (byte)PresenceOp.GuildDrain);
    }

    [Fact]
    public void GuildLinkEventKindValues_ArePinned()
    {
        Assert.Equal(1, (byte)GuildLinkEventKind.Deliver);
        Assert.Equal(2, (byte)GuildLinkEventKind.Guild);
        Assert.Equal(3, (byte)GuildLinkEventKind.Petition);
        Assert.Equal(4, (byte)GuildLinkEventKind.Completed);
    }

    [Fact]
    public void GuildRequestKindValues_ArePinned()
    {
        Assert.Equal(1, (byte)GuildRequestKind.Create);
        Assert.Equal(2, (byte)GuildRequestKind.Invite);
        Assert.Equal(17, (byte)GuildRequestKind.SetRank);
        Assert.Equal(20, (byte)GuildRequestKind.SetEmblem);
        Assert.Equal(21, (byte)GuildRequestKind.LoggedIn);
        Assert.Equal(22, (byte)GuildRequestKind.LoggedOut);
        Assert.Equal(23, (byte)GuildRequestKind.ForgetInvite);
        Assert.Equal(24, (byte)GuildRequestKind.AdminUninvite);
        Assert.Equal(30, (byte)GuildRequestKind.PetitionPut);
        Assert.Equal(31, (byte)GuildRequestKind.PetitionDelete);
        Assert.Equal(32, (byte)GuildRequestKind.PetitionSign);
        Assert.Equal(33, (byte)GuildRequestKind.PetitionTurnIn);
        Assert.Equal(34, (byte)GuildRequestKind.PetitionCharacterDeleted);
        Assert.Equal(35, (byte)GuildRequestKind.PetitionOffer);
    }

    [Fact]
    public void PlainRequest_HasTheGoldenLayout()
    {
        var request = new GuildRequest(GuildRequestKind.SetRank, ObjectGuid.Player(5), "Ab", "C", 2, 3, 7);
        byte[] expected = Hex("11" + "0500000000000000" + "024162" + "0143" + "02000000" + "03000000" + "0700000000000000" + "00" + "00000000" + "00");
        Assert.Equal(expected, Encode(w => GuildWireCodec.WriteRequest(w, request)));

        using BinaryReader reader = Open(expected);
        GuildRequest decoded = GuildWireCodec.ReadRequest(reader);
        Assert.Equal(request, decoded);
    }

    [Fact]
    public void RequestWithEmblemValues_HasTheGoldenLayout()
    {
        var request = new GuildRequest(GuildRequestKind.SetEmblem, ObjectGuid.Player(5), Values: [3, -1]);
        byte[] expected = Hex("14" + "0500000000000000" + "00" + "00" + "00000000" + "00000000" + "0000000000000000" + "00" + "00000000" + "01" + "02000000" + "03000000" + "FFFFFFFF");
        Assert.Equal(expected, Encode(w => GuildWireCodec.WriteRequest(w, request)));

        using BinaryReader reader = Open(expected);
        GuildRequest decoded = GuildWireCodec.ReadRequest(reader);
        Assert.Equal(GuildRequestKind.SetEmblem, decoded.Kind);
        Assert.Equal(new[] { 3, -1 }, decoded.Values);
    }

    [Fact]
    public void RequestWithAWholePetition_HasTheGoldenLayout()
    {
        var request = new GuildRequest(GuildRequestKind.PetitionPut, ObjectGuid.Player(5), Petition: SamplePetition());
        byte[] expected = Hex("1E" + "0500000000000000" + "00" + "00" + "00000000" + "00000000" + "0000000000000000" + "01" + PetitionHex + "00000000" + "00");
        Assert.Equal(expected, Encode(w => GuildWireCodec.WriteRequest(w, request)));

        using BinaryReader reader = Open(expected);
        GuildRequest decoded = GuildWireCodec.ReadRequest(reader);
        Assert.Equal(SamplePetition().Name, decoded.Petition!.Name);
        Assert.Equal(SamplePetition().Signatures, decoded.Petition.Signatures);
        Assert.Equal(500, decoded.Petition.CharterItemId);
    }

    [Fact]
    public void PetitionOfferRequest_CarriesTargetAndHeldFlagInTheNumbers()
    {
        var request = new GuildRequest(GuildRequestKind.PetitionOffer, ObjectGuid.Player(5), Item: 0x0000_0001_0000_01F4UL, A: 9, B: 1);
        byte[] expected = Hex("23" + "0500000000000000" + "00" + "00" + "09000000" + "01000000" + "F401000001000000" + "00" + "00000000" + "00");
        Assert.Equal(expected, Encode(w => GuildWireCodec.WriteRequest(w, request)));

        using BinaryReader reader = Open(expected);
        Assert.Equal(request, GuildWireCodec.ReadRequest(reader));
    }

    [Fact]
    public void DeliverEvent_HasTheGoldenLayout()
    {
        GuildLinkEvent e = GuildLinkEvent.OfDeliver(9, WorldOpcode.SmsgGuildInvite, [1, 2]);
        Assert.Equal(Hex(DeliverHex), Encode(w => GuildWireCodec.WriteEvent(w, e)));

        using BinaryReader reader = Open(Hex(DeliverHex));
        GuildLinkEvent back = GuildWireCodec.ReadEvent(reader);
        Assert.Equal(GuildLinkEventKind.Deliver, back.Kind);
        Assert.Equal(9UL, back.Character);
        Assert.Equal(WorldOpcode.SmsgGuildInvite, back.Opcode);
        Assert.Equal(new byte[] { 1, 2 }, back.Payload);
    }

    [Fact]
    public void GuildReplicaEvent_HasTheGoldenLayout_AndADisbandIsAnEmptyReplica()
    {
        Assert.Equal(Hex(GuildEventHex), Encode(w => GuildWireCodec.WriteEvent(w, GuildLinkEvent.OfGuild(4, SampleGuild()))));
        Assert.Equal(Hex(GuildGoneHex), Encode(w => GuildWireCodec.WriteEvent(w, GuildLinkEvent.OfGuild(4, null))));

        using BinaryReader reader = Open(Hex(GuildEventHex));
        GuildData data = GuildWireCodec.ReadEvent(reader).Guild!;
        Assert.Equal(SampleGuild().Ranks, data.Ranks);
        Assert.Equal(SampleGuild().Members, data.Members);
        Assert.Equal((SampleGuild().Id, SampleGuild().Name, SampleGuild().LeaderId, SampleGuild().Motd, SampleGuild().Info, SampleGuild().CreatedAt), (data.Id, data.Name, data.LeaderId, data.Motd, data.Info, data.CreatedAt));
        Assert.Equal((1, 2, 3, 4, 5), (data.EmblemStyle, data.EmblemColor, data.BorderStyle, data.BorderColor, data.BackgroundColor));

        using BinaryReader gone = Open(Hex(GuildGoneHex));
        GuildLinkEvent goneEvent = GuildWireCodec.ReadEvent(gone);
        Assert.Equal(4, goneEvent.Id);
        Assert.Null(goneEvent.Guild);
    }

    [Fact]
    public void PetitionReplicaEvent_HasTheGoldenLayout_AndARemovalIsAnEmptyReplica()
    {
        Assert.Equal(Hex(PetitionEventHex), Encode(w => GuildWireCodec.WriteEvent(w, GuildLinkEvent.OfPetition(1, SamplePetition()))));
        Assert.Equal(Hex(PetitionGoneHex), Encode(w => GuildWireCodec.WriteEvent(w, GuildLinkEvent.OfPetition(1, null))));

        using BinaryReader reader = Open(Hex(PetitionEventHex));
        PetitionData data = GuildWireCodec.ReadEvent(reader).Petition!;
        Assert.Equal(SamplePetition().Signatures, data.Signatures);
        Assert.Equal((5, 500, "Ab"), (data.OwnerId, data.CharterItemId, data.Name));

        using BinaryReader gone = Open(Hex(PetitionGoneHex));
        Assert.Null(GuildWireCodec.ReadEvent(gone).Petition);
    }

    [Fact]
    public void CompletedEvent_HasTheGoldenLayout()
    {
        Assert.Equal(Hex(CompletedHex), Encode(w => GuildWireCodec.WriteEvent(w, GuildLinkEvent.OfCompleted(77, true))));

        using BinaryReader reader = Open(Hex(CompletedHex));
        GuildLinkEvent back = GuildWireCodec.ReadEvent(reader);
        Assert.Equal(77, back.RequestId);
        Assert.True(back.Result);
    }

    [Fact]
    public void EventBatch_IsACountThenTheEvents_AndEncodedSizeMatchesTheBytes()
    {
        GuildLinkEvent[] events = [GuildLinkEvent.OfDeliver(9, WorldOpcode.SmsgGuildInvite, [1, 2]), GuildLinkEvent.OfCompleted(77, true), GuildLinkEvent.OfGuild(4, SampleGuild())];
        byte[] expected = Hex("03000000" + DeliverHex + CompletedHex + GuildEventHex);
        Assert.Equal(expected, Encode(w => GuildWireCodec.WriteEvents(w, events)));
        Assert.Equal(Hex(DeliverHex).Length, GuildWireCodec.EncodedSize(events[0]));
        Assert.Equal(Hex(GuildEventHex).Length, GuildWireCodec.EncodedSize(events[2]));

        using BinaryReader reader = Open(expected);
        IReadOnlyList<GuildLinkEvent> back = GuildWireCodec.ReadEvents(reader);
        Assert.Equal(new[] { GuildLinkEventKind.Deliver, GuildLinkEventKind.Completed, GuildLinkEventKind.Guild }, back.Select(e => e.Kind));
        Assert.Equal(Hex("00000000"), Encode(w => GuildWireCodec.WriteEvents(w, [])));
    }

    [Fact]
    public void MalformedFrames_AreRefused()
    {
        using BinaryReader unknownKind = Open(Hex("09"));
        Assert.Throws<InvalidDataException>(() => GuildWireCodec.ReadEvent(unknownKind));

        using BinaryReader hugeList = Open(Hex("FFFFFF7F"));
        Assert.Throws<InvalidDataException>(() => GuildWireCodec.ReadEvents(hugeList));

        using BinaryReader truncated = Open(Hex("01" + "0900000000000000" + "8300" + "10000000" + "0102"));
        Assert.Throws<EndOfStreamException>(() => GuildWireCodec.ReadEvent(truncated));
    }

    [Fact]
    public void GuildForwardAndDrain_DispatchThroughThePeerLinkCodec()
    {
        var directory = new InMemoryRealmPresenceDirectory();
        var service = new PresenceHolderService(directory, new InMemoryClusterSocialRouter(directory));

        // Nothing bound on the holder yet: a forwarded request is refused with an error frame, not a thrown exception.
        byte[] forward = PresenceWireCodec.EncodeRequest(PresenceOp.GuildForward, w =>
        {
            w.Write("wb");
            w.Write(5L);
            GuildWireCodec.WriteRequest(w, new GuildRequest(GuildRequestKind.Info, ObjectGuid.Player(5)));
        });
        Assert.Equal(1, service.Handle(forward)[0]);

        // A drain registers the worker and returns its queued events in a bounded batch.
        service.Guilds.Deliver(ObjectGuid.Player(1), WorldOpcode.SmsgGuildInvite, [1]);
        directory.TryUpsert(new RealmPresenceEntry(1, "Alice", 101, "wb", "g", 0, 12, 0, 10, 1, (byte)Race.Human, 0, 0, 0, 1, 1, DateTimeOffset.UtcNow), out _);
        service.Guilds.Deliver(ObjectGuid.Player(1), WorldOpcode.SmsgGuildInvite, [1, 2]);
        service.Guilds.ReplicateGuild(4, null);

        byte[] reply = service.Handle(PresenceWireCodec.EncodeRequest(PresenceOp.GuildDrain, w => w.Write("wb")));
        using BinaryReader r = PresenceWireCodec.OpenReply(reply);
        IReadOnlyList<GuildLinkEvent> events = GuildWireCodec.ReadEvents(r);
        Assert.Equal(new[] { GuildLinkEventKind.Deliver, GuildLinkEventKind.Guild }, events.Select(e => e.Kind));
        Assert.Equal(new byte[] { 1, 2 }, events[0].Payload);

        byte[] second = service.Handle(PresenceWireCodec.EncodeRequest(PresenceOp.GuildDrain, w => w.Write("wb")));
        using BinaryReader empty = PresenceWireCodec.OpenReply(second);
        Assert.Empty(GuildWireCodec.ReadEvents(empty));
    }
}

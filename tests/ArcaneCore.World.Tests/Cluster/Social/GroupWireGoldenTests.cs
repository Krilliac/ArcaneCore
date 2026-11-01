using System.Text;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Protocol;
using ArcaneCore.World.Cluster.Social;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Social;

/// <summary>
/// Golden bytes of the group operations carried over the peer link (<see cref="PresenceOp.GroupCommand"/>, <see cref="PresenceOp.GroupDrain"/>). Like
/// <see cref="PresenceWireGoldenTests"/>, the expected arrays are written out by hand (little-endian integers, 7-bit length-prefixed UTF-8 strings, one byte per bool), so a
/// change made to both the encoder and the decoder, a renumbered op or a renumbered <see cref="GroupCommandKind"/> fails here instead of breaking a mixed-version cluster.
/// </summary>
public sealed class GroupWireGoldenTests
{
    private static byte[] Hex(string hex) => Convert.FromHexString(hex);

    // kind SetLootMethod(8), actor 1, "Ab", team 1, game master true, target 2, "Cd", other "E", value 2, value2 3, master 4, payload [9, 8].
    private const string CommandHex =
        "08" + "0100000000000000" + "024162" + "01" + "01" + "0200000000000000" + "024364" + "0145" + "02000000" + "03000000" + "0400000000000000" + "02000000" + "0908";

    private static GroupCommand Command() => new(GroupCommandKind.SetLootMethod, 1, "Ab", 1, true, 2, "Cd", "E", 2, 3, 4, [9, 8]);

    // Event 1: recipient 9, replica (group 4, raid, leader 7 "Ab", method 2, looter 9, threshold 3, icons [5, 0], members 7 "Ab" sub 0 / 9 "Cd" sub 1 assistant),
    // departed 0, not disbanded, inviter 7, invite not cancelled, one packet (SMSG_GROUP_LIST = 125, payload [1, 2]).
    private const string EventWithReplicaHex =
        "0900000000000000" + "01" + "04000000" + "01" + "0700000000000000" + "024162" + "02" + "0900000000000000" + "03" +
        "02000000" + "0500000000000000" + "0000000000000000" +
        "02000000" + "0700000000000000" + "024162" + "00" + "00" + "0900000000000000" + "024364" + "01" + "01" +
        "00000000" + "00" + "0700000000000000" + "00" +
        "01000000" + "7D00" + "02000000" + "0102";

    // Event 2: recipient 7, no replica, departed group 4, disbanded, no inviter, invite cancelled, no packets.
    private const string EventWithoutReplicaHex = "0700000000000000" + "00" + "04000000" + "01" + "0000000000000000" + "01" + "00000000";

    private static GroupReplica Replica() => new(
        4, true, 7, "Ab", 2, 9, 3, [5, 0], [new GroupReplicaMember(7, "Ab", 0, false), new GroupReplicaMember(9, "Cd", 1, true)]);

    private static byte[] Encode(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            write(w);
        }

        return stream.ToArray();
    }

    [Fact]
    public void GroupPresenceOps_ArePinned()
    {
        Assert.Equal(21, (byte)PresenceOp.GroupCommand);
        Assert.Equal(22, (byte)PresenceOp.GroupDrain);
    }

    [Fact]
    public void GroupCommandKindValues_ArePinned()
    {
        Assert.Equal(1, (byte)GroupCommandKind.Invite);
        Assert.Equal(2, (byte)GroupCommandKind.Accept);
        Assert.Equal(3, (byte)GroupCommandKind.Decline);
        Assert.Equal(4, (byte)GroupCommandKind.Leave);
        Assert.Equal(5, (byte)GroupCommandKind.Kick);
        Assert.Equal(6, (byte)GroupCommandKind.SetLeader);
        Assert.Equal(7, (byte)GroupCommandKind.SetAssistant);
        Assert.Equal(8, (byte)GroupCommandKind.SetLootMethod);
        Assert.Equal(9, (byte)GroupCommandKind.ConvertToRaid);
        Assert.Equal(10, (byte)GroupCommandKind.ChangeSubGroup);
        Assert.Equal(11, (byte)GroupCommandKind.SwapSubGroup);
        Assert.Equal(12, (byte)GroupCommandKind.ReadyCheck);
        Assert.Equal(13, (byte)GroupCommandKind.TargetIcon);
        Assert.Equal(14, (byte)GroupCommandKind.Login);
        Assert.Equal(15, (byte)GroupCommandKind.Logout);
        Assert.Equal(16, (byte)GroupCommandKind.RefuseIgnored);
        Assert.Equal(17, (byte)GroupCommandKind.Relay);
    }

    [Fact]
    public void GroupCommand_HasTheGoldenLayout()
    {
        Assert.Equal(Hex(CommandHex), Encode(w => GroupWireCodec.WriteCommand(w, Command())));

        using var reader = new BinaryReader(new MemoryStream(Hex(CommandHex)), Encoding.UTF8);
        GroupCommand decoded = GroupWireCodec.ReadCommand(reader);
        Assert.Equal(Command() with { Payload = null }, decoded with { Payload = null });
        Assert.Equal(new byte[] { 9, 8 }, decoded.Payload);
    }

    [Fact]
    public void GroupCommandWithoutPayload_EncodesAnEmptyBlock()
    {
        var command = new GroupCommand(GroupCommandKind.Leave, 1, "Ab", 0, false);
        // kind 4, actor 1, "Ab", team 0, gm false, target 0, "", "", value 0, value2 0, master 0, payload length 0.
        Assert.Equal(
            Hex("04" + "0100000000000000" + "024162" + "00" + "00" + "0000000000000000" + "00" + "00" + "00000000" + "00000000" + "0000000000000000" + "00000000"),
            Encode(w => GroupWireCodec.WriteCommand(w, command)));
    }

    [Fact]
    public void GroupEventBatch_HasTheGoldenLayout_WithAndWithoutAReplica()
    {
        GroupEvent[] events =
        [
            new GroupEvent(9, Replica(), 0, false, 7, [new GroupPacket(WorldOpcode.SmsgGroupList, [1, 2])]),
            new GroupEvent(7, null, 4, true, 0, [], InviteCancelled: true),
        ];
        byte[] expected = Hex("02000000" + EventWithReplicaHex + EventWithoutReplicaHex);
        Assert.Equal(expected, Encode(w => GroupWireCodec.WriteEvents(w, events)));

        using var reader = new BinaryReader(new MemoryStream(expected), Encoding.UTF8);
        IReadOnlyList<GroupEvent> back = GroupWireCodec.ReadEvents(reader);
        Assert.Equal(2, back.Count);
        Assert.Equal(Replica().Members, back[0].Replica!.Members);
        Assert.Equal(Replica().TargetIcons, back[0].Replica!.TargetIcons);
        Assert.Equal(7UL, back[0].InviterGuid);
        Assert.False(back[0].InviteCancelled);
        Assert.Equal(WorldOpcode.SmsgGroupList, Assert.Single(back[0].Packets).Opcode);
        Assert.Null(back[1].Replica);
        Assert.Equal(4u, back[1].DepartedGroupId);
        Assert.True(back[1].Disbanded);
        Assert.True(back[1].InviteCancelled);
    }

    [Fact]
    public void EmptyEventBatch_IsACountOfZero()
    {
        Assert.Equal(Hex("00000000"), Encode(w => GroupWireCodec.WriteEvents(w, [])));
    }

    [Fact]
    public void GroupCommandAndDrain_DispatchThroughThePeerLinkCodec()
    {
        var directory = new InMemoryRealmPresenceDirectory();
        var service = new PresenceHolderService(directory, new InMemoryClusterSocialRouter(directory));
        service.Register(new RealmPresenceEntry(1, "Alice", 101, "wa", "g", 0, 12, 0, 10, 1, (byte)Race.Human, 0, 0, 0, 1, 1, DateTimeOffset.UtcNow));
        service.Register(new RealmPresenceEntry(2, "Bob", 102, "wb", "g", 0, 12, 0, 10, 1, (byte)Race.Human, 0, 0, 0, 1, 1, DateTimeOffset.UtcNow));
        byte team = (byte)TeamRules.TeamOf(Race.Human);

        var invite = new GroupCommand(GroupCommandKind.Invite, 1, "Alice", team, false, TargetName: "Bob");
        byte[] request = PresenceWireCodec.EncodeRequest(PresenceOp.GroupCommand, w =>
        {
            w.Write("wa");
            GroupWireCodec.WriteCommand(w, invite);
        });
        Assert.Equal(21, request[0]);
        Assert.Equal(Hex("00"), PresenceWireCodec.Dispatch(service, request));

        // Drain for worker "wb": the request is op 22 and the worker id; the reply is the status byte and the event batch.
        byte[] drainRequest = PresenceWireCodec.EncodeRequest(PresenceOp.GroupDrain, w => w.Write("wb"));
        Assert.Equal(Hex("16" + "027762"), drainRequest);
        byte[] reply = PresenceWireCodec.Dispatch(service, drainRequest);
        Assert.Equal(0, reply[0]);
        using var reader = new BinaryReader(new MemoryStream(reply, 1, reply.Length - 1), Encoding.UTF8);
        GroupEvent bob = Assert.Single(GroupWireCodec.ReadEvents(reader));
        Assert.Equal(2UL, bob.RecipientGuid);
        Assert.Equal(1UL, bob.InviterGuid);
        Assert.Contains(bob.Packets, p => p.Opcode == WorldOpcode.SmsgGroupInvite);

        // The batch was delivered once: a second drain is empty (status ok, count 0).
        Assert.Equal(Hex("00" + "00000000"), PresenceWireCodec.Dispatch(service, drainRequest));
        // The invite result for the inviter waits on her worker.
        Assert.Equal(1, service.Groups.PendingFor("wa"));
    }
}

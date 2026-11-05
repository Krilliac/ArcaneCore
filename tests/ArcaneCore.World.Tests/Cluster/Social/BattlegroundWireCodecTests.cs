using System.Text;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using ArcaneCore.World.Battlegrounds;
using ArcaneCore.World.Cluster.Social;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Social;

/// <summary>
/// The battleground queue frames carried in the presence protocol: ops and tags are pinned with hand-written bytes (little-endian integers, 7-bit length-prefixed UTF-8, one byte per
/// bool), every command and notice round-trips, and a frame that does not fit is refused. Unproven until the integration gate (written without a build, per the lane rules).
/// </summary>
public sealed class BattlegroundWireCodecTests
{
    private static readonly ObjectGuid Nine = ObjectGuid.Player(9);

    private static byte[] Bytes(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            write(w);
        }

        return stream.ToArray();
    }

    private static BinaryReader Open(byte[] bytes) => new(new MemoryStream(bytes), Encoding.UTF8);

    private static BattlegroundCommand RoundTrip(BattlegroundCommand command) => BattlegroundWireCodec.ReadCommand(Open(Bytes(w => BattlegroundWireCodec.WriteCommand(w, command))));

    private static BattlegroundNotice RoundTrip(BattlegroundNotice notice) => BattlegroundWireCodec.ReadNotice(Open(Bytes(w => BattlegroundWireCodec.WriteNotice(w, notice))));

    [Fact]
    public void BattlegroundPresenceOps_ArePinned()
    {
        Assert.Equal(25, (byte)PresenceOp.BattlegroundSubmit);
        Assert.Equal(26, (byte)PresenceOp.BattlegroundTake);
        Assert.Equal(27, (byte)PresenceOp.BattlegroundPost);
        Assert.Equal(28, (byte)PresenceOp.BattlegroundDrain);
    }

    [Fact]
    public void SmallFrames_HaveTheDocumentedBytes()
    {
        Assert.Equal(Convert.FromHexString("05" + "0900000000000000"), Bytes(w => BattlegroundWireCodec.WriteCommand(w, new BattlegroundLogoutCommand(Nine))));
        Assert.Equal(Convert.FromHexString("03" + "0900000000000000" + "3C000000"), Bytes(w => BattlegroundWireCodec.WriteCommand(w, new BattlegroundStatusCommand(Nine, 60))));
        Assert.Equal(Convert.FromHexString("05" + "0900000000000000"), Bytes(w => BattlegroundWireCodec.WriteNotice(w, new BattlegroundPrepareToPortNotice(Nine))));
        Assert.Equal(Convert.FromHexString("02" + "0900000000000000" + "01000000"), Bytes(w => BattlegroundWireCodec.WriteNotice(w, new BattlegroundGroupJoinedNotice(Nine, 1))));
        Assert.Equal(Convert.FromHexString("03" + "0900000000000000" + "03"), Bytes(w => BattlegroundWireCodec.WriteNotice(w, new BattlegroundJoinErrorNotice(Nine, BattlegroundJoinError.MixedFaction))));
    }

    [Fact]
    public void JoinCommand_RoundTripsWithGroupAndEntryPoint()
    {
        var requester = new BattlegroundMember(Nine, Team.Horde, 60, InWorld: true, Deserter: false);
        var mate = new BattlegroundMember(ObjectGuid.Player(10), Team.Horde, 58, InWorld: true, Deserter: true);
        var command = new BattlegroundJoinCommand(
            new BattlegroundJoinRequest(requester, BattlegroundType.WarsongGulch, 3, AsGroup: true, QueuedAtPortal: false, [requester, mate]),
            new BattlegroundEntryPoint(1, 1.5f, -2.5f, 3.5f, 0.25f, 14));

        var back = Assert.IsType<BattlegroundJoinCommand>(RoundTrip(command));

        Assert.Equal(requester, back.Request.Requester);
        Assert.Equal(BattlegroundType.WarsongGulch, back.Request.Type);
        Assert.Equal(3u, back.Request.DesiredInstanceId);
        Assert.True(back.Request.AsGroup);
        Assert.False(back.Request.QueuedAtPortal);
        Assert.Equal(new[] { requester, mate }, back.Request.Group);
        Assert.Equal(command.EntryPoint, back.EntryPoint);
    }

    [Fact]
    public void JoinCommand_WithoutEntryPoint_RoundTripsNull()
    {
        var requester = new BattlegroundMember(Nine, Team.Alliance, 20, InWorld: true, Deserter: false);
        var back = Assert.IsType<BattlegroundJoinCommand>(RoundTrip(new BattlegroundJoinCommand(
            new BattlegroundJoinRequest(requester, BattlegroundType.AlteracValley, 0, AsGroup: false, QueuedAtPortal: true, []), null)));

        Assert.Null(back.EntryPoint);
        Assert.Empty(back.Request.Group);
        Assert.True(back.Request.QueuedAtPortal);
    }

    [Fact]
    public void OtherCommands_RoundTrip()
    {
        var member = new BattlegroundMember(Nine, Team.Horde, 33, InWorld: false, Deserter: true);
        var port = new BattlegroundPortCommand(new BattlegroundPortRequest(Nine, 489, 1, 60, true));
        Assert.Equal(port, RoundTrip(port));
        Assert.Equal(new BattlegroundStatusCommand(Nine, 12), RoundTrip(new BattlegroundStatusCommand(Nine, 12)));
        Assert.Equal(new BattlegroundLoginCommand(member), RoundTrip(new BattlegroundLoginCommand(member)));
        Assert.Equal(new BattlegroundLogoutCommand(Nine), RoundTrip(new BattlegroundLogoutCommand(Nine)));
    }

    [Fact]
    public void EveryNotice_RoundTrips()
    {
        BattlegroundNotice[] notices =
        [
            new BattlegroundStatusNotice(Nine, 1, new BattlegroundStatusSubject(489, 5, 2), BattlegroundStatus.WaitJoin, 1000, 2000),
            new BattlegroundGroupJoinedNotice(Nine, 2),
            new BattlegroundJoinErrorNotice(Nine, BattlegroundJoinError.GroupTooMany),
            new BattlegroundQueueLimitNotice(Nine, 5),
            new BattlegroundPrepareToPortNotice(Nine),
            new BattlegroundEnterNotice(Nine, 489, 1000001, Team.Alliance, 1f, 2f, 3f, 4f),
        ];

        foreach (BattlegroundNotice notice in notices)
        {
            Assert.Equal(notice, RoundTrip(notice));
        }
    }

    [Fact]
    public void Lists_RoundTripWithTheirWorker()
    {
        BattlegroundQueuedNotice[] notices = [new("a", new BattlegroundPrepareToPortNotice(Nine)), new("b", new BattlegroundQueueLimitNotice(Nine, 3))];
        IReadOnlyList<BattlegroundQueuedNotice> back = BattlegroundWireCodec.ReadNotices(Open(Bytes(w => BattlegroundWireCodec.WriteNotices(w, notices))));
        Assert.Equal(notices, back);

        BattlegroundQueuedCommand[] commands = [new("a", new BattlegroundLogoutCommand(Nine))];
        Assert.Equal(commands, BattlegroundWireCodec.ReadCommands(Open(Bytes(w => BattlegroundWireCodec.WriteCommands(w, commands)))));
    }

    [Fact]
    public void UnknownTags_AndUndefinedEnums_AreRefused()
    {
        Assert.Throws<InvalidDataException>(() => BattlegroundWireCodec.ReadCommand(Open([99])));
        Assert.Throws<InvalidDataException>(() => BattlegroundWireCodec.ReadNotice(Open([99])));
        byte[] badError = Convert.FromHexString("03" + "0900000000000000" + "63");
        Assert.Throws<InvalidDataException>(() => BattlegroundWireCodec.ReadNotice(Open(badError)));
        byte[] badTeam = Convert.FromHexString("04" + "0900000000000000" + "07" + "00000000" + "00" + "00");
        Assert.Throws<InvalidDataException>(() => BattlegroundWireCodec.ReadCommand(Open(badTeam)));
    }

    [Fact]
    public void ListCountOverTheLimit_IsRefused()
    {
        byte[] huge = Bytes(w => w.Write(BattlegroundWireCodec.MaxListCount + 1));
        Assert.Throws<InvalidDataException>(() => BattlegroundWireCodec.ReadCommands(Open(huge)));
        Assert.Throws<InvalidDataException>(() => BattlegroundWireCodec.ReadNotices(Open(huge)));
    }

    [Fact]
    public void GroupOverTheLimit_CannotBeEncoded()
    {
        var member = new BattlegroundMember(Nine, Team.Alliance, 60, InWorld: true, Deserter: false);
        BattlegroundMember[] group = [.. Enumerable.Repeat(member, BattlegroundWireCodec.MaxGroupMembers + 1)];
        var command = new BattlegroundJoinCommand(new BattlegroundJoinRequest(member, BattlegroundType.WarsongGulch, 0, true, false, group), null);

        Assert.Throws<ArgumentException>(() => Bytes(w => BattlegroundWireCodec.WriteCommand(w, command)));
    }

    [Fact]
    public void TruncatedFrame_Throws()
    {
        byte[] whole = Bytes(w => BattlegroundWireCodec.WriteNotice(w, new BattlegroundEnterNotice(Nine, 489, 1, Team.Horde, 1, 2, 3, 4)));
        Assert.ThrowsAny<EndOfStreamException>(() => BattlegroundWireCodec.ReadNotice(Open(whole[..^2])));
    }
}

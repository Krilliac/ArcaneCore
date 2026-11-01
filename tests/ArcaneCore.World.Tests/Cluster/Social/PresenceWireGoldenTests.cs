using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.World.Cluster.Social;
using ArcaneCore.World.Cluster.Tooling;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Social;

/// <summary>
/// Golden bytes of the presence protocol carried over the peer link. The expected arrays are written out by hand (little-endian integers, 7-bit length-prefixed UTF-8
/// strings, one byte per bool), so a reordered or retyped field, or a renumbered op, fails here instead of silently breaking a mixed-version cluster.
/// </summary>
public sealed class PresenceWireGoldenTests
{
    // guid 1, name "Ab", account 2, worker "w", gateway "g", map 3, zone 4, area 5, level 6, class 7, race 8, gender 1, guild 9, group 10,
    // version 11, generation 12, updated ticks 13, guildName null, afk true, dnd false, status "s", account security 2, GM invisible true.
    private const string EntryHex =
        "0100000000000000" + "024162" + "02000000" + "0177" + "0167" + "03000000" + "04000000" + "05000000" + "06070801" + "09000000" + "0A000000" +
        "0B00000000000000" + "0C00000000000000" + "0D00000000000000" + "00" + "01" + "00" + "010173" + "02" + "01";

    private static RealmPresenceEntry Entry() => new(
        1, "Ab", 2, "w", "g", 3, 4, 5, 6, 7, 8, 1, 9, 10, 11, 12, new DateTimeOffset(13, TimeSpan.Zero), null, true, false, "s", 2, true);

    private static byte[] Hex(string hex) => Convert.FromHexString(hex);

    [Fact]
    public void PresenceOpValues_ArePinned()
    {
        Assert.Equal(1, (byte)PresenceOp.Register);
        Assert.Equal(2, (byte)PresenceOp.Upsert);
        Assert.Equal(3, (byte)PresenceOp.Remove);
        Assert.Equal(4, (byte)PresenceOp.DrainKicks);
        Assert.Equal(5, (byte)PresenceOp.FindByGuid);
        Assert.Equal(6, (byte)PresenceOp.FindByName);
        Assert.Equal(7, (byte)PresenceOp.FindByAccount);
        Assert.Equal(8, (byte)PresenceOp.FindByGuild);
        Assert.Equal(9, (byte)PresenceOp.FindByGroup);
        Assert.Equal(10, (byte)PresenceOp.QueryWho);
        Assert.Equal(11, (byte)PresenceOp.Snapshot);
        Assert.Equal(12, (byte)PresenceOp.Count);
        Assert.Equal(13, (byte)PresenceOp.Route);
        Assert.Equal(14, (byte)PresenceOp.JoinChannel);
        Assert.Equal(15, (byte)PresenceOp.LeaveChannel);
        Assert.Equal(16, (byte)PresenceOp.DrainGateway);
        Assert.Equal(17, (byte)PresenceOp.DrainWorker);
        Assert.Equal(18, (byte)PresenceOp.Logout);
        Assert.Equal(19, (byte)PresenceOp.ChannelModerate);
        Assert.Equal(20, (byte)PresenceOp.ChannelState);
    }

    [Fact]
    public void ModerationActionValues_ArePinned()
    {
        Assert.Equal(1, (byte)ChannelModerationAction.ClaimOwner);
        Assert.Equal(2, (byte)ChannelModerationAction.SetOwner);
        Assert.Equal(3, (byte)ChannelModerationAction.AddModerator);
        Assert.Equal(4, (byte)ChannelModerationAction.RemoveModerator);
        Assert.Equal(5, (byte)ChannelModerationAction.Ban);
        Assert.Equal(6, (byte)ChannelModerationAction.Unban);
        Assert.Equal(7, (byte)ChannelModerationAction.Mute);
        Assert.Equal(8, (byte)ChannelModerationAction.Unmute);
    }

    [Fact]
    public void EntryWithoutStaffFields_EncodesThemAsAPlainVisiblePlayer()
    {
        RealmPresenceEntry plain = Entry() with { Security = 0, GmInvisible = false };
        byte[] bytes = PresenceWireCodec.EncodeReply(w => PresenceWireCodec.WriteEntry(w, plain));

        // status ok, the entry, then security 0 and GM invisible false as its last two bytes.
        Assert.Equal(Hex("00" + EntryHex[..^4] + "0000"), bytes);
        using var r = new BinaryReader(new MemoryStream(bytes, 1, bytes.Length - 1));
        Assert.Equal(plain, PresenceWireCodec.ReadEntry(r));
    }

    [Fact]
    public void ChannelModerationRequests_HaveTheGoldenLayout_AndTheHolderAppliesThem()
    {
        var directory = new InMemoryRealmPresenceDirectory();
        var router = new InMemoryClusterSocialRouter(directory);
        var service = new PresenceHolderService(directory, router);
        service.Register(Entry());
        service.Register(Entry() with { CharacterGuid = 2, CharacterName = "Cd", AccountId = 3, Security = 0, GmInvisible = false });
        Assert.True(router.JoinChannel("X", 1, 12));

        // Claim: op 19, channel "X", action 1, actor 1, target 1.
        const string claim = "13" + "0158" + "01" + "0100000000000000" + "0100000000000000";
        Assert.Equal(Hex(claim), PresenceWireCodec.EncodeRequest(PresenceOp.ChannelModerate, w =>
        {
            w.Write("X");
            w.Write((byte)ChannelModerationAction.ClaimOwner);
            w.Write(1UL);
            w.Write(1UL);
        }));
        Assert.Equal(Hex("00" + "01"), PresenceWireCodec.Dispatch(service, Hex(claim)));

        // Ban of guid 2 by the owner: op 19, "X", action 5, actor 1, target 2.
        Assert.Equal(Hex("00" + "01"), PresenceWireCodec.Dispatch(service, Hex("13" + "0158" + "05" + "0100000000000000" + "0200000000000000")));
        // A character that is no moderator is refused: actor 2, action Mute, target 1.
        Assert.Equal(Hex("00" + "00"), PresenceWireCodec.Dispatch(service, Hex("13" + "0158" + "07" + "0200000000000000" + "0100000000000000")));

        // State: op 20, "X"; reply present, owner 1, moderators [1], banned [2], muted [].
        Assert.Equal(Hex("14" + "0158"), PresenceWireCodec.EncodeRequest(PresenceOp.ChannelState, w => w.Write("X")));
        Assert.Equal(
            Hex("00" + "01" + "0100000000000000" + "01000000" + "0100000000000000" + "01000000" + "0200000000000000" + "00000000"),
            PresenceWireCodec.Dispatch(service, Hex("14" + "0158")));

        // An unknown channel has no state.
        Assert.Equal(Hex("00" + "00"), PresenceWireCodec.Dispatch(service, Hex("14" + "0159")));
    }

    [Fact]
    public void RouteReply_CarriesTheDeliveryCountOnly_AndDrainWorkerReturnsTheQueuedDeliveries()
    {
        var directory = new InMemoryRealmPresenceDirectory();
        var service = new PresenceHolderService(directory, new InMemoryClusterSocialRouter(directory));
        service.Register(Entry());
        service.Register(Entry() with { CharacterGuid = 2, CharacterName = "Cd", AccountId = 3, Security = 0, GmInvisible = false });
        var message = new ClusterSocialMessage(Guid.NewGuid(), SocialRouteChannel.Whisper, 1, "Ab", 2, 12, "w", "g", 1, "hi", TargetCharacterGuid: 2);

        // Reply: ok, route status Delivered (0), one delivery: the count, not the delivery.
        byte[] routed = PresenceWireCodec.Dispatch(service, PresenceWireCodec.EncodeRequest(PresenceOp.Route, w => PresenceWireCodec.WriteMessage(w, message)));
        Assert.Equal(Hex("00" + "00" + "01000000"), routed);

        byte[] drained = PresenceWireCodec.Dispatch(service, Hex("11" + "0177"));
        using BinaryReader r = PresenceWireCodec.OpenReply(drained);
        RoutedSocialDelivery delivery = Assert.Single(PresenceWireCodec.ReadDeliveries(r));
        Assert.Equal(2UL, delivery.RecipientGuid);
        Assert.Equal("hi", delivery.PayloadText);
        Assert.Equal(PresenceWireCodec.DeliveryWireSize(delivery), drained.Length - 1 - 4);
        Assert.Equal(Hex("00" + "00000000"), PresenceWireCodec.Dispatch(service, Hex("11" + "0177")));
    }

    [Fact]
    public void RegisterAndUpsertRequests_HaveTheGoldenEntryLayout()
    {
        Assert.Equal(Hex("01" + EntryHex), PresenceWireCodec.EncodeRequest(PresenceOp.Register, w => PresenceWireCodec.WriteEntry(w, Entry())));
        Assert.Equal(Hex("02" + EntryHex), PresenceWireCodec.EncodeRequest(PresenceOp.Upsert, w => PresenceWireCodec.WriteEntry(w, Entry())));

        using var r = new BinaryReader(new MemoryStream(Hex(EntryHex)));
        Assert.Equal(Entry(), PresenceWireCodec.ReadEntry(r));
    }

    [Fact]
    public void FindRequests_HaveTheGoldenLayout()
    {
        Assert.Equal(Hex("05" + "0700000000000000"), PresenceWireCodec.EncodeRequest(PresenceOp.FindByGuid, w => w.Write(7UL)));
        Assert.Equal(Hex("06" + "0141"), PresenceWireCodec.EncodeRequest(PresenceOp.FindByName, w => w.Write("A")));
        Assert.Equal(Hex("07" + "05000000"), PresenceWireCodec.EncodeRequest(PresenceOp.FindByAccount, w => w.Write(5)));
    }

    [Fact]
    public void QueryWhoRequest_HasTheGoldenFilterLayout()
    {
        // min 1, max 60, class mask present 0x20, race mask absent, zones [4], terms ["x"], max results 49.
        const string filterHex = "01" + "3C" + "01" + "20000000" + "00" + "00000000" + "01000000" + "04000000" + "01000000" + "0178" + "31000000";
        var filter = new RealmWhoFilter(1, 60, 0x20u, null, [4u], ["x"], 49);

        Assert.Equal(Hex("0A" + filterHex), PresenceWireCodec.EncodeRequest(PresenceOp.QueryWho, w => PresenceWireCodec.WriteWho(w, filter)));

        using var r = new BinaryReader(new MemoryStream(Hex(filterHex)));
        RealmWhoFilter decoded = PresenceWireCodec.ReadWho(r);
        Assert.Equal(1, decoded.MinLevel);
        Assert.Equal(60, decoded.MaxLevel);
        Assert.Equal(0x20u, decoded.ClassMask);
        Assert.Null(decoded.RaceMask);
        Assert.Equal([4u], decoded.ZoneIds);
        Assert.Equal(["x"], decoded.SearchTerms);
        Assert.Equal(49, decoded.MaxResults);
    }

    [Fact]
    public void RouteRequest_HasTheGoldenMessageLayout()
    {
        const string messageHex =
            "33221100554477668899AABBCCDDEEFF" + "00" + "0100000000000000" + "0141" + "02000000" + "0300000000000000" + "0177" + "0167" + "0400000000000000" + "026869" +
            "01" + "0500000000000000" + "00" + "00" + "00000000" + "00" + "00000000" + "00" + "0000000000000000";
        var message = new ClusterSocialMessage(
            new Guid("00112233-4455-6677-8899-aabbccddeeff"), SocialRouteChannel.Whisper, 1, "A", 2, 3, "w", "g", 4, "hi", TargetCharacterGuid: 5);

        Assert.Equal(Hex("0D" + messageHex), PresenceWireCodec.EncodeRequest(PresenceOp.Route, w => PresenceWireCodec.WriteMessage(w, message)));

        using var r = new BinaryReader(new MemoryStream(Hex(messageHex)));
        Assert.Equal(message, PresenceWireCodec.ReadMessage(r));
    }

    [Fact]
    public void GoldenRegisterThenRemove_AreUnderstoodByTheHolder()
    {
        var directory = new InMemoryRealmPresenceDirectory();
        var service = new PresenceHolderService(directory, new InMemoryClusterSocialRouter(directory));

        byte[] registered = PresenceWireCodec.Dispatch(service, Hex("01" + EntryHex));
        Assert.Equal(Hex("0000"), registered[..2]);
        Assert.NotNull(directory.FindByGuid(1));

        // Remove: guid 1, version long.MaxValue, generation long.MaxValue, worker "w".
        byte[] removed = PresenceWireCodec.Dispatch(service, Hex("03" + "0100000000000000" + "FFFFFFFFFFFFFF7F" + "FFFFFFFFFFFFFF7F" + "0177"));
        Assert.Equal(Hex("0000"), removed);
        Assert.Null(directory.FindByGuid(1));
    }

    [Fact]
    public void DrainKicksAndCountRequests_GetTheGoldenEmptyReplies()
    {
        var directory = new InMemoryRealmPresenceDirectory();
        var service = new PresenceHolderService(directory, new InMemoryClusterSocialRouter(directory));

        Assert.Equal(Hex("00" + "00000000"), PresenceWireCodec.Dispatch(service, Hex("04" + "0177")));
        Assert.Equal(Hex("00" + "00000000"), PresenceWireCodec.Dispatch(service, Hex("0C")));
        Assert.Equal(Hex("00" + "00000000"), PresenceWireCodec.Dispatch(service, Hex("0B")));
    }

    [Fact]
    public void Replies_HaveTheGoldenLayouts()
    {
        // Upsert reply: status ok, result byte, nullable entry.
        Assert.Equal(
            Hex("00" + "01" + "00"),
            PresenceWireCodec.EncodeReply(w => PresenceWireCodec.WriteUpsertReply(w, new PresenceUpsertReply(PresenceMutationResult.RejectedStaleVersion, null))));
        Assert.Equal(
            Hex("00" + "00" + "01" + EntryHex),
            PresenceWireCodec.EncodeReply(w => PresenceWireCodec.WriteUpsertReply(w, new PresenceUpsertReply(PresenceMutationResult.Applied, Entry()))));

        // Kick list: count, then worker, account, guid, generation.
        Assert.Equal(
            Hex("00" + "01000000" + "0177" + "02000000" + "0100000000000000" + "0300000000000000"),
            PresenceWireCodec.EncodeReply(w => PresenceWireCodec.WriteKicks(w, [new PresenceKick("w", 2, 1, 3)])));

        // DrainWorker / DrainGateway reply: status ok, delivery list (an empty one here).
        Assert.Equal(Hex("00" + "00000000"), PresenceWireCodec.EncodeReply(w => PresenceWireCodec.WriteDeliveries(w, [])));

        // Error frame: status 1, message string.
        Assert.Equal(Hex("01" + "026E6F"), PresenceWireCodec.EncodeError("no"));
    }

    [Fact]
    public void KickReply_DecodesFromGoldenBytes()
    {
        using BinaryReader r = PresenceWireCodec.OpenReply(Hex("00" + "01000000" + "0177" + "02000000" + "0100000000000000" + "0300000000000000"));
        Assert.Equal([new PresenceKick("w", 2, 1, 3)], PresenceWireCodec.ReadKicks(r));
    }

    [Fact]
    public void ClusterSocialCommandCount_OnANonHolder_DoesNotTouchThePeerPath()
    {
        var directory = new InMemoryRealmPresenceDirectory();
        var service = new PresenceHolderService(directory, new InMemoryClusterSocialRouter(directory));
        int remoteLookups = 0;
        var nonHolder = new LeaseRoutedPresence(service, () => false, () =>
        {
            remoteLookups++;
            throw new InvalidOperationException("the synchronous peer path must not be used");
        });

        Assert.False(nonHolder.TryGetLocalCount(out _));
        Assert.Equal(42, ClusterCommands.DirectoryEntryCount(nonHolder, 42));
        Assert.Equal(0, remoteLookups);

        var holder = new LeaseRoutedPresence(service, () => true, () => null);
        service.Register(Entry());
        Assert.Equal(1, ClusterCommands.DirectoryEntryCount(holder, 42));
        Assert.Equal(42, ClusterCommands.DirectoryEntryCount(null, 42));
    }
}

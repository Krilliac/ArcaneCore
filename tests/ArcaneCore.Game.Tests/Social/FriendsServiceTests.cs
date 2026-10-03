using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Social;

/// <summary>Friend and ignore lists (vmangos SocialMgr / MiscHandler rules).</summary>
public sealed class FriendsServiceTests
{
    [Fact]
    public void AddFriend_Online_RepliesAddedOnlineWithPresence_AndPersists()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        f.ClearAll();

        f.Context.Friends.AddFriend(a, "P2");

        var reader = new PacketReader(f.Single(a, WorldOpcode.SmsgFriendStatus));
        Assert.Equal((byte)FriendsResult.AddedOnline, reader.ReadByte());
        Assert.Equal(b.Guid.Value, reader.ReadUInt64());
        Assert.Equal((byte)FriendStatus.Online, reader.ReadByte());
        Assert.Equal(b.ZoneId, reader.ReadUInt32());
        Assert.Equal((uint)b.Level, reader.ReadUInt32());
        Assert.Equal((uint)b.Class, reader.ReadUInt32());
        Assert.Equal(0, reader.Remaining);
        Assert.Equal([(1, 2, SocialFlags.Friend)], f.Persistence.Social);
        Assert.True(f.Context.Friends.Get(a).Has(2, SocialFlags.Friend));
    }

    [Theory]
    [InlineData("friend")]
    [InlineData("ignore")]
    [InlineData("unfriend")]
    [InlineData("unignore")]
    public void WriteQueueRefusal_DisconnectsThePlayer_AndPersistsNothing(string operation)
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        f.AddOffline(3);
        f.Context.Friends.Get(a).Set(2, operation == "unignore" ? SocialFlags.Ignored : SocialFlags.Friend, true);
        f.Persistence.RefuseSocial = true;

        switch (operation)
        {
            case "friend": f.Context.Friends.AddFriend(a, "P3"); break;
            case "ignore": f.Context.Friends.AddIgnore(a, "P3"); break;
            case "unfriend": f.Context.Friends.RemoveFriend(a, b.Guid); break;
            default: f.Context.Friends.RemoveIgnore(a, b.Guid); break;
        }

        Assert.True(f.Session(a).Kicked);
        Assert.Empty(f.Persistence.Social);
    }

    [Fact]
    public void AddFriend_Offline_RepliesAddedOffline_WithOnlyResultAndGuid()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        f.AddOffline(5);
        f.ClearAll();

        f.Context.Friends.AddFriend(a, "P5");

        byte[] payload = f.Single(a, WorldOpcode.SmsgFriendStatus);
        Assert.Equal(9, payload.Length);
        Assert.Equal((byte)FriendsResult.AddedOffline, payload[0]);
    }

    [Theory]
    [InlineData("P1", FriendsResult.Self)]
    [InlineData("Nobody", FriendsResult.NotFound)]
    [InlineData("P3", FriendsResult.Enemy)]
    public void AddFriend_Refusals(string name, FriendsResult expected)
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        f.AddPlayer(3, Race.Orc, x: 5000);
        f.ClearAll();

        f.Context.Friends.AddFriend(a, name);

        Assert.Equal((byte)expected, f.Single(a, WorldOpcode.SmsgFriendStatus)[0]);
        Assert.Empty(f.Persistence.Social);
    }

    [Fact]
    public void AddFriend_OppositeFaction_IsAllowedForStaffAndTwoSideRealms()
    {
        using var f = new SocialFixture(new SocialOptions { AllowTwoSideAddFriend = true });
        Player a = f.AddPlayer(1);
        f.AddOffline(3, Race.Orc);
        f.ClearAll();

        f.Context.Friends.AddFriend(a, "P3");

        Assert.Equal((byte)FriendsResult.AddedOffline, f.Single(a, WorldOpcode.SmsgFriendStatus)[0]);
    }

    [Fact]
    public void AddFriend_Twice_RepliesAlready()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        f.AddOffline(2);
        f.Context.Friends.AddFriend(a, "P2");
        f.ClearAll();

        f.Context.Friends.AddFriend(a, "P2");

        Assert.Equal((byte)FriendsResult.Already, f.Single(a, WorldOpcode.SmsgFriendStatus)[0]);
    }

    [Fact]
    public void FriendList_IsFull_AtFiftyFriends()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        for (uint i = 100; i < 100 + PlayerSocial.FriendLimit + 1; i++)
        {
            f.AddOffline(i);
            f.Context.Friends.AddFriend(a, $"P{i}");
        }

        Assert.Equal(PlayerSocial.FriendLimit, f.Context.Friends.Get(a).Count(SocialFlags.Friend));
        Assert.Equal((byte)FriendsResult.ListFull, f.Sent(a, WorldOpcode.SmsgFriendStatus)[^1][0]);
    }

    [Fact]
    public void RemoveFriend_RepliesRemoved_AndPersistsNone()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        CharacterInfo b = f.AddOffline(2);
        f.Context.Friends.AddFriend(a, "P2");
        f.ClearAll();

        f.Context.Friends.RemoveFriend(a, b.Guid);

        Assert.Equal((byte)FriendsResult.Removed, f.Single(a, WorldOpcode.SmsgFriendStatus)[0]);
        Assert.Equal((1, 2, SocialFlags.None), f.Persistence.Social[^1]);
    }

    [Fact]
    public void Ignore_AddAndRemove_KeepsFriendFlag()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        CharacterInfo b = f.AddOffline(2);
        f.Context.Friends.AddFriend(a, "P2");
        f.ClearAll();

        f.Context.Friends.AddIgnore(a, "P2");
        Assert.True(f.Context.IsIgnoring(a, b.Guid));
        Assert.Equal((1, 2, SocialFlags.Friend | SocialFlags.Ignored), f.Persistence.Social[^1]);

        f.Context.Friends.RemoveIgnore(a, b.Guid);
        Assert.False(f.Context.IsIgnoring(a, b.Guid));
        Assert.Equal((1, 2, SocialFlags.Friend), f.Persistence.Social[^1]);
        Assert.Equal(
            [(byte)FriendsResult.IgnoreAdded, (byte)FriendsResult.IgnoreRemoved],
            f.Sent(a, WorldOpcode.SmsgFriendStatus).Select(p => p[0]));
    }

    [Fact]
    public void Ignore_Self_And_Unknown()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        f.ClearAll();

        f.Context.Friends.AddIgnore(a, "P1");
        f.Context.Friends.AddIgnore(a, "Ghost");

        Assert.Equal(
            [(byte)FriendsResult.IgnoreSelf, (byte)FriendsResult.IgnoreNotFound],
            f.Sent(a, WorldOpcode.SmsgFriendStatus).Select(p => p[0]));
    }

    [Fact]
    public void Presence_IsBroadcastToFriendListers_ThatMaySeeThePlayer()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        f.Context.Friends.AddFriend(a, "P2");
        f.ClearAll();

        f.Context.Friends.BroadcastPresence(b, online: true);
        f.Context.Friends.BroadcastPresence(b, online: false);

        List<byte[]> sent = f.Sent(a, WorldOpcode.SmsgFriendStatus);
        Assert.Equal(2, sent.Count);
        Assert.Equal((byte)FriendsResult.Online, sent[0][0]);
        Assert.Equal(22, sent[0].Length);
        Assert.Equal((byte)FriendsResult.Offline, sent[1][0]);
        Assert.Equal(9, sent[1].Length);
        Assert.Empty(f.Sent(b, WorldOpcode.SmsgFriendStatus));
    }

    [Fact]
    public void Presence_OfHiddenStaff_IsNotBroadcast()
    {
        using var f = new SocialFixture();
        f.World.Options.GmLevelInWhoList = AccountSecurity.Player;
        Player a = f.AddPlayer(1);
        Player gm = f.AddPlayer(2, security: AccountSecurity.GameMaster);
        f.Context.Friends.AddFriend(a, "P2");
        f.ClearAll();

        f.Context.Friends.BroadcastPresence(gm, online: true);

        Assert.Empty(f.Sent(a, WorldOpcode.SmsgFriendStatus));
    }

    [Fact]
    public void FriendList_ListsOnlineFriendsWithDetails_AndOfflineWithStatusOnly()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        CharacterInfo c = f.AddOffline(3);
        f.Context.Friends.Load(a, [new SocialEntry(2, SocialFlags.Friend), new SocialEntry(3, SocialFlags.Friend), new SocialEntry(9, SocialFlags.Friend)]);
        f.ClearAll();

        f.Context.Friends.SendFriendList(a);

        var reader = new PacketReader(f.Single(a, WorldOpcode.SmsgFriendList));
        Assert.Equal(2, reader.ReadByte()); // character 9 does not exist and is skipped
        var seen = new Dictionary<ulong, byte>();
        for (int i = 0; i < 2; i++)
        {
            ulong guid = reader.ReadUInt64();
            byte status = reader.ReadByte();
            seen[guid] = status;
            if (status != 0)
            {
                Assert.Equal(b.ZoneId, reader.ReadUInt32());
                reader.ReadUInt32();
                reader.ReadUInt32();
            }
        }

        Assert.Equal(0, reader.Remaining);
        Assert.Equal((byte)FriendStatus.Online, seen[b.Guid.Value]);
        Assert.Equal((byte)FriendStatus.Offline, seen[c.Guid.Value]);
    }

    [Fact]
    public void IgnoreList_ListsIgnoredGuids()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        f.AddOffline(2);
        f.Context.Friends.Load(a, [new SocialEntry(2, SocialFlags.Ignored)]);
        f.ClearAll();

        f.Context.Friends.SendIgnoreList(a);

        var reader = new PacketReader(f.Single(a, WorldOpcode.SmsgIgnoreList));
        Assert.Equal(1, reader.ReadByte());
        Assert.Equal(ObjectGuid.Player(2).Value, reader.ReadUInt64());
    }
}

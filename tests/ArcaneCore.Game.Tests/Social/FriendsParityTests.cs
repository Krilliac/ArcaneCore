using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Social;

/// <summary>
/// Characterization pins for friend and ignore edge cases verified by reading vmangos (MiscHandler.cpp:464-565,
/// SocialMgr.cpp:30-260). All PINS: they were green on arrival (no deviation found), and guard the behavior.
/// </summary>
public sealed class FriendsParityTests
{
    [Fact] // PIN: the precedence at a full list is ALREADY before LIST_FULL (SocialMgr.cpp:30-90 checks the entry first)
    public void ReAddingAnExistingFriend_AtFifty_AnswersAlready_NotListFull()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        for (uint i = 100; i < 100 + PlayerSocial.FriendLimit; i++)
        {
            f.AddOffline(i);
            f.Context.Friends.AddFriend(a, $"P{i}");
        }

        f.ClearAll();
        f.Context.Friends.AddFriend(a, "P100");

        Assert.Equal((byte)FriendsResult.Already, f.Single(a, WorldOpcode.SmsgFriendStatus)[0]);
    }

    [Fact] // PIN: 25 ignores, the 26th answers IGNORE_FULL
    public void TheTwentySixthIgnore_AnswersIgnoreFull()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        for (uint i = 100; i < 100 + PlayerSocial.IgnoreLimit + 1; i++)
        {
            f.AddOffline(i);
            f.Context.Friends.AddIgnore(a, $"P{i}");
        }

        Assert.Equal(PlayerSocial.IgnoreLimit, f.Context.Friends.Get(a).Count(SocialFlags.Ignored));
        Assert.Equal((byte)FriendsResult.IgnoreFull, f.Sent(a, WorldOpcode.SmsgFriendStatus)[^1][0]);
    }

    [Fact] // PIN: MiscHandler.cpp:511-518 — removing a guid that was never added still answers FRIEND_REMOVED with that guid
    public void RemovingAFriendThatWasNeverAdded_StillAnswersRemoved()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        CharacterInfo stranger = f.AddOffline(2);
        f.ClearAll();

        f.Context.Friends.RemoveFriend(a, stranger.Guid);

        byte[] reply = f.Single(a, WorldOpcode.SmsgFriendStatus);
        Assert.Equal((byte)FriendsResult.Removed, reply[0]);
        Assert.Equal(stranger.Guid.Value, BitConverter.ToUInt64(reply, 1));
    }

    [Fact] // PIN: an ignore never removes a friend, and both flags share one stored row
    public void FriendThenIgnore_OfTheSameCharacter_StoresBothFlagsInOneEntry()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        f.AddOffline(2);
        f.Context.Friends.AddFriend(a, "P2");
        f.Persistence.Social.Clear();

        f.Context.Friends.AddIgnore(a, "P2");

        Assert.Equal([(1, 2, SocialFlags.Friend | SocialFlags.Ignored)], f.Persistence.Social);
    }
}

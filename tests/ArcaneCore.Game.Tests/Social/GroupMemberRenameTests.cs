using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using Xunit;

namespace ArcaneCore.Game.Tests.Social;

/// <summary>
/// Group membership outlives a logout, and a character can be renamed while offline. The slot name (used by
/// CMSG_GROUP_UNINVITE, CMSG_GROUP_CHANGE_SUB_GROUP and the leader name) must follow the character's name
/// when it comes back into the world.
/// </summary>
public sealed class GroupMemberRenameTests
{
    private static Player Relog(SocialFixture f, Player old, string newName)
    {
        f.Context.Groups.OnLoggingOut(old);
        f.World.RemovePlayer(old);
        var session = new FakeSession((int)old.Guid.Low, AccountSecurity.Player);
        var player = new Player(new CharacterRecord
        {
            Id = (int)old.Guid.Low, AccountId = session.AccountId, Name = newName, Race = (byte)Race.Human,
            Class = (byte)Class.Warrior, Gender = (byte)Gender.Male, Level = 1, ZoneId = 12, Z = 83.5f,
        }, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), session);
        f.World.AddPlayer(player);
        f.Context.Groups.OnLoggedIn(player);
        return player;
    }

    private static Group MakeParty(SocialFixture f, Player leader, params Player[] members)
    {
        foreach (Player member in members)
        {
            f.Context.Groups.Invite(leader, member.Name);
            f.Context.Groups.Accept(member);
        }

        return f.Context.Groups.GetGroup(leader.Guid)!;
    }

    [Fact]
    public void RenamedMember_IsUninvitedByItsNewName()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        Player c = f.AddPlayer(3);
        Group group = MakeParty(f, a, b, c);

        Player renamed = Relog(f, b, "Renamed");

        Assert.Equal("Renamed", group.Find(renamed.Guid)!.Name);
        f.Context.Groups.UninviteByName(a, "Renamed");
        Assert.Null(group.Find(renamed.Guid));
        Assert.Null(f.Context.Groups.GetGroup(renamed.Guid));
    }

    [Fact]
    public void RenamedLeader_UpdatesTheLeaderName()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        Group group = MakeParty(f, a, b);

        Player renamed = Relog(f, a, "Boss");

        Assert.Equal(renamed.Guid, group.LeaderGuid);
        Assert.Equal("Boss", group.LeaderName);
        Assert.Equal("Boss", group.Find(renamed.Guid)!.Name);
    }
}

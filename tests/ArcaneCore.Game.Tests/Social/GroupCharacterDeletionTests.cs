using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Social;

/// <summary>
/// The group half of character deletion (vmangos Player::DeleteFromDB → RemoveFromGroup): an
/// offline member leaves its group, a new leader is chosen when it led, and a two-member group
/// disbands, as an ordinary leave would. Friend lists and guilds are covered end to end in
/// ArcaneCore.World.Tests (CharacterDeleteCleanupTests).
/// </summary>
public sealed class GroupCharacterDeletionTests
{
    [Fact]
    public void Group_LosesAnOfflineLeader_AndKeepsTheOthersWithANewLeader()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        Player c = f.AddPlayer(3);
        f.Context.Groups.Invite(a, b.Name);
        f.Context.Groups.Accept(b);
        f.Context.Groups.Invite(a, c.Name);
        f.Context.Groups.Accept(c);
        Group group = f.Context.Groups.GetGroup(a.Guid)!;
        f.Context.Groups.OnLoggingOut(a);
        f.World.RemovePlayer(a);
        Assert.Same(group, f.Context.Groups.GetGroup(a.Guid)); // offline members stay
        f.ClearAll();

        Assert.True(f.Context.Groups.OnCharacterDeleted(a.Guid));

        Assert.Null(f.Context.Groups.GetGroup(a.Guid));
        Assert.Same(group, f.Context.Groups.GetGroup(b.Guid));
        Assert.Equal([b.Guid, c.Guid], group.Members.Select(m => m.Guid));
        Assert.NotEqual(a.Guid, group.LeaderGuid);
        Assert.NotEmpty(f.Sent(b, WorldOpcode.SmsgGroupSetLeader));
        Assert.False(f.Context.Groups.OnCharacterDeleted(a.Guid));
    }

    [Fact]
    public void Group_OfTwo_DisbandsWhenTheOfflineMemberIsDeleted()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        f.Context.Groups.Invite(a, b.Name);
        f.Context.Groups.Accept(b);
        f.Context.Groups.OnLoggingOut(b);
        f.World.RemovePlayer(b);
        f.ClearAll();

        Assert.True(f.Context.Groups.OnCharacterDeleted(b.Guid));

        Assert.Null(f.Context.Groups.GetGroup(a.Guid));
        Assert.Null(f.Context.Groups.GetGroup(b.Guid));
        Assert.NotEmpty(f.Sent(a, WorldOpcode.SmsgGroupList));
    }
}

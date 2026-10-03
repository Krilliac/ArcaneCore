using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Guilds;
using Xunit;

namespace ArcaneCore.Game.Tests.Social;

/// <summary>The join/leave events every membership path raises (Guild::AddMember / DelMember, Guild.cpp:197-278, :533-593).</summary>
public sealed class GuildManagerEventsTests
{
    private static Guild Found(SocialFixture f, Player leader)
    {
        f.Context.Guilds.Load([]);
        Assert.Equal(GuildAdminResult.Ok, f.Context.Guilds.Create(leader.Guid.Low, "Arcane", out Guild? guild));
        return guild!;
    }

    [Fact]
    public void FoundingAcceptingAndAdminInviting_RaiseMemberJoined()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        f.AddOffline(3);
        var joined = new List<uint>();
        f.Context.Guilds.MemberJoined += (_, id, _) => joined.Add(id);

        Found(f, a);
        f.Context.Guilds.Invite(a, b.Name);
        f.Context.Guilds.Accept(b);
        Assert.Equal(GuildAdminResult.Ok, f.Context.Guilds.AdminInvite(3, "Arcane"));

        Assert.Equal([1u, 2u, 3u], joined);
    }

    [Fact]
    public void LeavingAndUninviting_RaiseMemberLeft()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        f.AddOffline(3);
        Found(f, a);
        f.Context.Guilds.Invite(a, b.Name);
        f.Context.Guilds.Accept(b);
        f.Context.Guilds.AdminInvite(3, "Arcane");
        var left = new List<uint>();
        f.Context.Guilds.MemberLeft += (_, id) => left.Add(id);

        f.Context.Guilds.Leave(b);
        f.Context.Guilds.AdminUninvite(3);

        Assert.Equal([2u, 3u], left);
    }
}

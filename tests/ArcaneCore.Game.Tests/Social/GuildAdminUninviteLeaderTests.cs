using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Social;

/// <summary>
/// .guild uninvite of the guild master (vmangos HandleGuildUninviteCommand → Guild::DelMember, Guild.cpp:539-573):
/// DelMember itself broadcasts GE_LEADER_CHANGED then GE_LEFT once; the command adds nothing.
/// </summary>
public sealed class GuildAdminUninviteLeaderTests
{
    [Fact]
    public void AdminUninviteOfTheLeader_BroadcastsLeftOnce()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        f.Context.Guilds.Load([]);
        Assert.Equal(GuildAdminResult.Ok, f.Context.Guilds.Create(a.Guid.Low, "Arcane", out Guild? guild));
        f.Context.Guilds.Invite(a, b.Name);
        f.Context.Guilds.Accept(b);
        f.ClearAll();

        Assert.Equal(GuildAdminResult.Ok, f.Context.Guilds.AdminUninvite(a.Guid.Low));

        Assert.Equal(b.Guid.Low, guild!.LeaderId);
        List<GuildEvent> events = [.. f.Sent(b, WorldOpcode.SmsgGuildEvent).Select(p => (GuildEvent)p[0])];
        Assert.Equal([GuildEvent.LeaderChanged, GuildEvent.Left], events);
    }
}

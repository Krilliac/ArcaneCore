using ArcaneCore.Game.Channels;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Social;

/// <summary>
/// A custom channel is keyed and named from the same text (vmangos ChannelMgr::GetJoinChannel keys on the lowered
/// name, Channel::Channel runs normalizePlayerName, which only changes case: neither trims).
/// </summary>
public sealed class ChannelNameNormalizationTests
{
    [Fact]
    public void JoinWithATrailingSpace_AnnouncesTheNameTheChannelIsFoundBy_SoSayAndLeaveReachIt()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        f.ClearAll();

        f.Context.Channels.Join(a, "Foo ", string.Empty);

        var joined = new PacketReader(f.Sent(a, WorldOpcode.SmsgChannelNotify)[0]);
        Assert.Equal((byte)ChatNotify.YouJoined, joined.ReadByte());
        string announced = joined.ReadCString();

        // The client addresses the channel by the announced name from now on.
        Channel? channel = f.Context.Channels.Find(Team.Alliance, announced);
        Assert.NotNull(channel);
        Assert.True(channel.IsOn(a.Guid));

        f.ClearAll();
        f.Context.Channels.Leave(a, announced);
        Assert.Empty(f.Context.Channels.JoinedBy(a));
        Assert.DoesNotContain(f.Sent(a, WorldOpcode.SmsgChannelNotify), p => p[0] == (byte)ChatNotify.NotMember);
    }
}

using ArcaneCore.Game.Channels;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.Game.Tests.Social;
using Xunit;

namespace ArcaneCore.Game.Tests.Security;

/// <summary>
/// vmangos has no per-player channel cap (ChannelMgr.cpp:52-69), so a client can create
/// unlimited named channels. The cap is opt-in hardening (Social:MaxJoinedChannels, default 0 =
/// unlimited, as retail); the 1.12 UI joins at most about ten.
/// </summary>
public sealed class ChannelJoinCapTests
{
    [Fact]
    public void JoinBeyondTheCap_IsRefusedAndCreatesNoChannel()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        f.Context.Channels.MaxJoinedChannels = 3;

        for (int i = 0; i < 3; i++)
        {
            f.Context.Channels.Join(a, "chan" + i, string.Empty);
        }

        f.ClearAll();
        f.Context.Channels.Join(a, "overflow", string.Empty);

        Assert.Equal([ChatNotify.InvalidName], f.Sent(a, WorldOpcode.SmsgChannelNotify).Select(p => (ChatNotify)p[0]));
        Assert.Null(f.Context.Channels.Find(Team.Alliance, "overflow"));
        Assert.Equal(3, f.Context.Channels.JoinedBy(a).Count);

        // leaving frees a slot
        f.Context.Channels.Leave(a, "chan0");
        f.Context.Channels.Join(a, "overflow", string.Empty);
        Assert.NotNull(f.Context.Channels.Find(Team.Alliance, "overflow"));
    }

    [Fact]
    public void RejoiningAnAlreadyJoinedChannel_IsNotBlockedByTheCap()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        f.Context.Channels.MaxJoinedChannels = 1;
        f.Context.Channels.Join(a, "only", string.Empty);
        f.ClearAll();

        f.Context.Channels.Join(a, "only", string.Empty);

        Assert.DoesNotContain(ChatNotify.InvalidName,
            f.Sent(a, WorldOpcode.SmsgChannelNotify).Select(p => (ChatNotify)p[0]));
    }

    [Fact]
    public void ZeroMeansUnlimited_AndTheDefaultIsRetail()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Assert.Equal(0, f.Context.Channels.MaxJoinedChannels);

        for (int i = 0; i < 100; i++)
        {
            f.Context.Channels.Join(a, "chan" + i, string.Empty);
        }

        Assert.Equal(100, f.Context.Channels.JoinedBy(a).Count);
    }
}

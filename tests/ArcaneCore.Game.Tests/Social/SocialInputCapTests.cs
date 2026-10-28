using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Social;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Social;

/// <summary>Length caps on channel names and passwords, and the ready-check answer rate limit.</summary>
public sealed class SocialInputCapTests
{
    [Fact]
    public void Join_WithANameOverTheCap_IsRefused_AndNoChannelIsCreated()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        f.ClearAll();

        string name = "C" + new string('x', 400);
        f.Context.Channels.Join(a, name, string.Empty);

        Assert.Null(f.Context.Channels.Find(a.Team, name));
        Assert.Empty(f.Context.Channels.JoinedBy(a));
        Assert.NotEmpty(f.Sent(a, WorldOpcode.SmsgChannelNotify));
    }

    [Fact]
    public void Join_WithAPasswordOverTheCap_IsRefused()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        f.ClearAll();

        f.Context.Channels.Join(a, "Capped", new string('p', 400));

        Assert.Null(f.Context.Channels.Find(a.Team, "Capped"));
        Assert.Empty(f.Context.Channels.JoinedBy(a));
    }

    [Fact]
    public void Join_AtOrdinaryLengths_StillWorks()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        f.Context.Channels.Join(a, "Ordinary", "secret");
        Assert.NotNull(f.Context.Channels.Find(a.Team, "Ordinary"));
    }

    [Fact]
    public void ReadyCheckAnswers_AreAllForwardedByDefault_LikeVmangos()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        f.Context.Groups.Invite(a, b.Name);
        f.Context.Groups.Accept(b);
        f.ClearAll();

        Assert.Equal(0, new SocialOptions().ReadyCheckAnswerIntervalMs);
        for (int i = 0; i < 5; i++)
        {
            f.Context.Groups.ReadyCheck(b, i % 2);
        }

        Assert.Equal(5, f.Sent(a, WorldOpcode.MsgRaidReadyCheck).Count());
    }

    [Fact]
    public void ReadyCheckAnswers_WhenTheLimitIsSwitchedOn_AreRateLimitedPerMember()
    {
        using var f = new SocialFixture(new SocialOptions { ReadyCheckAnswerIntervalMs = 1000 });
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        f.Context.Groups.Invite(a, b.Name);
        f.Context.Groups.Accept(b);
        f.ClearAll();

        // vmangos forwards an answer with no pending check (GroupHandler.cpp HandleRaidReadyCheckOpcode), so the first one still arrives.
        f.Context.Groups.ReadyCheck(b, 1);
        for (int i = 0; i < 10; i++)
        {
            f.Context.Groups.ReadyCheck(b, 0);
        }

        Assert.Single(f.Sent(a, WorldOpcode.MsgRaidReadyCheck));
    }
}
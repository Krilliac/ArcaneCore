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
            f.Context.Groups.ReadyCheck(b, (byte)(i % 2));
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

    [Fact]
    public void SetPassword_OverTheCap_IsRefused_AndTheChannelKeepsItsPassword()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        f.Context.Channels.Join(a, "Capped", string.Empty);
        Channels.Channel channel = f.Context.Channels.Find(a.Team, "Capped")!;
        f.Context.Channels.SetPassword(a, "Capped", "old");
        Assert.Equal("old", channel.Password);
        f.ClearAll();

        f.Context.Channels.SetPassword(a, "Capped", new string('p', f.Context.Options.MaxChannelPassphraseLength + 1));

        Assert.Equal("old", channel.Password);
        Assert.Empty(f.Sent(a, WorldOpcode.SmsgChannelNotify));
    }

    [Fact]
    public void SetPassword_AtTheCap_IsAccepted()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        f.Context.Channels.Join(a, "AtCap", string.Empty);
        Channels.Channel channel = f.Context.Channels.Find(a.Team, "AtCap")!;
        string atCap = new('p', f.Context.Options.MaxChannelPassphraseLength);

        f.Context.Channels.SetPassword(a, "AtCap", atCap);

        Assert.Equal(atCap, channel.Password);
    }

    [Fact]
    public void ANewReadyCheck_ResetsTheAnswers_SoEveryMemberMayAnswerAgainAtOnce()
    {
        using var f = new SocialFixture(new SocialOptions { ReadyCheckAnswerIntervalMs = 1000 });
        long now = 5000;
        f.Context.Groups.MillisecondsClock = () => now;
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        f.Context.Groups.Invite(a, b.Name);
        f.Context.Groups.Accept(b);
        f.ClearAll();

        f.Context.Groups.ReadyCheck(b, 1);
        f.Context.Groups.ReadyCheck(b, 0); // inside the interval: refused
        Assert.Single(f.Sent(a, WorldOpcode.MsgRaidReadyCheck));

        f.Context.Groups.ReadyCheck(a, null); // the leader starts a new check, same instant
        f.ClearAll();
        f.Context.Groups.ReadyCheck(b, 1);
        Assert.Single(f.Sent(a, WorldOpcode.MsgRaidReadyCheck));
    }

    [Fact]
    public void TheReadyAnswerTable_AtTheCap_PrunesOnlyExpiredEntries()
    {
        using var f = new SocialFixture(new SocialOptions { ReadyCheckAnswerIntervalMs = 1000 });
        long now = 0;
        Groups.GroupManager groups = f.Context.Groups;
        groups.MillisecondsClock = () => now;
        var admit = typeof(Groups.GroupManager).GetMethod("AdmitReadyAnswer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var table = (System.Collections.IDictionary)typeof(Groups.GroupManager).GetField("_readyAnswerAt", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(groups)!;
        bool Admit(ulong guid) => (bool)admit.Invoke(groups, [new ObjectGuid(guid)])!;

        for (ulong g = 1; g <= 4095; g++)
        {
            Assert.True(Admit(g));
        }

        now = 900;
        Assert.True(Admit(5000)); // the 4096th entry, still fresh at t = 1000
        Assert.Equal(4096, table.Count);

        now = 1000; // the first 4095 are exactly one interval old
        Assert.True(Admit(6000));
        Assert.Equal(2, table.Count); // 5000 (age 100) and 6000 survive
        Assert.True(table.Contains(new ObjectGuid(5000)));
        Assert.False(Admit(5000)); // still limited: it was not pruned
    }
}

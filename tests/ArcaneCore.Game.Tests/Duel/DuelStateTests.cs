using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using Xunit;

namespace ArcaneCore.Game.Tests.Duel;

public sealed class DuelStateTests
{
    [Fact]
    public void FreshPlayer_HasNoDuel()
    {
        Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());

        Assert.Null(player.Duel);
        Assert.Equal(0ul, player.DuelArbiter);
        Assert.Equal(0u, player.DuelTeam);
    }

    [Fact]
    public void Arbiter_WritesBothWordsOfPlayerDuelArbiter_AndMarksThemChanged()
    {
        Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
        player.ClearChangedFields();

        player.DuelArbiter = 0x0000_0001_0000_0042UL;

        Assert.Equal(0x0000_0001_0000_0042UL, player.GetUInt64(UpdateFields.PlayerDuelArbiter));
        Assert.True(player.ChangedFields.GetBit(UpdateFields.PlayerDuelArbiter));
        Assert.True(player.ChangedFields.GetBit(UpdateFields.PlayerDuelArbiter + 1));
    }

    [Fact]
    public void Team_WritesPlayerDuelTeam()
    {
        Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());

        player.DuelTeam = 2;

        Assert.Equal(2u, player.GetUInt32(UpdateFields.PlayerDuelTeam));
    }

    [Fact]
    public void BothDuelFields_AreVisibleToOtherPlayers()
    {
        Player owner = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
        Player other = TestWorld.CreatePlayer(2, 0, 0, new FakeSession());
        owner.DuelArbiter = 77;
        owner.DuelTeam = 1;

        Assert.Equal(77u, owner.GetValueFor(UpdateFields.PlayerDuelArbiter, other));
        Assert.Equal(1u, owner.GetValueFor(UpdateFields.PlayerDuelTeam, other));
        Assert.True(((UpdateFieldFlags)owner.FieldFlags[UpdateFields.PlayerDuelArbiter] & UpdateFieldFlags.Public) != 0);
        Assert.True(((UpdateFieldFlags)owner.FieldFlags[UpdateFields.PlayerDuelTeam] & UpdateFieldFlags.Public) != 0);
    }

    [Fact]
    public void Options_DefaultToTheVmangosValues()
    {
        var options = new DuelOptions();

        Assert.True(options.Enabled);
        Assert.Equal(3, options.StartDelaySeconds);
        Assert.Equal(75f, options.OutOfBoundsYards);
        Assert.Equal(70f, options.ReturnInBoundsYards);
        Assert.Equal(10, options.OutOfBoundsGraceSeconds);
        Assert.False(options.RequireKnownArea);
        Assert.False(options.ExpiredRequestIsSilent);
        Assert.Equal("World:Duel", DuelOptions.SectionName);
    }

    [Fact]
    public void DuelInfo_StartsPending()
    {
        Player a = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
        Player b = TestWorld.CreatePlayer(2, 0, 0, new FakeSession());

        var info = new DuelInfo(initiator: a, opponent: b);

        Assert.Same(a, info.Initiator);
        Assert.Same(b, info.Opponent);
        Assert.Equal(0, info.StartTimerSeconds);
        Assert.Equal(0, info.StartTimeSeconds);
        Assert.Equal(0, info.OutOfBoundSeconds);
        Assert.False(info.Finished);
    }
}

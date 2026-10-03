using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.Reputation;
using Xunit;
using static ArcaneCore.Game.Tests.Reputation.ReputationFixtures;

namespace ArcaneCore.Game.Tests.Reputation;

/// <summary>ReputationMgr initialization, change, client toggle and load rules.</summary>
public sealed class PlayerReputationTests
{
    [Fact]
    public void Initialize_UsesRaceAndClassBaseAndDefaultFlags()
    {
        PlayerReputation human = Human();
        Assert.Equal(5, human.States.Count());
        Assert.Null(human.State(Get(Defias)));
        Assert.Equal(FactionStateFlags.Visible | FactionStateFlags.PeaceForced, human.State(Get(Stormwind))!.Flags);
        Assert.Equal(ReputationRank.Neutral, human.Rank(Get(Stormwind)));
        Assert.Equal(500, human.Reputation(Get(ClassBased)));
        Assert.Equal(0, human.State(Get(ClassBased))!.Standing);
        Assert.Equal(-1, human.WatchedFaction);

        var orcMage = new PlayerReputation(Factions, Race.Orc, Class.Mage);
        Assert.Equal(-42000, orcMage.Reputation(Get(Stormwind)));
        Assert.Equal(ReputationRank.Hated, orcMage.Rank(Get(Stormwind)));
        Assert.True(orcMage.State(Get(Stormwind))!.IsAtWar);
        Assert.Equal(100, orcMage.Reputation(Get(ClassBased)));
        Assert.Equal(FactionStateFlags.None, orcMage.State(Get(ClassBased))!.Flags);
    }

    [Fact]
    public void Apply_ClampsRelativeToBase_MakesVisibleOnce_AndRespectsForcedFlags()
    {
        PlayerReputation rep = Human();
        Assert.True(rep.Apply(Get(BootyBay), 100, incremental: true));
        Assert.True(rep.Apply(Get(BootyBay), 50, incremental: true));
        Assert.Equal(150, rep.Reputation(Get(BootyBay)));
        Assert.Equal([0], rep.TakeNewlyVisible());
        Assert.Empty(rep.TakeNewlyVisible());

        Assert.True(rep.Apply(Get(ClassBased), 100_000, incremental: false));
        Assert.Equal(ReputationMath.Cap, rep.Reputation(Get(ClassBased)));
        Assert.Equal(ReputationMath.Cap - 500, rep.State(Get(ClassBased))!.Standing);
        Assert.True(rep.Apply(Get(ClassBased), int.MinValue, incremental: true));
        Assert.Equal(ReputationMath.Bottom, rep.Reputation(Get(ClassBased)));

        Assert.True(rep.Apply(Get(Pirates), 10, incremental: true));
        Assert.Equal(FactionStateFlags.InvisibleForced, rep.State(Get(Pirates))!.Flags);
        Assert.True(rep.Apply(Get(AllianceParent), 10, incremental: true));
        Assert.Equal(FactionStateFlags.Hidden, rep.State(Get(AllianceParent))!.Flags);
        Assert.Empty(rep.TakeNewlyVisible());
        Assert.False(rep.Apply(Get(Defias), 10, incremental: true));
    }

    [Fact]
    public void DroppingToHostile_DeclaresWar_UnlessPeaceIsForcedAboveHated()
    {
        PlayerReputation rep = Human();
        rep.Apply(Get(BootyBay), -6000, incremental: false);
        Assert.True(rep.State(Get(BootyBay))!.IsAtWar);

        rep.Apply(Get(Stormwind), -6000, incremental: false);
        Assert.Equal(ReputationRank.Hostile, rep.Rank(Get(Stormwind)));
        Assert.False(rep.State(Get(Stormwind))!.IsAtWar);
        rep.Apply(Get(Stormwind), -42000, incremental: false);
        Assert.True(rep.State(Get(Stormwind))!.IsAtWar);
    }

    [Fact]
    public void ClientWarToggle_RefusesForcedPeaceHiddenInvisibleAndUnknownSlots()
    {
        PlayerReputation rep = Human();
        rep.Apply(Get(BootyBay), 1, incremental: true);
        Assert.True(rep.SetAtWarByClient(0, true));
        Assert.False(rep.SetAtWarByClient(0, true));
        Assert.True(rep.State(Get(BootyBay))!.IsAtWar);
        Assert.True(rep.SetAtWarByClient(0, false));
        Assert.False(rep.State(Get(BootyBay))!.IsAtWar);

        Assert.False(rep.SetAtWarByClient(7, true));   // forced peace, Neutral
        Assert.False(rep.SetAtWarByClient(5, true));   // forced invisible
        Assert.False(rep.SetAtWarByClient(10, true));  // hidden
        Assert.False(rep.SetAtWarByClient(63, true));  // no faction in the slot
        Assert.False(rep.SetAtWarByClient(-1, true));

        rep.Apply(Get(Stormwind), -42000, incremental: false); // Hated: war declared and allowed
        Assert.True(rep.SetAtWarByClient(7, false));
        Assert.True(rep.SetAtWarByClient(7, true));
    }

    [Fact]
    public void Inactive_RequiresAVisibleNonHiddenFaction()
    {
        PlayerReputation rep = Human();
        Assert.False(rep.SetInactiveByClient(0, true)); // not yet visible
        rep.Apply(Get(BootyBay), 1, incremental: true);
        Assert.True(rep.SetInactiveByClient(0, true));
        Assert.False(rep.SetInactiveByClient(0, true));
        Assert.Equal(FactionStateFlags.Visible | FactionStateFlags.Inactive, rep.State(Get(BootyBay))!.Flags);
        Assert.True(rep.SetInactiveByClient(0, false));
        Assert.False(rep.SetInactiveByClient(10, true));
        Assert.False(rep.SetInactiveByClient(5, true));
        Assert.False(rep.SetInactiveByClient(40, true));
    }

    [Fact]
    public void WatchedFaction_MustBeAVisibleSlotOrNone()
    {
        PlayerReputation rep = Human();
        Assert.True(rep.SetWatchedFaction(7));
        Assert.False(rep.SetWatchedFaction(7));
        Assert.False(rep.SetWatchedFaction(0));  // invisible yet
        Assert.False(rep.SetWatchedFaction(10)); // hidden
        Assert.False(rep.SetWatchedFaction(63));
        Assert.False(rep.SetWatchedFaction(-2));
        Assert.Equal(7, rep.WatchedFaction);
        Assert.True(rep.SetWatchedFaction(-1));
        Assert.Equal(-1, rep.WatchedFaction);
    }

    [Fact]
    public void Load_RestoresThroughTheRules_ClampsAndIgnoresUnknownRows()
    {
        PlayerReputation rep = Human();
        rep.Load(
        [
            new(1, Stormwind, 5000, (uint)(FactionStateFlags.Visible | FactionStateFlags.AtWar)), // war refused by forced peace
            new(1, BootyBay, -50000, (uint)FactionStateFlags.Visible),                               // clamped to Hated, war forced
            new(1, Pirates, 10, (uint)FactionStateFlags.Visible),                                    // cannot override forced invisibility
            new(1, ClassBased, 42, (uint)FactionStateFlags.Visible),                                 // matches defaults
            new(1, Defias, 1, 1),
            new(1, UnknownFaction, 1, 1),
        ], watchedFaction: 6);

        Assert.Equal(5000, rep.Reputation(Get(Stormwind)));
        Assert.Equal(FactionStateFlags.Visible | FactionStateFlags.PeaceForced, rep.State(Get(Stormwind))!.Flags);
        Assert.Equal(ReputationMath.Bottom, rep.Reputation(Get(BootyBay)));
        Assert.Equal(FactionStateFlags.Visible | FactionStateFlags.AtWar, rep.State(Get(BootyBay))!.Flags);
        Assert.Equal(FactionStateFlags.InvisibleForced, rep.State(Get(Pirates))!.Flags);
        Assert.Equal(542, rep.Reputation(Get(ClassBased)));
        Assert.Equal(6, rep.WatchedFaction);
        Assert.Empty(rep.TakeNewlyVisible());

        // Only the row whose flags changed on load needs saving; an untouched default row does not.
        CharacterReputationRow[] dirty = [.. rep.TakeDirty(1)];
        Assert.Contains(new CharacterReputationRow(1, BootyBay, ReputationMath.Bottom, 3), dirty);
        Assert.DoesNotContain(dirty, row => row.Faction == ClassBased);
        Assert.Empty(rep.TakeDirty(1));
    }

    [Theory]
    [InlineData(63)]
    [InlineData(-2)]
    [InlineData(int.MaxValue)]
    public void Load_DropsAWatchedFactionWithoutASlot(int watched)
    {
        PlayerReputation rep = Human();
        rep.Load([], watched);
        Assert.Equal(-1, rep.WatchedFaction);
    }

    [Fact]
    public void StandingUpdate_SendsThePrimaryFirst_ThenOtherPendingStates_AndDirtyRowsOnce()
    {
        PlayerReputation rep = Human();
        rep.MarkAllSent();
        rep.Apply(Get(Stormwind), 10, incremental: true);
        rep.Apply(Get(BootyBay), 20, incremental: true);
        Assert.Equal([(0, 20), (7, 10)], rep.TakeStandingUpdate(rep.State(Get(BootyBay))!));
        Assert.Equal([(0, 20)], rep.TakeStandingUpdate(rep.State(Get(BootyBay))!));
        Assert.Equal([new CharacterReputationRow(9, BootyBay, 20, 1), new CharacterReputationRow(9, Stormwind, 10, 0x11)], rep.TakeDirty(9));
        Assert.Empty(rep.TakeDirty(9));
    }
}

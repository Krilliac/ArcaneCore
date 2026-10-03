using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Talents;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;
using static ArcaneCore.Game.Tests.Talents.TalentRig;
using static ArcaneCore.Game.Tests.Talents.TalentServiceLearnTests;

namespace ArcaneCore.Game.Tests.Talents;

/// <summary>
/// Retail "disabled spell" semantics. A talent is always removed outright; the non-passive higher ranks and LEARN_SPELL
/// children a player holds are only disabled (hidden, remembered) and come back when the talent is relearned, while
/// passive dependents are removed. Reference: vmangos Player::RemoveSpell (Player.cpp:3797-3885: dependents :3803-3808,
/// "talents never disabled" :3818, higher non-talent ranks :3820-3827) and Player::LearnSpell (:3768-3796: re-learn
/// disabled higher ranks).
/// </summary>
public sealed class TalentDisabledSpellTests
{
    [Fact]
    public void ResetRemovesTheTalent_AndDisablesTheTrainerLearnedHigherRanks()
    {
        using var rig = new TalentRig();
        rig.Learn(5, 0);                                              // the ability itself is the talent: P1
        rig.Kit.Spellbook.Teach(rig.Player, P2, P3);                  // trainer-learned ranks
        rig.Session.Clear();

        Assert.True(rig.Service.ResetTalents(rig.Player, noCost: true));

        Assert.False(rig.Has(P1));
        Assert.False(rig.Has(P2));
        Assert.False(rig.Has(P3));
        Assert.Equal<uint>([P2, P3], [.. rig.Service.StateOf(rig.Player).Disabled.Order()]);
        Assert.Equal(3, Packets(rig.Session, WorldOpcode.SmsgRemovedSpell).Count);
        // vmangos recursion order: highest rank first, the talent itself last
        Assert.Equal(
            [(ushort)P3, (ushort)P2, (ushort)P1],
            Packets(rig.Session, WorldOpcode.SmsgRemovedSpell).Select(p => BitConverter.ToUInt16(p)).ToArray());
        Assert.Equal([(P3, true), (P2, true)], rig.Sink.Disabled);
    }

    [Fact]
    public void RelearningTheTalent_ReEnablesTheDisabledRanks_WithLearnedSpellPackets()
    {
        using var rig = new TalentRig();
        rig.Learn(5, 0);
        rig.Kit.Spellbook.Teach(rig.Player, P2, P3);
        Assert.True(rig.Service.ResetTalents(rig.Player, noCost: true));
        rig.Session.Clear();
        rig.Sink.Disabled.Clear();

        rig.Learn(5, 0);

        Assert.True(rig.Has(P1));
        Assert.True(rig.Has(P2));
        Assert.True(rig.Has(P3));
        Assert.Empty(rig.Service.StateOf(rig.Player).Disabled);
        Assert.Equal(
            [P1, P2, P3],
            Packets(rig.Session, WorldOpcode.SmsgLearnedSpell).Select(p => BitConverter.ToUInt32(p)).ToArray());
        Assert.Equal([(P2, false), (P3, false)], rig.Sink.Disabled.OrderBy(d => d.Spell));
    }

    [Fact]
    public void PassiveDependents_AreRemovedNotDisabled()
    {
        using var rig = new TalentRig();
        rig.Learn(5, 0);
        rig.Kit.Spellbook.Teach(rig.Player, PassiveChild);

        Assert.True(rig.Service.ResetTalents(rig.Player, noCost: true));

        Assert.False(rig.Has(PassiveChild));
        Assert.DoesNotContain(PassiveChild, rig.Service.StateOf(rig.Player).Disabled);
        rig.Learn(5, 0);
        Assert.False(rig.Has(PassiveChild));                         // a removed dependent is not given back
    }

    [Fact]
    public void ADisabledSpell_IsNotInTheBookOrHasSpell_AndStaysDisabledAcrossAnUnrelatedLearn()
    {
        using var rig = new TalentRig();
        rig.Learn(5, 0);
        rig.Kit.Spellbook.Teach(rig.Player, P2);
        Assert.True(rig.Service.ResetTalents(rig.Player, noCost: true));

        rig.Learn(1, 0);                                              // a different talent

        Assert.False(rig.Has(P2));
        Assert.Contains(P2, rig.Service.StateOf(rig.Player).Disabled);
    }

    [Fact]
    public void ALearnSpellChild_IsDisabledOnReset_AndComesBackWhenTheTalentIsRelearned()
    {
        using var rig = new TalentRig();
        rig.Learn(3, 0);                                              // talent 3 teaches T3Child by its LEARN_SPELL effect
        Assert.True(rig.Has(T3Child));

        Assert.True(rig.Service.ResetTalents(rig.Player, noCost: true));

        Assert.False(rig.Has(T3R1));
        Assert.False(rig.Has(T3Child));
        Assert.Contains(T3Child, rig.Service.StateOf(rig.Player).Disabled);   // active (non-passive) child: disabled

        rig.Learn(3, 0);

        Assert.True(rig.Has(T3Child));
        Assert.Empty(rig.Service.StateOf(rig.Player).Disabled);
    }

    [Fact]
    public void WithoutAChainOrBookEnumeration_TheTalentIsStillRemoved_AndNothingIsDisabled()
    {
        using var rig = new TalentRig();
        rig.Service.RankChain = null;
        rig.Learn(5, 0);
        rig.Kit.Spellbook.Teach(rig.Player, P2);

        Assert.True(rig.Service.ResetTalents(rig.Player, noCost: true));

        Assert.False(rig.Has(P1));
        Assert.True(rig.Has(P2));                                     // no chain knowledge: fail open for the dependent only
        Assert.Empty(rig.Service.StateOf(rig.Player).Disabled);
    }

    [Fact]
    public void LoadState_RestoresTheDisabledSet_AndRemovesAnyOfThemFromTheBookSilently()
    {
        // crash window: a spell row and a disabled row both persisted -> the spell is disabled (a lost ability, never a free one)
        using var rig = new TalentRig();
        rig.Kit.Spellbook.Teach(rig.Player, P2, 100);
        rig.Session.Clear();

        rig.Service.LoadState(rig.Player, new RespecState(2, Now), [P2, P3]);
        IReadOnlyList<uint> removed = rig.Service.RemoveDisabledFromBook(rig.Player);

        Assert.Equal<uint>([P2], removed);
        Assert.False(rig.Has(P2));
        Assert.True(rig.Has(100));
        Assert.Equal<uint>([P2, P3], [.. rig.Service.StateOf(rig.Player).Disabled.Order()]);
        Assert.Equal(new RespecState(2, Now), rig.Service.StateOf(rig.Player).Respec);
        Assert.Empty(rig.Session.Sent);
        Assert.Empty(rig.Sink.Disabled);                              // loading is not a change
    }

    [Fact]
    public void ADisabledRank_StaysDisabledWhileItsAncestorIsNeverRelearned()
    {
        using var rig = new TalentRig();
        rig.Learn(5, 0);
        rig.Kit.Spellbook.Teach(rig.Player, P2);
        Assert.True(rig.Service.ResetTalents(rig.Player, noCost: true));

        // "relog": a fresh service state loaded from the persisted disabled set
        rig.Service.LoadState(rig.Player, default, [.. rig.Service.StateOf(rig.Player).Disabled]);
        rig.Service.RemoveDisabledFromBook(rig.Player);

        Assert.False(rig.Has(P2));
        Assert.Contains(P2, rig.Service.StateOf(rig.Player).Disabled);
    }

    [Fact]
    public void TheTalentsResetEvent_FiresOncePerSuccessfulReset()
    {
        using var rig = new TalentRig();
        rig.Learn(5, 0);
        int fired = 0;
        rig.Service.TalentsReset += _ => fired++;

        Assert.True(rig.Service.ResetTalents(rig.Player, noCost: true));
        Assert.False(rig.Service.ResetTalents(rig.Player, noCost: true));   // nothing left to reset

        Assert.Equal(1, fired);
    }
}

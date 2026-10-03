using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Stealth;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Rogue;

/// <summary>
/// Invisibility (aura 18) against detection (aura 19): vmangos Unit::IsVisibleForOrDetect (Unit.cpp:6321-6461), CanDetectInvisibilityOf
/// (Unit.cpp:6502-6540), Aura::HandleInvisibility / HandleInvisibilityDetect (SpellAuras.cpp:3708-3780). The aura values are the classic-db
/// z2815 rows (base points + 1): Lesser Invisibility 7870 = 100, Greater Invisibility 16380 = 300, Detect Lesser Invisibility 132 = 100,
/// Detect Invisibility 2970 = 200, Detect Greater Invisibility 11743 = 300; every one uses invisibility type 0.
/// </summary>
public sealed class InvisibilityTests
{
    private const uint LesserInvisibility = 7870;
    private const uint GreaterInvisibility = 16380;
    private const uint DetectLesser = 132;
    private const uint Detect = 2970;
    private const uint DetectGreater = 11743;
    private const uint CancelledByInvisibility = 971_001;

    private sealed class Rig : IDisposable
    {
        public SpellTestKit Kit { get; }

        public Player Viewer { get; }

        public Player Target { get; }

        public Rig(bool attachRule = true)
        {
            static SpellInfo Perm(SpellInfo s) => s with { Duration = new SpellDuration(-1, 0, -1), SpellVisual = 1, StartRecoveryCategory = 0, StartRecoveryTime = 0 };
            Kit = new SpellTestKit(
                Perm(Spell(LesserInvisibility, Effect(SpellEffectName.ApplyAura, 100, aura: AuraType.ModInvisibility))),
                Perm(Spell(GreaterInvisibility, Effect(SpellEffectName.ApplyAura, 300, aura: AuraType.ModInvisibility))),
                Perm(Spell(DetectLesser, Effect(SpellEffectName.ApplyAura, 100, aura: AuraType.ModInvisibilityDetection))),
                Perm(Spell(Detect, Effect(SpellEffectName.ApplyAura, 200, aura: AuraType.ModInvisibilityDetection))),
                Perm(Spell(DetectGreater, Effect(SpellEffectName.ApplyAura, 300, aura: AuraType.ModInvisibilityDetection))),
                Perm(Spell(CancelledByInvisibility, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy))
                    with { AuraInterruptFlags = (SpellAuraInterruptFlags)AuraInterruptMask.StealthInvisibility }));
            if (attachRule)
            {
                Kit.World.GetMap(0).AddVisibilityRule(new InvisibilityVisibilityRule(Kit.System));
            }

            (Viewer, _) = Kit.AddPlayer(1);
            (Target, _) = Kit.AddPlayer(2, 5);
            Target.Relocate(5, 0, 83.5f, 0f, 0);
            Target.NeedsVisibilityUpdate = true;
            Kit.World.RunTick(0);
        }

        public bool ViewerSeesTarget => Viewer.VisibleObjects.Contains(Target.Guid);

        public void Cast(Unit unit, uint spell) => Kit.System.CastSpell(unit, spell, SpellCastTargets.ForSelf(), triggered: true);

        public void Dispose() => Kit.Dispose();
    }

    [Fact]
    public void AnInvisibleUnit_IsHiddenFromAViewerWithoutDetection_AndShownAgainWhenTheAuraGoes()
    {
        using var rig = new Rig();
        Assert.True(rig.ViewerSeesTarget);

        rig.Cast(rig.Target, LesserInvisibility);
        Assert.False(rig.ViewerSeesTarget);

        rig.Kit.System.RemoveAuras(rig.Target, LesserInvisibility);
        Assert.True(rig.ViewerSeesTarget);
    }

    [Theory]
    [InlineData(LesserInvisibility, DetectLesser, true)] // 100 <= 100
    [InlineData(LesserInvisibility, DetectGreater, true)] // 100 <= 300
    [InlineData(GreaterInvisibility, DetectLesser, false)] // 300 > 100
    [InlineData(GreaterInvisibility, Detect, false)] // 300 > 200
    [InlineData(GreaterInvisibility, DetectGreater, true)] // 300 <= 300
    public void DetectionSeesInvisibilityOnlyUpToItsLevel(uint invisibility, uint detection, bool seen)
    {
        using var rig = new Rig();
        rig.Cast(rig.Viewer, detection);

        rig.Cast(rig.Target, invisibility);

        Assert.Equal(seen, rig.ViewerSeesTarget);
    }

    [Fact]
    public void GainingOrLosingDetection_UpdatesWhatTheViewerSeesAtOnce()
    {
        using var rig = new Rig();
        rig.Cast(rig.Target, LesserInvisibility);
        Assert.False(rig.ViewerSeesTarget);

        rig.Cast(rig.Viewer, DetectLesser);
        Assert.True(rig.ViewerSeesTarget);

        rig.Kit.System.RemoveAuras(rig.Viewer, DetectLesser);
        Assert.False(rig.ViewerSeesTarget);
    }

    [Fact]
    public void ADetectionLevelOnlyCounts_ForTheMatchingInvisibilityType()
    {
        using var rig = new Rig();
        // A type 1 detection (made from the type 0 spell data with another misc value) does not reveal type 0.
        SpellInfo other = Spell(971_050, Effect(SpellEffectName.ApplyAura, 300, aura: AuraType.ModInvisibilityDetection, misc: 1))
            with { Duration = new SpellDuration(-1, 0, -1), SpellVisual = 1 };
        rig.Kit.System.Store = new SpellStore([.. rig.Kit.System.Store.All, other], [], []);
        rig.Cast(rig.Viewer, 971_050);

        rig.Cast(rig.Target, LesserInvisibility);

        Assert.False(rig.ViewerSeesTarget);
    }

    [Fact]
    public void TwoUnitsUnderTheSameInvisibilityType_SeeEachOther()
    {
        using var rig = new Rig();
        rig.Cast(rig.Viewer, LesserInvisibility);

        rig.Cast(rig.Target, GreaterInvisibility); // type 0 as well: the masks share a bit

        Assert.True(rig.ViewerSeesTarget);
    }

    [Fact]
    public void AGroupMateAndAGameMasterSeeAnInvisiblePlayer_AStrangerDoesNot()
    {
        using var rig = new Rig();
        var groups = new FakeGroups();
        groups.Parties.Add([rig.Viewer.Guid, rig.Target.Guid]);
        rig.Kit.System.Groups = groups;
        (Player stranger, _) = rig.Kit.AddPlayer(3, 6);
        (Player gm, _) = rig.Kit.AddPlayer(4, 7);
        gm.SetUInt32(UpdateFields.PlayerFlags, (uint)PlayerFlags.Gm);
        rig.Kit.World.RunTick(0);
        Assert.Contains(rig.Target.Guid, stranger.VisibleObjects);

        rig.Cast(rig.Target, LesserInvisibility);

        Assert.True(rig.ViewerSeesTarget); // same party
        Assert.Contains(rig.Target.Guid, gm.VisibleObjects);
        Assert.DoesNotContain(rig.Target.Guid, stranger.VisibleObjects);
    }

    [Fact]
    public void ApplyingInvisibility_CancelsTheAurasThatBreakOnIt_AndRaisesTheGlowFlag()
    {
        using var rig = new Rig();
        rig.Cast(rig.Target, CancelledByInvisibility);
        Assert.True(rig.Kit.System.HasAura(rig.Target, CancelledByInvisibility));
        Assert.Equal(0, rig.Target.GetByte(UpdateFields.PlayerFieldBytes2, 1) & 0x40);

        rig.Cast(rig.Target, LesserInvisibility);

        Assert.False(rig.Kit.System.HasAura(rig.Target, CancelledByInvisibility));
        Assert.Equal(0x40, rig.Target.GetByte(UpdateFields.PlayerFieldBytes2, 1) & 0x40);

        rig.Cast(rig.Target, GreaterInvisibility);
        rig.Kit.System.RemoveAuras(rig.Target, LesserInvisibility);
        Assert.Equal(0x40, rig.Target.GetByte(UpdateFields.PlayerFieldBytes2, 1) & 0x40); // another invisibility aura is left

        rig.Kit.System.RemoveAuras(rig.Target, GreaterInvisibility);
        Assert.Equal(0, rig.Target.GetByte(UpdateFields.PlayerFieldBytes2, 1) & 0x40);
    }

    [Fact]
    public void WithoutTheRule_InvisibilityHidesNothing()
    {
        using var rig = new Rig(attachRule: false);

        rig.Cast(rig.Target, LesserInvisibility);

        Assert.True(rig.ViewerSeesTarget);
    }
}

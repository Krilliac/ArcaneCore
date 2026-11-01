using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// Improved Sprint end to end with the shapes of the 1.12.1 client's Spell.dbc (client-dbc-5875): talent 13743 is a passive ADD_TARGET_TRIGGER
/// (aura 109) of 30918 with an EMPTY EffectItemType, family Rogue (8); Sprint 8696 is family 8 flags 0x40; Improved Sprint 30918 has AttributesEx
/// 0x8000 (immunity purges the effect) and two MECHANIC_IMMUNITY (77) effects, misc 11 (snare) and 7 (root). The class mask of the talent only exists
/// through the built-in fixup, so a rogue who is rooted and snared and casts Sprint loses both only with it.
/// </summary>
public sealed class ImprovedSprintRealDataTests
{
    private const uint ImprovedSprintRank1 = 13743;
    private const uint Sprint = 8696;
    private const uint ImprovedSprintTrigger = 30918;
    private const uint EntanglingRoots = 339;
    private const uint WingClip = 2974;

    private static SpellTestKit NewKit() => new(
        // Spell.dbc 13743: Attributes 0x1d0 (passive), duration index 21 (-1), effect 0 APPLY_AURA 109, base points 49, trigger 30918, item type 0.
        Spell(ImprovedSprintRank1, Effect(SpellEffectName.ApplyAura, 50, SpellImplicitTarget.UnitCaster, AuraType.AddTargetTrigger, trigger: ImprovedSprintTrigger)) with
        {
            SpellFamilyName = 8,
            Attributes = (SpellAttributes)0x1d0,
            Duration = new SpellDuration(-1, 0, -1),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        },
        // Spell.dbc 8696: Attributes 0x10010, AttributesEx 0x20, family 8 flags 0x40, effect 0 APPLY_AURA 31 (mod increase speed), 15 s.
        Spell(Sprint, Effect(SpellEffectName.ApplyAura, 60, SpellImplicitTarget.UnitCaster, AuraType.ModIncreaseSpeed)) with
        {
            SpellFamilyName = 8,
            SpellFamilyFlags = 0x40,
            Attributes = (SpellAttributes)0x10010,
            AttributesEx = (SpellAttributesEx)0x20,
            Duration = new SpellDuration(15_000, 0, 15_000),
            SpellVisual = 1,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        },
        // Spell.dbc 30918: Attributes 0x40080, AttributesEx 0x8000, two APPLY_AURA 77 effects on the caster, misc 11 and 7.
        Spell(ImprovedSprintTrigger,
            Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.MechanicImmunity, misc: 11),
            Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.MechanicImmunity, misc: 7)) with
        {
            Attributes = (SpellAttributes)0x40080,
            AttributesEx = (SpellAttributesEx)0x8000,
            Duration = new SpellDuration(5_000, 0, 5_000),
            SpellVisual = 1,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        },
        // Spell.dbc 339 Entangling Roots: Mechanic 7, dispel 1, effect 0 APPLY_AURA 26 (root), effect 1 APPLY_AURA 3 (damage over time).
        Spell(EntanglingRoots, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.ModRoot)) with
        {
            Mechanic = 7,
            Dispel = 1,
            Duration = new SpellDuration(12_000, 0, 12_000),
            SpellVisual = 1,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        },
        // Spell.dbc 2974 Wing Clip: effect 0 APPLY_AURA 33 (mod decrease speed) with effect mechanic 11 (snare).
        Spell(WingClip, Effect(SpellEffectName.ApplyAura, -50, SpellImplicitTarget.UnitEnemy, AuraType.ModDecreaseSpeed) with { Mechanic = 11 }) with
        {
            Duration = new SpellDuration(10_000, 0, 10_000),
            SpellVisual = 1,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        });

    [Fact]
    public void RootedAndSnaredRogue_WithImprovedSprint_LosesBothWhenSprintProcs()
    {
        using SpellTestKit kit = NewKit();
        (Player rogue, _) = kit.AddPlayer(1);
        (Player druid, _) = kit.AddHostilePlayer(2);
        kit.System.CastSpell(rogue, ImprovedSprintRank1, SpellCastTargets.ForSelf(), triggered: true);

        // Rank 1 is a 50 percent chance; the kit's random is seeded, so the first proc is reached in a fixed number of casts.
        bool procced = false;
        for (int attempt = 0; attempt < 40 && !procced; attempt++)
        {
            kit.System.RemoveAuras(rogue, Sprint);
            kit.System.CastSpell(druid, EntanglingRoots, SpellCastTargets.ForUnit(rogue.Guid), triggered: true);
            kit.System.CastSpell(druid, WingClip, SpellCastTargets.ForUnit(rogue.Guid), triggered: true);
            Assert.True(kit.System.HasAura(rogue, EntanglingRoots));
            Assert.True(kit.System.HasAura(rogue, WingClip));

            kit.System.CastSpell(rogue, Sprint, SpellCastTargets.ForSelf(), triggered: true);
            procced = kit.System.HasAura(rogue, ImprovedSprintTrigger);
            if (!procced)
            {
                Assert.True(kit.System.HasAura(rogue, EntanglingRoots)); // no proc, no relief
                Assert.True(kit.System.HasAura(rogue, WingClip));
            }
        }

        Assert.True(procced, "Improved Sprint never triggered");
        Assert.False(kit.System.HasAura(rogue, EntanglingRoots));
        Assert.False(kit.System.HasAura(rogue, WingClip));
        Assert.True(kit.System.HasAura(rogue, Sprint));
    }

    [Fact]
    public void RootedAndSnaredRogue_WithoutTheTalent_KeepsBoth()
    {
        using SpellTestKit kit = NewKit();
        (Player rogue, _) = kit.AddPlayer(1);
        (Player druid, _) = kit.AddHostilePlayer(2);
        kit.System.CastSpell(druid, EntanglingRoots, SpellCastTargets.ForUnit(rogue.Guid), triggered: true);
        kit.System.CastSpell(druid, WingClip, SpellCastTargets.ForUnit(rogue.Guid), triggered: true);

        kit.System.CastSpell(rogue, Sprint, SpellCastTargets.ForSelf(), triggered: true);

        Assert.False(kit.System.HasAura(rogue, ImprovedSprintTrigger));
        Assert.True(kit.System.HasAura(rogue, EntanglingRoots));
        Assert.True(kit.System.HasAura(rogue, WingClip));
    }
}

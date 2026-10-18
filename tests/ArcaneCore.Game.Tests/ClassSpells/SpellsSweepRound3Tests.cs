using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.ClassScripts;
using ArcaneCore.Game.Spells.Rogue;
using ArcaneCore.Game.Spells.Warlock;
using ArcaneCore.Protocol;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.ClassSpells;

/// <summary>Spells live-sweep round 3 (2026-10-17 night): vmangos parity fixes from the spells open list.</summary>
public sealed class SpellsSweepRound3Tests
{
    private static SpellInfo SelfAura(uint id, AuraType type, int amount, int misc = 0) =>
        Spell(id, Effect(SpellEffectName.ApplyAura, amount, SpellImplicitTarget.UnitCaster, type, misc: misc)) with
        {
            Duration = new SpellDuration(60_000, 0, 60_000),
            SpellVisual = 1,
        };

    [Fact]
    public void Bloodthirst_AddsAttackPowerVersusTheTargetsCreatureType()
    {
        const uint bloodthirst = 23881, versusHumanoid = 90001, versusBeast = 90002;
        using var kit = new SpellTestKit(
            Spell(bloodthirst, Effect(SpellEffectName.SchoolDamage, 45, SpellImplicitTarget.UnitEnemy)),
            SelfAura(versusHumanoid, AuraType.ModMeleeAttackPowerVersus, 100, misc: 1 << 6),
            SelfAura(versusBeast, AuraType.ModMeleeAttackPowerVersus, 70, misc: 1 << 0));
        (Player warrior, _) = kit.AddPlayer(1);
        (Player enemy, _) = kit.AddPlayer(2, 2, 0);
        Assert.True(kit.System.AddAura(warrior, versusHumanoid));
        Assert.True(kit.System.AddAura(warrior, versusBeast));

        SpellInfo spell = kit.System.Store.Get(bloodthirst)!;
        var modifier = new BloodthirstDamage(kit.System);
        float ap = RogueBleedScripts.MeleeAttackPower(warrior);

        Assert.Equal((int)(45 * ap / 100), modifier.Modify(SpellValueKind.EffectValue, new SpellValueContext(warrior, spell, 0), 45));
        Assert.Equal((int)(45 * (ap + 100) / 100), modifier.Modify(SpellValueKind.EffectValue, new SpellValueContext(warrior, spell, 0, enemy), 45));
    }

    [Theory]
    [InlineData(28863u, 0.3f)]   // Void Zone: raised against falling under the ground
    [InlineData(27810u, 0f)]     // Shadow Fissure
    public void CasterTargetPosition_PutsTheDestinationAtTheExplicitTarget(uint spellId, float raise)
    {
        using var kit = new SpellTestKit(
            Spell(spellId, Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.LocationCasterTargetPosition)) with
            {
                RangeIndex = 4, Range = new SpellRange(0, 40), StartRecoveryCategory = 0, StartRecoveryTime = 0,
            },
            Spell(18392, Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.LocationCasterTargetPosition)) with
            {
                RangeIndex = 4, Range = new SpellRange(0, 40), StartRecoveryCategory = 0, StartRecoveryTime = 0,
            });
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 12, 5);
        SpellCastTargets? seen = null;
        kit.System.RegisterEffect(SpellEffectName.Dummy, context => seen = context.Cast.Targets);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, spellId, SpellCastTargets.ForUnit(target.Guid), triggered: true));
        Assert.NotNull(seen);
        Assert.True(seen!.HasDest);
        Assert.Equal((target.X, target.Y, target.Z + raise), seen.Dest);

        seen = null;
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, 18392u, SpellCastTargets.ForUnit(target.Guid), triggered: true));
        Assert.NotNull(seen);
        Assert.False(seen!.HasDest); // Onyxia's Fireball keeps its original visual
    }

    [Theory]
    [InlineData(Class.Warlock, true)]
    [InlineData(Class.Mage, false)]
    public void ACancelledRitualOfDoom_ClearsTheWarlocksClientCooldown(Class cls, bool cleared)
    {
        using var kit = new SpellTestKit(
            Spell(RitualOfDoomCooldownFix.RitualOfDoom, Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.UnitCaster)) with
            {
                CastTime = new SpellCastTime(10_000, 0, 10_000),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            });
        (Player caster, FakeSession session) = kit.AddPlayer(1);
        caster.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)cls);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, RitualOfDoomCooldownFix.RitualOfDoom, SpellCastTargets.ForSelf(), triggered: false));
        session.Sent.Clear();

        kit.System.CancelCast(caster, RitualOfDoomCooldownFix.RitualOfDoom);

        Assert.Equal(cleared, session.Sent.Any(p => p.Opcode == WorldOpcode.SmsgClearCooldown));
    }
}
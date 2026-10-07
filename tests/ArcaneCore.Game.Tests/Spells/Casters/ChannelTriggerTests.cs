using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells.Casters;

/// <summary>
/// A periodic-trigger channel whose aura sits on the caster (Arcane Missiles: effect 0 PERIODIC_TRIGGER_SPELL on
/// TARGET_UNIT_CASTER) casts the triggered spell at the CHANNEL TARGET, not at the aura holder.
/// vmangos SpellAuras.cpp:1519-1556 (Aura::TriggerSpell).
/// </summary>
public sealed class ChannelTriggerTests
{
    private const uint Missiles = 3001;
    private const uint Missile = 3002;
    private const uint SelfTrigger = 3003;
    private const uint SelfTickAura = 3004;

    private static SpellTestKit NewKit() => new(
        // Arcane Missiles shape: effect 0 trigger aura on the caster, effect 1 a dummy aura on the enemy.
        SpellTestKit.Spell(
            Missiles,
            SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.PeriodicTriggerSpell, amplitude: 1000, trigger: Missile),
            SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.Dummy)) with
        {
            AttributesEx = SpellAttributesEx.IsChanneled,
            Duration = new SpellDuration(3000, 0, 3000),
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            SpellVisual = 1,
        },
        SpellTestKit.Spell(Missile, SpellTestKit.Effect(SpellEffectName.Heal, 1, SpellImplicitTarget.Unit)),
        // A non-channelled periodic trigger on the holder keeps casting at the holder.
        SpellTestKit.Spell(SelfTickAura, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.PeriodicTriggerSpell, amplitude: 1000, trigger: SelfTrigger)) with
        {
            Duration = new SpellDuration(2000, 0, 2000),
            SpellVisual = 1,
        },
        SpellTestKit.Spell(SelfTrigger, SpellTestKit.Effect(SpellEffectName.Heal, 1, SpellImplicitTarget.Unit)),
        // The Arcane Missiles shape again, but its triggered spell may target the dead (SPELL_ATTR_EX2_ALLOW_DEAD_TARGET).
        SpellTestKit.Spell(
            DeadMissiles,
            SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.PeriodicTriggerSpell, amplitude: 1000, trigger: DeadMissile),
            SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.Dummy)) with
        {
            AttributesEx = SpellAttributesEx.IsChanneled,
            Duration = new SpellDuration(3000, 0, 3000),
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            SpellVisual = 1,
        },
        SpellTestKit.Spell(DeadMissile, SpellTestKit.Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.Unit)) with
        {
            AttributesEx2 = SpellAttributesEx2.AllowDeadTarget,
        });

    private const uint DeadMissiles = 3005;
    private const uint DeadMissile = 3006;

    [Fact]
    public void ArcaneMissilesShape_TriggersAtTheChannelTarget_NotAtTheCaster()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player enemy, _) = kit.AddPlayer(2, 10, 0);
        kit.Spellbook.Teach(caster, Missiles);
        List<(Unit Caster, Unit Target)> hits = [];
        kit.System.SpellHit += (c, t, s) =>
        {
            if (s.Id == Missile)
            {
                hits.Add((c, t));
            }
        };

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, Missiles, SpellCastTargets.ForUnit(enemy.Guid)));
        kit.Advance(3000, step: 100);

        Assert.Equal(3, hits.Count);
        Assert.All(hits, hit =>
        {
            Assert.Same(caster, hit.Caster);
            Assert.Same(enemy, hit.Target);
        });
    }

    [Fact]
    public void NonChannelledPeriodicTrigger_StillTargetsTheHolder()
    {
        using SpellTestKit kit = NewKit();
        (Player player, _) = kit.AddPlayer(1);
        List<Unit> targets = [];
        kit.System.SpellHit += (c, t, s) =>
        {
            if (s.Id == SelfTrigger)
            {
                targets.Add(t);
            }
        };

        kit.System.CastSpell(player, SelfTickAura, SpellCastTargets.ForSelf(), triggered: true);
        kit.Advance(2000, step: 100);

        Assert.Equal(2, targets.Count);
        Assert.All(targets, t => Assert.Same(player, t));
    }

    [Fact]
    public void TheChannelEnding_StopsFurtherMissiles()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player enemy, _) = kit.AddPlayer(2, 10, 0);
        kit.Spellbook.Teach(caster, Missiles);
        int hits = 0;
        kit.System.SpellHit += (c, t, s) => hits += s.Id == Missile ? 1 : 0;
        kit.System.HandleCastRequest(caster, Missiles, SpellCastTargets.ForUnit(enemy.Guid));

        kit.Advance(3000, step: 100);
        int atEnd = hits;
        kit.Advance(3000, step: 100);

        Assert.Equal(atEnd, hits);
        Assert.DoesNotContain(kit.System.GetAuras(caster), h => h.Spell.Id == Missiles);
    }

    [Fact]
    public void AChannelTargetThatDied_ReceivesNoFurtherTriggeredSpells()
    {
        // vmangos: the triggered spell's CheckCast refuses a dead explicit target (Spell.cpp:5572, CanTargetAliveState), so a corpse
        // never receives EffectTriggerSpell; the damage, heal and energize ticks already stop on a dead target.
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player enemy, _) = kit.AddPlayer(2, 10, 0);
        kit.Spellbook.Teach(caster, Missiles);
        int liveHits = 0;
        int corpseHits = 0;
        kit.System.SpellHit += (c, t, s) =>
        {
            if (s.Id == Missile)
            {
                _ = t.IsAlive ? liveHits++ : corpseHits++;
            }
        };

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, Missiles, SpellCastTargets.ForUnit(enemy.Guid)));
        kit.Advance(1000, step: 100);
        enemy.Health = 0;
        kit.Advance(2000, step: 100);

        Assert.Equal(1, liveHits);
        Assert.Equal(0, corpseHits);
    }

    [Fact]
    public void ATriggeredSpellThatMayTargetTheDead_StillFiresAtADeadChannelTarget()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player enemy, _) = kit.AddPlayer(2, 10, 0);
        kit.Spellbook.Teach(caster, DeadMissiles);
        int corpseHits = 0;
        kit.System.SpellHit += (c, t, s) => corpseHits += s.Id == DeadMissile && !t.IsAlive ? 1 : 0;

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, DeadMissiles, SpellCastTargets.ForUnit(enemy.Guid)));
        kit.Advance(1000, step: 100);
        enemy.Health = 0;
        kit.Advance(2000, step: 100);

        Assert.Equal(2, corpseHits);
    }
}

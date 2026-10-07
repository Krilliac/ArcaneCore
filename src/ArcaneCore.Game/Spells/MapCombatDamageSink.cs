using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Combat.Threat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Spells;

/// <summary>Spell damage and healing through map combat: damage, death, threat and combat state (the world daemon's <see cref="IDamageSink"/>).</summary>
public class MapCombatDamageSink : IDamageSink
{
    public uint DealSpellDamage(Unit caster, Unit victim, SpellInfo spell, uint damage, bool periodic)
        => DealSpellDamage(caster, victim, spell, damage, periodic, startsCombat: true);

    public uint DealSpellDamage(Unit caster, Unit victim, SpellInfo spell, uint damage, bool periodic, bool startsCombat)
        => DealSpellDamage(caster, victim, spell, damage, periodic, startsCombat, critical: false);

    public uint DealSpellDamage(Unit caster, Unit victim, SpellInfo spell, uint damage, bool periodic, bool startsCombat, bool critical)
        => DealSpellDamage(caster, victim, spell, damage, periodic, startsCombat, critical, durabilityLoss: true);

    public uint DealSpellDamage(Unit caster, Unit victim, SpellInfo spell, uint damage, bool periodic, bool startsCombat, bool critical, bool durabilityLoss)
    {
        if (caster.Map is not { } map || !ReferenceEquals(map, victim.Map))
        {
            return 0;
        }

        uint health = victim.Health;
        // The spell and the crit flag give the threat formula its school, spell_threat multiplier and MOD_CRITICAL_THREAT (MapCombat.AddDamageThreat);
        // the spell also carries the death durability exemption, and durabilityLoss is DealDamage's own flag (instant kill, split damage).
        map.Combat.DealDamage(caster, victim, damage, direct: !periodic, meleeDamage: false, startsCombat: startsCombat, threatSpell: spell, critical: critical,
            durabilityLoss: durabilityLoss);
        return health - Math.Min(health, victim.Health);
    }

    public uint Heal(Unit caster, Unit target, SpellInfo spell, uint amount) => Heal(caster, target, spell, amount, periodic: false);

    /// <summary>A heal of <paramref name="origin"/>: see <see cref="IDamageSink.Heal(Unit, Unit, SpellInfo, uint, IDamageSink.HealingOrigin)"/>.</summary>
    public uint Heal(Unit caster, Unit target, SpellInfo spell, uint amount, IDamageSink.HealingOrigin origin)
        => origin switch
        {
            IDamageSink.HealingOrigin.NoThreat => RestoreHealth(caster, target, amount, out _),
            IDamageSink.HealingOrigin.Periodic or IDamageSink.HealingOrigin.PeriodicLeech => Heal(caster, target, spell, amount, periodic: true),
            _ => Heal(caster, target, spell, amount, periodic: false),
        };

    /// <summary>
    /// vmangos SPELL_AURA_PERIODIC_ENERGIZE (SpellAuras.cpp:6248-6256): the effective gain of a power other than mana or happiness assists half
    /// of it, times the spell's spell_threat multiplier, to every list that holds the target ("1.9 - Mana regeneration over time will no longer
    /// generate threat").
    /// </summary>
    public void AssistPeriodicEnergizeThreat(Unit caster, Unit target, SpellInfo spell, uint effectiveGain, PowerType power)
    {
        if (effectiveGain == 0 || power is PowerType.Mana or PowerType.Happiness || caster.Map is not { } map
            || !ReferenceEquals(map, target.Map) || !caster.IsAlive || !target.IsAlive)
        {
            return;
        }

        float multiplier = map.Combat.SpellThreatCatalog?.Find(spell.Id)?.Multiplier ?? 1f;
        foreach (Unit holder in HostileRefs.ThreatAssist(target, caster, effectiveGain * 0.5f * multiplier, spell, map.Combat.ThreatModifiers))
        {
            map.Combat.Track(holder);
        }
    }

    /// <summary>Restore up to <paramref name="amount"/> health (vmangos DealHeal without the threat): the health actually restored.</summary>
    private static uint RestoreHealth(Unit caster, Unit target, uint amount, out Map? map)
    {
        map = null;
        if (!target.IsAlive || caster.Map is not { } casterMap || !ReferenceEquals(casterMap, target.Map))
        {
            return 0;
        }

        map = casterMap;
        uint healed = Math.Min(amount, target.MaxHealth - Math.Min(target.Health, target.MaxHealth));
        target.Health += healed;
        return healed;
    }

    public uint Heal(Unit caster, Unit target, SpellInfo spell, uint amount, bool periodic)
    {
        uint healed = RestoreHealth(caster, target, amount, out Map? map);
        if (map is null)
        {
            return 0;
        }
        // vmangos Spell::DoAllEffectOnTarget / HostileRefManager::threatAssist (Spell.cpp:1362-1366, HostileRefManager.cpp:62-76):
        // half the effective gain (a paladin's direct heal a quarter) times the spell's spell_threat multiplier, split over every list that
        // holds the target, as assist threat, through the threat formula.
        Unit[] enemies = HealThreat.ForHeal(map.Combat, caster, target, spell, healed, periodic);
        if (enemies.Length > 0)
        {
            foreach (Unit enemy in enemies)
            {
                map.Combat.Track(enemy);
            }

            if (caster.Combat.ThreatenedBy.Count > 0)
            {
                map.Combat.SetInCombatState(caster, 0);
            }
        }

        return healed;
    }
}


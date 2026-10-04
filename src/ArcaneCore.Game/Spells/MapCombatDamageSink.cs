using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Combat.Threat;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

/// <summary>Spell damage and healing through map combat: damage, death, threat and combat state (the world daemon's <see cref="IDamageSink"/>).</summary>
public class MapCombatDamageSink : IDamageSink
{
    public uint DealSpellDamage(Unit caster, Unit victim, SpellInfo spell, uint damage, bool periodic)
        => DealSpellDamage(caster, victim, spell, damage, periodic, startsCombat: true);

    public uint DealSpellDamage(Unit caster, Unit victim, SpellInfo spell, uint damage, bool periodic, bool startsCombat)
        => DealSpellDamage(caster, victim, spell, damage, periodic, startsCombat, critical: false);

    public uint DealSpellDamage(Unit caster, Unit victim, SpellInfo spell, uint damage, bool periodic, bool startsCombat, bool critical)
    {
        if (caster.Map is not { } map || !ReferenceEquals(map, victim.Map))
        {
            return 0;
        }

        uint health = victim.Health;
        // The spell and the crit flag give the threat formula its school, spell_threat multiplier and MOD_CRITICAL_THREAT (MapCombat.AddDamageThreat).
        map.Combat.DealDamage(caster, victim, damage, direct: !periodic, meleeDamage: false, startsCombat: startsCombat, threatSpell: spell, critical: critical);
        return health - Math.Min(health, victim.Health);
    }

    public uint Heal(Unit caster, Unit target, SpellInfo spell, uint amount) => Heal(caster, target, spell, amount, periodic: false);

    public uint Heal(Unit caster, Unit target, SpellInfo spell, uint amount, bool periodic)
    {
        if (!target.IsAlive || caster.Map is not { } map || !ReferenceEquals(map, target.Map))
        {
            return 0;
        }

        uint healed = Math.Min(amount, target.MaxHealth - Math.Min(target.Health, target.MaxHealth));
        target.Health += healed;
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


using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Teleport;

namespace ArcaneCore.World.Spells;

/// <summary>Resolve players and creatures through the map's shared object registry.</summary>
internal sealed class WorldSpellUnitResolver : ISpellUnitResolver
{
    public Unit? Find(Unit reference, ObjectGuid guid)
        => guid.IsEmpty ? null : reference.Guid == guid ? reference : reference.Map?.FindObject(guid) as Unit;
}

/// <summary>World-thread spell effects use map combat for damage, death and threat.</summary>
internal sealed class WorldSpellDamageSink : IDamageSink
{
    private readonly SpellSystem _spells;

    public WorldSpellDamageSink(SpellSystem spells) => _spells = spells;

    public uint DealSpellDamage(Unit caster, Unit victim, SpellInfo spell, uint damage, bool periodic)
        => DealSpellDamage(caster, victim, spell, damage, periodic, startsCombat: true);

    public uint DealSpellDamage(Unit caster, Unit victim, SpellInfo spell, uint damage, bool periodic, bool startsCombat)
        => DealSpellDamage(caster, victim, spell, damage, periodic, startsCombat, durabilityLoss: true);

    public uint DealSpellDamage(Unit caster, Unit victim, SpellInfo spell, uint damage, bool periodic, bool startsCombat, bool durabilityLoss)
    {
        if (caster.Map is not { } map || !ReferenceEquals(map, victim.Map))
        {
            return 0;
        }

        uint health = victim.Health;
        float? spellThreat = _spells.CalculateSpellThreat(caster, victim, spell, damage);
        map.Combat.DealDamage(caster, victim, damage, direct: !periodic, meleeDamage: false,
            startsCombat: startsCombat, durabilityLoss: durabilityLoss, spell: spell,
            spellThreat: spellThreat, spellThreatApplies: spellThreat is not null);
        return health - Math.Min(health, victim.Health);
    }

    public uint Heal(Unit caster, Unit target, SpellInfo spell, uint amount)
        => Heal(caster, target, spell, amount, IDamageSink.HealingOrigin.Legacy);

    public uint Heal(Unit caster, Unit target, SpellInfo spell, uint amount, IDamageSink.HealingOrigin origin)
    {
        if (!target.IsAlive || caster.Map is not { } map || !ReferenceEquals(map, target.Map))
        {
            return 0;
        }

        uint healed = Math.Min(amount, target.MaxHealth - Math.Min(target.Health, target.MaxHealth));
        target.Health += healed;
        // vmangos Spell::DoAllEffectOnTarget / HostileRefManager::threatAssist:
        // baseline healing threat is half the effective gain, split over hostile references.
        // School-scoped aura modifiers and helpful-threat suppression use the shared threat seam.
        // Direct Paladin healing uses the vmangos quarter coefficient; periodic, periodic-leech and legacy healing retain half.
        // https://github.com/vmangos/core/blob/development/src/game/Spells/Spell.cpp
        // https://github.com/vmangos/core/blob/development/src/game/Threat/HostileRefManager.cpp
        Unit[] enemies = target.Combat.ThreatenedBy
            .Where(unit => unit.IsAlive && ReferenceEquals(unit.Map, map)).ToArray();
        if (healed > 0 && enemies.Length > 0 && origin != IDamageSink.HealingOrigin.NoThreat)
        {
            float coefficient = origin == IDamageSink.HealingOrigin.Direct && caster.Class == Class.Paladin
                ? 0.25f
                : 0.5f;
            float threat = healed * coefficient / enemies.Length;
            foreach (Unit enemy in enemies)
            {
                if (_spells.CalculateSpellThreat(caster, enemy, spell, threat, helpful: true) is { } scaled)
                {
                    enemy.Combat.Threat.AddThreat(caster, scaled);
                    map.Combat.Track(enemy);
                }
            }

            if (caster.Combat.ThreatenedBy.Count > 0)
            {
                map.Combat.SetInCombatState(caster, 0);
            }
        }

        return healed;
    }

    public void AssistPeriodicEnergizeThreat(Unit caster, Unit target, SpellInfo spell, uint effectiveGain, PowerType power)
    {
        if (effectiveGain == 0 || power is PowerType.Mana or PowerType.Happiness
            || caster.Map is not { } map || !ReferenceEquals(map, target.Map)
            || !caster.IsAlive || !target.IsAlive)
        {
            return;
        }

        Unit[] enemies = target.Combat.ThreatenedBy
            .Where(unit => unit is Creature { IsAlive: true } && ReferenceEquals(unit.Map, map)).ToArray();
        if (enemies.Length == 0)
        {
            return;
        }

        float threat = effectiveGain * 0.5f / enemies.Length;
        foreach (Creature enemy in enemies.OfType<Creature>())
        {
            if (_spells.CalculateSpellThreat(caster, enemy, spell, threat, helpful: true) is { } scaled)
            {
                enemy.Combat.Threat.AddThreat(caster, scaled);
                map.Combat.Track(enemy);
            }
        }
    }
}

/// <summary>Players use the shared teleport state machine; non-players only move within their map.</summary>
internal sealed class WorldSpellTeleportSink(Func<TeleportService> teleports) : ITeleportSink
{
    private readonly NearTeleportSink _near = new();

    public bool CanTeleport(Unit unit, uint mapId, float x, float y, float z, float orientation)
        => unit is Player player
            ? teleports().CanTeleportTo(player, mapId, x, y, z, orientation)
            : _near.CanTeleport(unit, mapId, x, y, z, orientation);

    public bool Teleport(Unit unit, uint mapId, float x, float y, float z, float orientation)
        => unit is Player player
            ? teleports().TeleportTo(player, mapId, x, y, z, orientation)
            : _near.Teleport(unit, mapId, x, y, z, orientation);
}

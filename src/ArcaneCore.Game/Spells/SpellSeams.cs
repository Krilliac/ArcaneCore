using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// Finds the units a spell names by GUID. The default resolves players of the reference unit's
/// map; the creatures area extends it to creatures (seam — docs/integration/spells.md).
/// </summary>
public interface ISpellUnitResolver
{
    /// <summary>The unit with <paramref name="guid"/> in <paramref name="reference"/>'s map, or null.</summary>
    Unit? Find(Unit reference, ObjectGuid guid);
}

/// <summary>
/// Where spell damage and healing land. The combat area owns the real implementation (damage
/// reduction, threat, death, combat state); the default only moves health (seam).
/// </summary>
public interface IDamageSink
{
    /// <summary>Origin of a healing operation for threat coefficients; legacy keeps the historical sink behavior.</summary>
    enum HealingOrigin
    {
        Legacy,
        Direct,
        Periodic,
        NoThreat,
        PeriodicLeech,
    }

    /// <summary>Deal <paramref name="damage"/> from a spell; returns the damage actually done.</summary>
    uint DealSpellDamage(Unit caster, Unit victim, SpellInfo spell, uint damage, bool periodic);

    /// <summary>
    /// As above, but <paramref name="startsCombat"/> false keeps the victim out of combat with the caster
    /// (a trap's hit on a player, vmangos Spell.cpp:1650). Sinks that do not track combat ignore it.
    /// </summary>
    uint DealSpellDamage(Unit caster, Unit victim, SpellInfo spell, uint damage, bool periodic, bool startsCombat)
        => DealSpellDamage(caster, victim, spell, damage, periodic);

    /// <summary>
    /// As above with <paramref name="critical"/>: whether a direct hit crit, for the threat formula (a critical hit multiplies the
    /// threat by the caster's MOD_CRITICAL_THREAT auras, vmangos ThreatCalcHelper::CalcThreat). Sinks that do not model threat ignore it.
    /// </summary>
    uint DealSpellDamage(Unit caster, Unit victim, SpellInfo spell, uint damage, bool periodic, bool startsCombat, bool critical)
        => DealSpellDamage(caster, victim, spell, damage, periodic, startsCombat);

    /// <summary>
    /// As above, carrying Unit::DealDamage's durability-loss flag. Instant kill and split damage suppress
    /// death wear (vmangos SpellEffects.cpp:285; Unit.cpp:2140,2179). Health-only sinks ignore it.
    /// </summary>
    uint DealSpellDamage(Unit caster, Unit victim, SpellInfo spell, uint damage, bool periodic, bool startsCombat, bool critical, bool durabilityLoss)
        => DealSpellDamage(caster, victim, spell, damage, periodic, startsCombat, critical);

    /// <summary>
    /// As above, carrying Unit::DealDamage's <c>reflected</c> flag: a reflected spell damaging its own caster (vmangos Unit.cpp:770-776, "Fixed bug
    /// where you could kill someone in a duel with spell reflection") is cut to 1 health in a duel. Sinks without duels ignore it.
    /// </summary>
    uint DealSpellDamage(Unit caster, Unit victim, SpellInfo spell, uint damage, bool periodic, bool startsCombat, bool critical, bool durabilityLoss, bool reflected)
        => DealSpellDamage(caster, victim, spell, damage, periodic, startsCombat, critical, durabilityLoss);

    /// <summary>Heal <paramref name="amount"/>; returns the health actually restored.</summary>
    uint Heal(Unit caster, Unit target, SpellInfo spell, uint amount);

    /// <summary>
    /// As above, saying whether the heal is periodic (vmangos gives a heal over time half the healed amount as threat for every class,
    /// a direct heal 0.5 or, for a paladin, 0.25: Spell.cpp:1362-1366, SpellAuras.cpp:6013). Sinks that do not model threat ignore it.
    /// </summary>
    uint Heal(Unit caster, Unit target, SpellInfo spell, uint amount, bool periodic) => Heal(caster, target, spell, amount);

    /// <summary>
    /// Heal with an explicit origin: <see cref="HealingOrigin.Direct"/> and <see cref="HealingOrigin.Legacy"/> are direct heals,
    /// <see cref="HealingOrigin.Periodic"/> and <see cref="HealingOrigin.PeriodicLeech"/> periodic ones (vmangos threatAssist with half
    /// the gain, SpellAuras.cpp:6013 and the PERIODIC_LEECH tick); <see cref="HealingOrigin.NoThreat"/> is vmangos DealHeal alone
    /// (SPELL_EFFECT_HEALTH_LEECH, SpellEffects.cpp:1876), which assists nobody. Sinks without a threat model only heal.
    /// </summary>
    uint Heal(Unit caster, Unit target, SpellInfo spell, uint amount, HealingOrigin origin)
        => origin is HealingOrigin.Periodic or HealingOrigin.PeriodicLeech
            ? Heal(caster, target, spell, amount, periodic: true)
            : origin == HealingOrigin.NoThreat ? Heal(caster, target, spell, amount) : Heal(caster, target, spell, amount, periodic: false);

    /// <summary>
    /// Assist hostile references for effective periodic power gain (vmangos SPELL_AURA_PERIODIC_ENERGIZE, SpellAuras.cpp:6248-6256: half the
    /// gain of a power other than mana or happiness); default sinks have no threat model.
    /// </summary>
    void AssistPeriodicEnergizeThreat(Unit caster, Unit target, SpellInfo spell, uint effectiveGain, PowerType power)
    {
    }
}

/// <summary>
/// Moves a unit for SPELL_EFFECT_TELEPORT_UNITS. The default handles teleports inside the
/// current map; far teleports belong to the teleport/map area (seam).
/// </summary>
public interface ITeleportSink
{
    /// <summary>Teleport <paramref name="unit"/>; false when the destination is not supported.</summary>
    bool Teleport(Unit unit, uint mapId, float x, float y, float z, float orientation);

    /// <summary>
    /// Whether <see cref="Teleport"/> would be accepted right now, without moving anything. Quest reward
    /// preflight asks this before a transient teleport reward may consume the quest; the answer can
    /// still change before publication, which asks again.
    /// </summary>
    bool CanTeleport(Unit unit, uint mapId, float x, float y, float z, float orientation);
}

/// <summary>A player's known spells (the spellbook). Implemented by the world daemon, which persists it.</summary>
public interface ISpellbook
{
    bool HasSpell(Player player, uint spellId);

    /// <summary>Add a spell (world thread). Returns false when it was already known.</summary>
    bool LearnSpell(Player player, uint spellId);

    /// <summary>
    /// Remove a spell (world thread; vmangos Player::RemoveSpell storage half). Returns false when it was not known
    /// or the book cannot forget spells (default: a book written before talents existed keeps its spells).
    /// </summary>
    bool ForgetSpell(Player player, uint spellId) => false;
}

/// <summary>A fixed teleport destination (cmangos-classic <c>spell_target_position</c>).</summary>
public readonly record struct SpellTargetPosition(uint MapId, float X, float Y, float Z, float Orientation);

/// <summary>Default <see cref="ISpellUnitResolver"/>: the caster itself or a player of the same map.</summary>
public sealed class MapPlayerResolver : ISpellUnitResolver
{
    public Unit? Find(Unit reference, ObjectGuid guid)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (guid.IsEmpty)
        {
            return null;
        }

        if (reference.Guid == guid)
        {
            return reference;
        }

        return reference.Map?.FindPlayer(guid);
    }
}

/// <summary>
/// Default <see cref="IDamageSink"/>: clamps health between 0 and the maximum. Reaching 0 health
/// is left to the combat area (death state, corpse, loot) — see docs/areas/spells.md.
/// </summary>
public sealed class HealthOnlyDamageSink : IDamageSink
{
    public uint DealSpellDamage(Unit caster, Unit victim, SpellInfo spell, uint damage, bool periodic)
    {
        ArgumentNullException.ThrowIfNull(victim);
        uint dealt = Math.Min(damage, victim.Health);
        victim.Health -= dealt;
        return dealt;
    }

    public uint Heal(Unit caster, Unit target, SpellInfo spell, uint amount)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!target.IsAlive)
        {
            return 0;
        }

        uint healed = Math.Min(amount, target.MaxHealth - Math.Min(target.Health, target.MaxHealth));
        target.Health += healed;
        return healed;
    }
}

/// <summary>
/// Default <see cref="ITeleportSink"/>: a near teleport within the unit's map. Players get
/// MSG_MOVE_TELEPORT_ACK (vmangos Player::TeleportTo near branch →
/// MovementPacketSender::SendTeleportToController) and are relocated at once; the client's
/// acknowledgement handler is the movement area's (docs/integration/spells.md).
/// </summary>
public sealed class NearTeleportSink : ITeleportSink
{
    public bool CanTeleport(Unit unit, uint mapId, float x, float y, float z, float orientation)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return unit.Map is { } map && map.MapId == mapId;
    }

    public bool Teleport(Unit unit, uint mapId, float x, float y, float z, float orientation)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.Map is not { } map || map.MapId != mapId)
        {
            return false;
        }

        uint now = unit.Movement.Time;
        unit.Relocate(x, y, z, orientation, now);
        if (unit is Player player)
        {
            player.NeedsVisibilityUpdate = true;
            player.Session.Send(
                Protocol.WorldOpcode.MsgMoveTeleportAck,
                SpellPackets.BuildTeleportAck(player.Guid, player.NextMovementCounter(), player.Movement));
        }

        return true;
    }
}

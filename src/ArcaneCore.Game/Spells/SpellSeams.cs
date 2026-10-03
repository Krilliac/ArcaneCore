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
    /// <summary>Deal <paramref name="damage"/> from a spell; returns the damage actually done.</summary>
    uint DealSpellDamage(Unit caster, Unit victim, SpellInfo spell, uint damage, bool periodic);

    /// <summary>
    /// As above, but <paramref name="startsCombat"/> false keeps the victim out of combat with the caster
    /// (a trap's hit on a player, vmangos Spell.cpp:1650). Sinks that do not track combat ignore it.
    /// </summary>
    uint DealSpellDamage(Unit caster, Unit victim, SpellInfo spell, uint damage, bool periodic, bool startsCombat)
        => DealSpellDamage(caster, victim, spell, damage, periodic);

    /// <summary>Heal <paramref name="amount"/>; returns the health actually restored.</summary>
    uint Heal(Unit caster, Unit target, SpellInfo spell, uint amount);
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

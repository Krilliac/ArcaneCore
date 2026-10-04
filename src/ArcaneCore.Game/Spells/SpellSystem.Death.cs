using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>
    /// A unit died (raised after <c>MapCombat.Kill</c> moved it out of the alive state). Mirrors the spell
    /// part of vmangos Unit::SetDeathState(JUST_DIED): the cast or channel in progress is interrupted
    /// first (Unit.cpp:7322-7323, InterruptNonMeleeSpells(false)), then Unit::RemoveAllAurasOnDeath
    /// (Unit.cpp:3969-4004) removes every holder that is neither passive nor death persistent
    /// (<see cref="SpellInfo.IsDeathPersistent"/>, SPELL_ATTR_EX3_ALLOW_AURA_WHILE_DEAD), DoTs, stuns,
    /// roots and buffs alike, so none of them is ticked, shown or captured by <see cref="CaptureState"/>
    /// at logout. Each aura goes through the normal removal path, so its remove handler, the visible aura
    /// slot and area-aura children are cleaned up. Cooldowns and auras the dead unit cast on others are
    /// left alone. Deliberate limits: the Hunter's Mark (SPELL_AURA_MOD_STALKED) carve-out at the top of
    /// RemoveAllAurasOnDeath has no handler yet (vmangos-only; cmangos-classic lacks it); the creature
    /// respawn aura clear (Creature.cpp:827) is not here; player side effects of dying (shapeshift
    /// removal, pet, combo points) belong to other systems; the passive test lacks vmangos'
    /// extra "Attributes == DO_NOT_DISPLAY and DurationIndex 21" case (SpellAuras.cpp:6666) because
    /// <see cref="SpellInfo"/> carries the resolved duration, not the DBC index. World thread.
    /// </summary>
    public void OnUnitDied(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (unit is Player { IsAlive: false } dying)
        {
            // Player::SetDeathState(JUST_DIED) picks the self-resurrection spell before Unit::SetDeathState strips the auras (the Soulstone is one).
            Death.Resurrection.SelfResurrection.OnPlayerDied(this, dying);
        }

        RemoveAurasOnDeath(unit);
        if (!unit.IsAlive)
        {
            RaiseUnitDied(unit); // diminishing returns reset on death (Unit.cpp:7363)
        }
    }

    private void RemoveAurasOnDeath(Unit unit)
    {
        if (unit.IsAlive || GetState(unit.Guid) is not { } state || !ReferenceEquals(state.Unit, unit))
        {
            return;
        }

        if (state.CurrentCast is { } cast)
        {
            Cancel(cast);
        }

        CancelAutoRepeat(unit); // ranged (autorepeat lane): Unit::SetDeathState(JUST_DIED) interrupts the auto-repeat spell with the rest

        foreach (SpellAuraHolder holder in state.Auras.Where(h => !h.Spell.IsPassive && !h.Spell.IsDeathPersistent).ToArray())
        {
            RemoveHolder(state, holder, AuraRemoveMode.Death);
        }
    }
}

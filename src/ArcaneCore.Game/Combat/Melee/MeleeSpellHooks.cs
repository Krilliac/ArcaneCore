using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Combat;

/// <summary>
/// What the melee swing needs from the spell system (combat sits below spells, so the world installs it through
/// <see cref="CombatEnvironment.MeleeSpells"/>; without one nothing is cast from a swing).
/// </summary>
public interface IMeleeSpellHooks
{
    /// <summary>vmangos SpellCaster::IsNonMeleeSpellCasted(false) (SpellCaster.cpp:2005-2022): a generic cast or a channel in progress.</summary>
    bool IsNonMeleeSpellCasted(Unit unit);

    /// <summary>
    /// The swing fires the unit's queued next-swing spell at <paramref name="victim"/> (vmangos Unit::AttackerStateUpdate,
    /// Unit.cpp:2249-2257). True when there was one: it is cast (or fails and is dropped) and the white swing is skipped.
    /// </summary>
    bool TryCastQueuedSwingSpell(Unit attacker, Unit victim);

    /// <summary>The unit stopped attacking: a queued next-swing spell is interrupted (vmangos Unit::AttackStop, Unit.cpp:4604).</summary>
    void OnMeleeAttackStopped(Unit attacker);
}

/// <summary>The <see cref="IMeleeSpellHooks"/> backed by the world's <see cref="SpellSystem"/>.</summary>
public sealed class SpellSystemMeleeHooks(SpellSystem spells) : IMeleeSpellHooks
{
    private readonly SpellSystem _spells = spells ?? throw new ArgumentNullException(nameof(spells));

    public bool IsNonMeleeSpellCasted(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return _spells.GetState(unit.Guid)?.CurrentCast is { State: not SpellCastState.Finished };
    }

    public bool TryCastQueuedSwingSpell(Unit attacker, Unit victim)
    {
        ArgumentNullException.ThrowIfNull(attacker);
        ArgumentNullException.ThrowIfNull(victim);
        return _spells.CastQueuedMeleeSpell(attacker, victim) != SpellCastResult.NotFound;
    }

    public void OnMeleeAttackStopped(Unit attacker)
    {
        ArgumentNullException.ThrowIfNull(attacker);
        _spells.CancelQueuedMeleeSpell(attacker);
    }
}

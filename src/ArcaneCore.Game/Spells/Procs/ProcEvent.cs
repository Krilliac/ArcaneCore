using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Procs;

/// <summary>
/// One combat event offered to the proc engine (vmangos <c>ProcSystemArguments</c>, Objects/SpellCaster.h:247-268 and SpellCaster.cpp:236-247):
/// the unit that did something (the actor the engine is called on), the unit it did it to, the proc flags of each side, what happened
/// (<see cref="Extra"/>), how much damage or healing came of it and the spell, when one caused it.
/// </summary>
public readonly record struct ProcEvent
{
    /// <summary>The unit the actor hit, missed or healed (vmangos <c>pVictim</c>); null for a cast with no unit target.</summary>
    public Unit? Victim { get; init; }

    /// <summary>The actor's side (vmangos <c>procFlagsAttacker</c>).</summary>
    public ProcFlags AttackerFlags { get; init; }

    /// <summary>The victim's side (vmangos <c>procFlagsVictim</c>).</summary>
    public ProcFlags VictimFlags { get; init; }

    /// <summary>The outcome (vmangos <c>procExtra</c>: hit, crit, miss, dodge, absorb, reflect, cast end ...).</summary>
    public ProcFlagsEx Extra { get; init; }

    /// <summary>The damage or healing that landed (vmangos <c>amount</c>; 1 for a hit that dealt nothing but must count as active).</summary>
    public uint Amount { get; init; }

    /// <summary>The amount before absorbs and resists (vmangos <c>originalAmount</c>).</summary>
    public uint OriginalAmount { get; init; }

    /// <summary>The hand or ranged slot the event used (PPM chance and the equipment requirement read it).</summary>
    public WeaponAttackType AttackType { get; init; }

    /// <summary>The spell that caused the event; null for a white swing (vmangos <c>procSpell</c>).</summary>
    public SpellInfo? ProcSpell { get; init; }

    /// <summary>The spell was cast by an aura (a proc or a periodic trigger) or is an item's triggered spell (vmangos <c>isSpellTriggeredByAuraOrItem</c>).</summary>
    public bool SpellTriggeredByAuraOrItem { get; init; }

    /// <summary>The spell is a reflected one: the damage it causes on its caster may not kill in a duel (vmangos <c>damageInfo.reflected</c>).</summary>
    public bool Reflected { get; init; }
}

/// <summary>The trigger check of one aura holder (vmangos <c>SpellProcEventTriggerCheck</c>).</summary>
public enum ProcTriggerCheck
{
    /// <summary>The aura procs (SPELL_PROC_TRIGGER_OK).</summary>
    Ok,

    /// <summary>The event cannot proc the aura (SPELL_PROC_TRIGGER_FAILED).</summary>
    Failed,

    /// <summary>The event qualified but the chance roll failed (SPELL_PROC_TRIGGER_ROLL_FAILED): a proc-cooldown-on-failure aura still starts its cooldown.</summary>
    RollFailed,
}

/// <summary>What an aura's proc handler did (vmangos <c>SpellAuraProcResult</c>).</summary>
public enum AuraProcResult
{
    /// <summary>It procced: a charge is spent unless the spell burns charges only on failure (SPELL_AURA_PROC_OK).</summary>
    Ok,

    /// <summary>It did nothing: no charge is spent unless the spell burns charges on failure (SPELL_AURA_PROC_FAILED).</summary>
    Failed,

    /// <summary>This aura type never procs: the effect is skipped entirely (SPELL_AURA_PROC_CANT_TRIGGER).</summary>
    CantTrigger,
}

using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Procs;

namespace ArcaneCore.Game.Spells.Procs;

/// <summary>
/// One aura effect that procs (the arguments of a vmangos <c>AuraProcHandler[]</c> member, UnitAuraProcHandler.cpp:37-231, called from
/// <c>Unit::HandleTriggers</c>, Unit.cpp:4245-4345).
/// </summary>
/// <param name="System">The spell system.</param>
/// <param name="Owner">The unit that carries the aura (vmangos calls the handler ON this unit).</param>
/// <param name="Target">The other side of the event (vmangos <c>pVictim</c>): the victim for an attacker-side proc, the attacker for a victim-side one.</param>
/// <param name="Holder">The proccing aura holder.</param>
/// <param name="Aura">The proccing effect of the holder.</param>
/// <param name="ProcSpell">The spell that caused the event, or null for a white swing.</param>
/// <param name="ProcFlag">The owner side's proc flags of the event.</param>
/// <param name="ProcExtra">The outcome of the event.</param>
/// <param name="Amount">The damage or healing that landed.</param>
/// <param name="OriginalAmount">The amount before absorbs and resists.</param>
/// <param name="CooldownMs">The spell_proc_event hidden cooldown to put on a triggered spell (0 = none).</param>
/// <param name="IsVictim">Whether the owner is the victim of the event.</param>
/// <param name="AttackType">The hand or ranged slot of the event.</param>
/// <param name="Reflected">The event is a reflected spell hitting its own caster.</param>
public readonly record struct AuraProcContext(
    SpellSystem System,
    Unit Owner,
    Unit? Target,
    SpellAuraHolder Holder,
    SpellAura Aura,
    SpellInfo? ProcSpell,
    ProcFlags ProcFlag,
    ProcFlagsEx ProcExtra,
    uint Amount,
    uint OriginalAmount,
    uint CooldownMs,
    bool IsVictim,
    WeaponAttackType AttackType,
    bool Reflected = false);

/// <summary>A proc handler for one aura type (vmangos <c>pAuraProcHandler</c>). Registered with <see cref="SpellSystem.RegisterProcHandler"/>.</summary>
public delegate AuraProcResult AuraProcHandler(in AuraProcContext context);

/// <summary>What <see cref="IProcScript.CheckProc"/> is asked about (the arguments of vmangos <c>AuraScript::OnCheckProc</c>).</summary>
/// <param name="System">The spell system.</param>
/// <param name="Owner">The unit that carries the aura.</param>
/// <param name="Target">The other side of the event.</param>
/// <param name="Holder">The aura holder being checked.</param>
/// <param name="ProcSpell">The spell that caused the event, or null for a white swing.</param>
/// <param name="ProcFlag">The owner side's proc flags of the event.</param>
/// <param name="ProcExtra">The outcome of the event.</param>
/// <param name="AttackType">The hand or ranged slot of the event.</param>
/// <param name="IsVictim">Whether the owner is the victim of the event.</param>
public readonly record struct ProcCheckContext(
    SpellSystem System,
    Unit Owner,
    Unit? Target,
    SpellAuraHolder Holder,
    SpellInfo? ProcSpell,
    ProcFlags ProcFlag,
    ProcFlagsEx ProcExtra,
    WeaponAttackType AttackType,
    bool IsVictim);

/// <summary>
/// The per-spell proc seam (vmangos <c>AuraScript::OnCheckProc</c> and <c>AuraScript::OnProc</c>, consulted by
/// <c>Unit::IsTriggeredAtSpellProcEvent</c>, UnitAuraProcHandler.cpp:387-389, and <c>Unit::HandleTriggers</c>, Unit.cpp:4308-4312).
/// Class scripts (Sweeping Strikes, seals, Judgement of Light, Lightning Shield ...) register one per aura spell with
/// <see cref="SpellSystem.RegisterProcScript"/>. Returning null defers to the generic engine.
/// </summary>
public interface IProcScript
{
    /// <summary>Decide the trigger check yourself, or null to run the generic one (spell_proc_event, flags, chance).</summary>
    ProcTriggerCheck? CheckProc(in ProcCheckContext context) => null;

    /// <summary>Handle the proc of one effect yourself, or null to run the aura type's handler.</summary>
    AuraProcResult? OnProc(in AuraProcContext context) => null;
}

/// <summary>The proc conditions of the world (vmangos <c>SpellMgr::GetSpellProcEvent</c>).</summary>
public interface ISpellProcEventCatalog
{
    /// <summary>The spell_proc_event row of <paramref name="spellId"/> (after the rank fill), or null.</summary>
    SpellProcEventRecord? Find(uint spellId);
}

/// <summary>An empty catalog: every aura runs on its Spell.dbc procFlags and procChance.</summary>
public sealed class EmptySpellProcEventCatalog : ISpellProcEventCatalog
{
    public static EmptySpellProcEventCatalog Instance { get; } = new();

    public SpellProcEventRecord? Find(uint spellId) => null;
}

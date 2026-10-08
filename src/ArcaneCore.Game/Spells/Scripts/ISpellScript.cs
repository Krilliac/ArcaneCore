namespace ArcaneCore.Game.Spells.Scripts;

/// <summary>
/// Per-spell class logic, the counterpart of the vmangos <c>SpellScript</c> objects (src/scripts/spells/spell_warlock.cpp,
/// spell_mage.cpp) that are keyed by spell id. A script is a class with a parameterless constructor that carries a
/// <see cref="SpellScriptAttribute"/> naming the spell ids it serves; <see cref="SpellScriptRegistry"/> finds it by reflection and
/// <see cref="SpellScriptDispatcher"/> routes the hooks. All members have empty default bodies: implement what the spell needs.
/// <para>
/// Hook order inside one cast follows vmangos: <see cref="OnCheckCast"/> runs after every other check (Spell.cpp:6480-6481), then
/// power and ammo are taken, then <see cref="OnCast"/> (Spell.cpp:3716-3724, TakePower, TakeReagents, then m_spellScript->OnCast),
/// then <see cref="OnEffectExecute"/> once per DUMMY, SCRIPT_EFFECT or declared (<see cref="SpellScriptAttribute.ExecuteEffects"/>) effect (Spell.cpp:5254-5257 calls it before every effect; the
/// other effects have no script hook), and <see cref="OnSuccessfulDispel"/> after a DISPEL effect that removed at least
/// one aura.
/// </para>
/// <see cref="OnSummon"/> follows a creature summon of the spell: only SPELL_EFFECT_SUMMON_WILD raises it here (vmangos also calls it from the
/// pet, guardian, totem, critter and game object summons, SpellEffects.cpp:2432-5818, which no script of this code base needs yet).
/// </summary>
public interface ISpellScript
{
    /// <summary>
    /// Final veto of the cast (vmangos Spell::CheckCast ends with <c>m_spellScript->OnCheckCast</c>). Runs for triggered casts too, and
    /// at cast start and again when the cast lands (<see cref="SpellCastCheckContext.Strict"/>).
    /// </summary>
    SpellCastResult OnCheckCast(in SpellCastCheckContext context) => SpellCastResult.CastOk;

    /// <summary>The cast went through: power taken, before targets and effects.</summary>
    void OnCast(SpellCast cast)
    {
    }

    /// <summary>A DUMMY, SCRIPT_EFFECT or declared effect of the spell is about to run on a target (vmangos OnEffectExecute).</summary>
    void OnEffectExecute(SpellEffectContext context)
    {
    }

    /// <summary>A DISPEL effect of the spell removed <paramref name="removedStacks"/> aura stacks (at least one) from its target.</summary>
    void OnSuccessfulDispel(SpellEffectContext context, int removedStacks)
    {
    }

    /// <summary>
    /// A creature summon of the spell entered the world (vmangos SpellScript::OnSummon, called by Spell::EffectSummonWild after the summon is
    /// placed and marked, SpellEffects.cpp:2765-2770).
    /// </summary>
    void OnSummon(SpellEffectContext context, Creatures.Creature summon)
    {
    }
}

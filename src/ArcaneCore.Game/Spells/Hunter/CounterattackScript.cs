using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Spells.Hunter;

/// <summary>
/// Counterattack (19306, 20909, 20910; vmangos scripts/spells/spell_hunter.cpp:121-136, <c>HunterCounterAttackScript::OnCheckCast</c>). The spell data
/// already needs the caster aura state HUNTER_PARRY (CasterAuraState 7), which a hunter's parry opens for four seconds (Unit::ProcSkillsAndReactives).
/// The script adds the target rule: the cast must be at the unit whose attack was parried, vmangos <c>GetReactiveTarget(REACTIVE_HUNTER_PARRY)</c>;
/// any other target is BAD_TARGETS.
/// <para>
/// The parried attacker is read from the hunter's combo target: the same parry that opens the window gives the hunter one combo point on that attacker
/// (vmangos <c>AddComboPoints(pTarget, 1)</c> beside <c>StartReactiveTimer(REACTIVE_HUNTER_PARRY, pTarget)</c>, Unit.cpp ProcSkillsAndReactives), and
/// a later parry moves both to the new attacker. The reactive service lives in the combat area and is not reachable from a script.
/// </para>
/// </summary>
[SpellScript(19306, 20909, 20910)]
public sealed class CounterattackScript : ISpellScript
{
    public static readonly uint[] Ranks = [19306, 20909, 20910];

    public SpellCastResult OnCheckCast(in SpellCastCheckContext context)
    {
        if (context.Caster is not Player hunter)
        {
            return SpellCastResult.CastOk; // vmangos asks m_casterUnit; only a player keeps the marker here
        }

        ObjectGuid parried = new(hunter.GetUInt64(UpdateFields.PlayerFieldComboTarget));
        return context.Target is { } target && !parried.IsEmpty && target.Guid == parried
            ? SpellCastResult.CastOk
            : SpellCastResult.BadTargets;
    }
}

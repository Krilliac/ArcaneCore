using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Battlegrounds;

/// <summary>
/// vmangos AVCreateShredderScript (scripts/battlegrounds/battleground_alterac.cpp:4877-4891), the script of Create Shredder 21544 (Horde) and
/// 21565 (Alliance): the second effect of the spell (TRIGGER_SPELL Control Shredder 21556 / 21566, the possession) cannot target the shredder,
/// which does not exist yet when the targets are selected, so the summon is marked as created by that triggered spell and gets the caster as
/// its creator (UNIT_CREATED_BY_SPELL, UNIT_FIELD_CREATEDBY). Raised by the wild summon (<see cref="ISpellScript.OnSummon"/>).
/// </summary>
[SpellScript(AlteracValley.SpellSummonShredderHorde, AlteracValley.SpellSummonShredderAlliance)]
public sealed class AlteracValleyCreateShredderScript : ISpellScript
{
    public void OnSummon(SpellEffectContext context, Creature summon)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(summon);
        IReadOnlyList<SpellEffectInfo> effects = context.Spell.Effects;
        uint control = effects.Count > 1 ? effects[1].TriggerSpell : 0; // spell->m_spellInfo->EffectTriggerSpell[1]
        summon.SetUInt32(UpdateFields.UnitCreatedBySpell, control);
        summon.SetCreatorGuid(context.Caster.Guid);
    }
}

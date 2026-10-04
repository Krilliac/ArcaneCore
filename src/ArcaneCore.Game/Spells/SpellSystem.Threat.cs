using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Combat.Threat;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// What a landed spell does to threat and combat beyond its own damage and healing (vmangos Spell::DoAllEffectOnTarget,
/// Spells/Spell.cpp:1649-1725, and Spell::HandleThreatSpells, :5172-5230). Called for every target whose effects were applied.
/// </summary>
public sealed partial class SpellSystem
{
    /// <summary>vmangos SPELL_ATTR_EX2_NO_INITIAL_THREAT (SpellDefines.h:882): the hit starts no combat and creates no assist reference.</summary>
    private const uint AttributeEx2NoInitialThreat = 0x00400000;

    /// <summary>
    /// A spell that landed on <paramref name="target"/>:
    /// <list type="number">
    /// <item>A hostile hit that did no damage (a debuff, a crowd control, a dispel, a taunt) still puts both sides in combat and gives the
    /// target's AI its AttackedBy call with a zero-threat entry (:1649-1679). Damage already does all of that through the combat
    /// area, so only the harmless hits are routed here. Not for a triggered cast, a spell with EX_NO_THREAT or NO_INITIAL_THREAT
    /// (:1655-1658), or a spell that takes the target over (MOD_POSSESS, :1671).</item>
    /// <item>A positive spell on a target that is in combat puts the caster in combat and gives the caster a zero-threat entry on every
    /// list that holds the target (:1713-1720), unless the spell has EX_NO_THREAT, NO_INITIAL_THREAT or was triggered.</item>
    /// <item>The spell's flat spell_threat (<see cref="MapCombat.SpellThreatCatalog"/>): added once per target that was hit, to the target's
    /// list for a harmful spell, spread over the target's enemies like healing for a positive one (:5172-5230).</item>
    /// </list>
    /// Limits: whether the target can see the caster (stealth and invisibility), the Pickpocket back-attack and the "spell is partly
    /// positive" refusal of the flat threat need data the spell system does not keep per effect; a triggered cast stands in for
    /// "triggered by an aura".
    /// </summary>
    private void ApplySpellThreat(SpellCast cast, Unit target, SpellTargetOutcome outcome)
    {
        Unit caster = cast.Caster;
        SpellInfo spell = cast.Spell;
        if (caster.Map is not { } map || !ReferenceEquals(map, target.Map) || !target.IsAlive || IsQuestSettlementPending(caster) || IsQuestSettlementPending(target))
        {
            return;
        }

        MapCombat combat = map.Combat;
        bool noInitialThreat = ((uint)spell.AttributesEx & MapCombat.AttributeExNoThreat) != 0 || ((uint)spell.AttributesEx2 & AttributeEx2NoInitialThreat) != 0;
        if (!ReferenceEquals(caster, target))
        {
            if (!spell.IsPositive || spell.HasEffect(SpellEffectName.Dispel))
            {
                if (outcome.Damage == 0 && outcome.Healing == 0 && !cast.IsTriggered && !noInitialThreat
                    && !spell.Effects.Any(static e => e.AuraType == AuraType.ModPossess) && Relations.IsHostile(caster, target))
                {
                    Damage.DealSpellDamage(caster, target, spell, 0, periodic: false, startsCombat: StartsCombat(caster, target));
                }
            }
            else if (target.Combat.IsInCombat && !cast.IsTriggered && !noInitialThreat)
            {
                combat.SetInCombatState(caster, target.Combat.CombatTimer > 0 ? CombatConstants.PvpCombatTimerMs : 0);
                foreach (Unit holder in HostileRefs.ThreatAssist(target, caster, 0f, spell, null))
                {
                    combat.Track(holder);
                }
            }
        }

        if (combat.SpellThreatCatalog?.Find(spell.Id) is { Threat: not 0 } entry && entry.CanCauseThreatOnMask(outcome.EffectMask))
        {
            if (spell.IsPositive)
            {
                foreach (Unit holder in HostileRefs.ThreatAssist(target, caster, entry.Threat, spell, combat.ThreatModifiers))
                {
                    combat.Track(holder);
                }
            }
            else if (ThreatRules.CanHaveThreatList(target))
            {
                SpellThreat.Add(this, caster, target, spell, entry.Threat);
            }
        }
    }
}

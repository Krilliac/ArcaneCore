using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Combat.Threat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// What a landed spell does to threat and combat beyond its own damage and healing (vmangos Spell::DoAllEffectOnTarget,
/// Spells/Spell.cpp:1649-1731, and Spell::HandleThreatSpells, :5172-5230). Called for every target whose effects were applied.
/// </summary>
public sealed partial class SpellSystem
{
    /// <summary>
    /// Explicit spell threat (vmangos Unit::AddThreat with the spell, as SPELL_EFFECT_THREAT does): the threat formula once (SPELLMOD_THREAT, then the
    /// caster's MOD_THREAT multiplier of the spell's school), NO_HARMFUL_THREAT adds nothing and EX_NO_THREAT only raises an existing entry.
    /// </summary>
    public void AddSpellThreat(Unit caster, Unit victim, SpellInfo spell, float threat)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(victim);
        ArgumentNullException.ThrowIfNull(spell);
        SpellThreat.Add(this, caster, victim, spell, threat);
    }

    /// <summary>vmangos SPELL_ATTR_EX2_NO_INITIAL_THREAT (SpellDefines.h:882): the hit starts no combat and creates no assist reference.</summary>
    private const uint AttributeEx2NoInitialThreat = 0x00400000;

    /// <summary>
    /// vmangos Unit::SetInCombatWithAssisted (Unit.cpp:6000-6037): puts <paramref name="caster"/> in combat with the PvP combat timer when
    /// <paramref name="assisted"/> has one, and propagates the assisted player's PvP and contested-PvP flags to the assisting player (or pet owner).
    /// </summary>
    public static void SetInCombatWithAssisted(MapCombat combat, Unit caster, Unit assisted, bool touchOnly = false)
    {
        ArgumentNullException.ThrowIfNull(combat);
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(assisted);

        uint timer = assisted.Combat.CombatTimer > 0 ? CombatConstants.PvpCombatTimerMs : 0;
        if (!touchOnly)
        {
            combat.SetInCombatState(caster, timer);
        }
        else if (timer > caster.Combat.CombatTimer)
        {
            caster.Combat.CombatTimer = timer;
        }

        if (caster.GetCharmerOrOwnerPlayerOrSelf() is { } selfPlayer
            && assisted.GetCharmerOrOwnerPlayerOrSelf() is { } otherPlayer
            && !ReferenceEquals(selfPlayer, otherPlayer))
        {
            if ((selfPlayer.UnitFlags & UnitFlags.Pvp) == 0 && (otherPlayer.UnitFlags & UnitFlags.Pvp) != 0)
            {
                MapCombat.UpdatePvp(selfPlayer, true);
            }

            if ((selfPlayer.Flags & PlayerFlags.ContestedPvp) == 0 && (otherPlayer.Flags & PlayerFlags.ContestedPvp) != 0)
            {
                MapCombat.UpdatePvpContested(selfPlayer, true);
            }
        }
    }

    /// <summary>
    /// A spell that landed on <paramref name="target"/>:
    /// <list type="number">
    /// <item>A hostile hit that did no damage (a debuff, a crowd control, a dispel, a taunt) still puts both sides in combat and gives the
    /// target's AI its AttackedBy call with a zero-threat entry (:1649-1679). Damage already does all of that through the combat
    /// area, so only the harmless hits are routed here. Not for a triggered cast, a spell with EX_NO_THREAT or NO_INITIAL_THREAT
    /// (:1655-1658), or a spell that takes the target over (MOD_POSSESS, :1671).</item>
    /// <item>A positive or friendly-target spell on a target that is in combat puts the caster in combat via <see cref="SetInCombatWithAssisted"/> and gives
    /// the caster a zero-threat entry on every list that holds the target (:1713-1720), unless the spell has EX_NO_THREAT, NO_INITIAL_THREAT or was
    /// triggered; when the target is not in combat, assisting a PvP-flagged player (or pet) still flags the assisting player (:1722-1730).</item>
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
            if ((!spell.IsPositive || spell.HasEffect(SpellEffectName.Dispel)) && Relations.IsHostile(caster, target))
            {
                if (outcome.Damage == 0 && outcome.Healing == 0 && !cast.IsTriggered && !noInitialThreat
                    && !spell.Effects.Any(static e => e.AuraType == AuraType.ModPossess))
                {
                    Damage.DealSpellDamage(caster, target, spell, 0, periodic: false, startsCombat: StartsCombat(caster, target));
                }
            }
            else if (spell.IsPositive || spell.IsFriendlyTarget())
            {
                if (target.Combat.IsInCombat)
                {
                    if (!cast.IsTriggered && !noInitialThreat)
                    {
                        SetInCombatWithAssisted(combat, caster, target);
                        foreach (Unit holder in HostileRefs.ThreatAssist(target, caster, 0f, spell, null))
                        {
                            combat.Track(holder);
                        }
                    }
                }
                else if (caster.GetCharmerOrOwnerPlayerOrSelf() is { } selfPlayer
                    && target.GetCharmerOrOwnerPlayerOrSelf() is { } otherPlayer
                    && !ReferenceEquals(selfPlayer, otherPlayer)
                    && (selfPlayer.UnitFlags & UnitFlags.Pvp) == 0
                    && (otherPlayer.UnitFlags & UnitFlags.Pvp) != 0)
                {
                    MapCombat.UpdatePvp(selfPlayer, true);
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

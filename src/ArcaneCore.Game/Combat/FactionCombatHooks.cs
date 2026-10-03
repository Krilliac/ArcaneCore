using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Npc;

namespace ArcaneCore.Game.Combat;

/// <summary>
/// Production <see cref="CombatHooks"/>: a template-only SUBSET of vmangos
/// <c>WorldObject::IsValidAttackTarget</c> (D:\refs\vmangos\src\game\Objects\Object.cpp:3745-3815). It is not
/// "vmangos CanAttack"; it reads only the FactionTemplate.dbc rows of the <see cref="FactionTemplateCatalog"/>
/// the creature and quest areas already load.
/// <para>
/// Template reaction of <c>a</c> towards <c>b</c> (<c>GetFactionReactionTo</c>, Object.cpp:3734-3741, record logic
/// DBCStructure.h:362-388): hostile if <c>a.IsHostileTo(b)</c> (checked first, so a template with both hostile and
/// friendly bits is hostile); else friendly if <c>a.IsFriendlyTo(b)</c> or <c>b.IsFriendlyTo(a)</c>; else neutral.
/// A template missing from the catalog (including 0) is neutral (Object.cpp:3705-3709).
/// </para>
/// <para>
/// Pairs: (1) neither unit has <see cref="UnitFlags.PlayerControlled"/> (the vmangos UNIT_FLAG_PLAYER_CONTROLLED,
/// UnitDefines.h:494): attackable only if the reaction is hostile in EITHER direction (Object.cpp:3760-3763), so
/// neutral creature pairs are not attackable. (2) Otherwise (player, pet, charm or totem involved) the attack is
/// refused if the reaction is friendly in EITHER direction (Object.cpp:3767-3769); neutral stays attackable.
/// (3) Player versus player is left to the base rule (team friendliness, PvP flag).
/// </para>
/// <para>
/// Not modelled, so this is NOT equivalent to vmangos: player reputation / at-war state (Faction.dbc
/// reputationListID, <c>CanHaveReputation</c>, FACTION_FLAG_AT_WAR making a faction HOSTILE, Object.cpp:3714-3731 and
/// 3677-3693); neutral-versus-neutral being attackable only when the faction is at war for reputation-capable
/// factions (Object.cpp:3775-3792); the contested-guard rule (IsContestedGuardFaction plus PLAYER_FLAGS_CONTESTED_PVP
/// => HOSTILE, Object.cpp:3714-3716); GM players reading NEUTRAL (Object.cpp:3625-3626, 3633-3634) and forced
/// reactions (GetForcedRankIfAny); the ordering with same-group / FFA reactions (Object.cpp:3654-3664) and
/// the FFA parts of the PvP block (Object.cpp:3805-3815); the duel reaction and the duel PvP exemption ARE modelled, in the
/// base <see cref="CombatHooks"/> (Object.cpp:3650-3652, 3797-3800; see <see cref="DuelRules"/>); resolving the affecting player of a pet or charm (no owner field exists).
/// <see cref="CombatHooks.IsFriendly"/> is not overridden (vmangos IsFriendlyTo / IsValidHelpfulTarget polarity for
/// friendly-NPC spells, heals and dispels is unchanged): spell targeting and other consumers keep the base rule.
/// A world with no loaded catalog does not register these hooks at all (see WorldCombatHooksFeature), so the
/// permissive <see cref="CombatHooks.Default"/> applies there.
/// </para>
/// </summary>
public sealed class FactionCombatHooks(FactionTemplateCatalog factions) : CombatHooks
{
    private enum Reaction { Hostile, Neutral, Friendly }

    public FactionTemplateCatalog Factions { get; } = factions ?? throw new ArgumentNullException(nameof(factions));

    public override bool CanAttack(Unit attacker, Unit victim)
    {
        if (!base.CanAttack(attacker, victim))
        {
            return false;
        }

        if (attacker is Player && victim is Player)
        {
            return true;
        }

        Reaction forward = ReactionTo(attacker, victim);
        Reaction backward = ReactionTo(victim, attacker);
        if (((attacker.UnitFlags | victim.UnitFlags) & UnitFlags.PlayerControlled) == 0)
        {
            // Object.cpp:3760-3763
            return forward == Reaction.Hostile || backward == Reaction.Hostile;
        }

        // Object.cpp:3767-3769
        return forward != Reaction.Friendly && backward != Reaction.Friendly;
    }

    // WorldObject::GetFactionReactionTo, template part only (Object.cpp:3701-3741).
    private Reaction ReactionTo(Unit from, Unit to)
    {
        if (Factions.Find(from.FactionTemplate) is not { } a || Factions.Find(to.FactionTemplate) is not { } b)
        {
            return Reaction.Neutral;
        }

        if (a.IsHostileTo(b))
        {
            return Reaction.Hostile;
        }

        return a.IsFriendlyTo(b) || b.IsFriendlyTo(a) ? Reaction.Friendly : Reaction.Neutral;
    }
}

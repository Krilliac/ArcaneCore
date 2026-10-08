using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Reputation;

namespace ArcaneCore.Game.Reputation;

/// <summary>
/// The full unit-to-unit reaction ladder of vmangos <c>WorldObject::GetReactionTo</c> and
/// <c>GetFactionReactionTo</c> (D:\refs\vmangos\src\game\Objects\Object.cpp:3608-3743), re-implemented with the
/// reputation state, forced reactions, duels and group membership this codebase has. Order: self; owner or charmer
/// (same controlling player); GM neutral; forced rank; for player-controlled units the duel, same-raid and reputation
/// (at war) rules; then the faction rule (contested guards, forced rank, standing capped at Neutral while at war, and
/// finally the template relations).
/// <para>
/// Fails closed: <c>false</c> means "cannot resolve" (a reputation faction whose player state is not loaded), and the
/// callers fall back to the template-only hooks instead of guessing. An unknown template or a faction missing from
/// Faction.dbc reads as the vmangos answer (Neutral, or the template relations), not as a failure.
/// </para>
/// Not modelled: free-for-all PvP (<c>IsFFAPvP</c>; no such state exists here). World thread.
/// </summary>
public sealed class ReputationReactionResolver(
    FactionTemplateCatalog templates,
    FactionCatalog factions,
    Func<Player, PlayerReputation?> reputationOf,
    Func<Player, Player, bool>? sameRaid = null)
{
    private readonly FactionTemplateCatalog _templates = templates ?? throw new ArgumentNullException(nameof(templates));
    private readonly FactionCatalog _factions = factions ?? throw new ArgumentNullException(nameof(factions));
    private readonly Func<Player, PlayerReputation?> _reputationOf = reputationOf ?? throw new ArgumentNullException(nameof(reputationOf));

    public FactionTemplateCatalog Templates => _templates;

    public FactionCatalog Factions => _factions;

    /// <summary>The reputation state of <paramref name="player"/>, or null while it is not loaded (game objects judge players with it).</summary>
    public PlayerReputation? ReputationOf(Player player) => _reputationOf(player);

    /// <summary>The unit's faction template row, or null (unit template 0 or not in the catalog: Neutral for every rule).</summary>
    public FactionTemplateRecord? TemplateOf(Unit unit) => _templates.Find(unit.FactionTemplate);

    /// <summary>WorldObject::GetReactionTo(target): the reaction of <paramref name="self"/> towards <paramref name="target"/>.</summary>
    public bool TryGetReaction(Unit self, Unit target, out ReputationRank rank)
    {
        ArgumentNullException.ThrowIfNull(self);
        ArgumentNullException.ThrowIfNull(target);
        rank = ReputationRank.Neutral;
        if (ReferenceEquals(self, target))
        {
            rank = ReputationRank.Friendly;
            return true;
        }

        Player? selfOwner = DuelRules.ControllingPlayer(self);
        Player? targetOwner = DuelRules.ControllingPlayer(target);

        // Always friendly to a charmer, an owner or a unit they control (GetCharmerOrOwnerOrOwnGuid).
        if (selfOwner is not null && ReferenceEquals(selfOwner, targetOwner))
        {
            rank = ReputationRank.Friendly;
            return true;
        }

        FactionTemplateRecord? selfTemplate = TemplateOf(self);
        FactionTemplateRecord? targetTemplate = TemplateOf(target);

        // Forced reputation (SPELL_AURA_FORCE_REACTION), after the GM rule (Object.cpp:3622-3640).
        if (selfOwner is not null)
        {
            if (selfOwner.IsGameMaster)
            {
                return true;
            }

            if (targetTemplate is not null && Reputation(selfOwner)?.TryGetForcedRank(targetTemplate.Faction, out rank) == true)
            {
                return true;
            }
        }
        else if (targetOwner is not null)
        {
            if (targetOwner.IsGameMaster)
            {
                return true;
            }

            if (selfTemplate is not null && Reputation(targetOwner)?.TryGetForcedRank(selfTemplate.Faction, out rank) == true)
            {
                return true;
            }
        }

        rank = ReputationRank.Neutral;
        if ((self.UnitFlags & UnitFlags.PlayerControlled) != 0)
        {
            if ((target.UnitFlags & UnitFlags.PlayerControlled) != 0 && selfOwner is not null && targetOwner is not null)
            {
                // Duel: always hostile to the opponent (Object.cpp:3650-3652).
                if (DuelRules.IsOpponentHostile(self, target))
                {
                    rank = ReputationRank.Hostile;
                    return true;
                }

                // Same group or raid: friendly (Object.cpp:3654-3657).
                if (sameRaid?.Invoke(selfOwner, targetOwner) == true)
                {
                    rank = ReputationRank.Friendly;
                    return true;
                }
            }

            if (selfOwner is not null && targetTemplate is not null
                && _factions.Find(targetTemplate.Faction) is { CanHaveReputation: true } targetFaction)
            {
                // Contested guards against a contested player (Object.cpp:3677-3680).
                if (targetTemplate.IsContestedGuard && (selfOwner.Flags & PlayerFlags.ContestedPvp) != 0)
                {
                    rank = ReputationRank.Hostile;
                    return true;
                }

                // A faction with a reputation list depends only on the at-war state (Object.cpp:3682-3691).
                if (Reputation(selfOwner) is not { } selfReputation)
                {
                    return false;
                }

                rank = selfReputation.State(targetFaction) is { IsAtWar: true } ? ReputationRank.Hostile : ReputationRank.Friendly;
                return true;
            }
        }

        return TryGetFactionReaction(selfTemplate, target, targetTemplate, targetOwner, out rank);
    }

    /// <summary>WorldObject::GetFactionReactionTo(template, target) (Object.cpp:3701-3743), CvP standing included.</summary>
    private bool TryGetFactionReaction(FactionTemplateRecord? selfTemplate, Unit target, FactionTemplateRecord? targetTemplate,
        Player? targetOwner, out ReputationRank rank)
    {
        rank = ReputationRank.Neutral;
        if (selfTemplate is null || targetTemplate is null)
        {
            return true; // always neutral when a template is missing (Object.cpp:3705-3709)
        }

        if (targetOwner is not null)
        {
            if (selfTemplate.IsContestedGuard && (targetOwner.Flags & PlayerFlags.ContestedPvp) != 0)
            {
                rank = ReputationRank.Hostile;
                return true;
            }

            PlayerReputation? reputation = Reputation(targetOwner);
            if (reputation is not null && reputation.TryGetForcedRank(selfTemplate.Faction, out ReputationRank forced))
            {
                rank = forced;
                return true;
            }

            if (_factions.Find(selfTemplate.Faction) is { CanHaveReputation: true } faction)
            {
                if (reputation is null)
                {
                    return false;
                }

                // CvP: the standing, but never above Neutral while at war (Object.cpp:3722-3729).
                rank = reputation.Rank(faction);
                if (reputation.State(faction) is { IsAtWar: true } && rank > ReputationRank.Neutral)
                {
                    rank = ReputationRank.Neutral;
                }

                return true;
            }
        }

        rank = ReputationRank.Neutral;
        if (selfTemplate.IsHostileTo(targetTemplate))
        {
            rank = ReputationRank.Hostile;
        }
        else if (selfTemplate.IsFriendlyTo(targetTemplate) || targetTemplate.IsFriendlyTo(selfTemplate))
        {
            rank = ReputationRank.Friendly;
        }

        return true;
    }

    /// <summary>
    /// WorldObject::IsValidAttackTarget from the faction rules on (Object.cpp:3760-3792), for two units that already passed
    /// the targetability checks. False when it cannot be resolved.
    /// </summary>
    public bool TryCanAttack(Unit attacker, Unit victim, out bool canAttack)
    {
        ArgumentNullException.ThrowIfNull(attacker);
        ArgumentNullException.ThrowIfNull(victim);
        canAttack = false;
        if (!TryGetReaction(attacker, victim, out ReputationRank forward) || !TryGetReaction(victim, attacker, out ReputationRank backward))
        {
            return false;
        }

        // CvC: attackable only when one of them is hostile (Object.cpp:3760-3763).
        if (((attacker.UnitFlags | victim.UnitFlags) & UnitFlags.PlayerControlled) == 0)
        {
            canAttack = forward <= ReputationRank.Hostile || backward <= ReputationRank.Hostile;
            return true;
        }

        // PvP, PvC, CvP: friendly in either direction denies (Object.cpp:3767-3769).
        if (forward > ReputationRank.Neutral || backward > ReputationRank.Neutral)
        {
            return true;
        }

        // Not all neutral creatures can be attacked (Object.cpp:3775-3792): between a player side and a creature, two
        // Neutral reactions need the faction to be at war, unless a forced rank decides.
        Player? attackerOwner = DuelRules.ControllingPlayer(attacker);
        Player? victimOwner = DuelRules.ControllingPlayer(victim);
        if (forward == ReputationRank.Neutral && backward == ReputationRank.Neutral && (attackerOwner is null) != (victimOwner is null))
        {
            Player player = victimOwner ?? attackerOwner!;
            Unit other = victimOwner is not null ? attacker : victim;
            if (TemplateOf(other) is { } template && Reputation(player) is { } reputation
                && !reputation.TryGetForcedRank(template.Faction, out _)
                && _factions.Find(template.Faction) is { } factionEntry
                && reputation.State(factionEntry) is { IsAtWar: false })
            {
                return true;
            }
        }

        canAttack = true;
        return true;
    }

    private PlayerReputation? Reputation(Player player) => _reputationOf(player);
}

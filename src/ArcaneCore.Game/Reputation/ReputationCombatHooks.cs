using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Reputation;

/// <summary>
/// Production <see cref="CombatHooks"/> with player reputation, composed over the template-only hooks
/// (<see cref="FactionCombatHooks"/>, which stays untouched): <see cref="CanAttack"/> and <see cref="IsHostileTo"/> follow
/// the vmangos reaction ladder (<see cref="ReputationReactionResolver"/>, Object.cpp:3608-3816), so a Hated player is
/// attackable by his former friends, a reputation faction is not attackable unless at war, contested guards attack only
/// contested players, forced reactions and GM neutrality apply. Anything the resolver cannot answer (a reputation faction
/// whose player state is not loaded yet) and player-versus-player pairs go to the wrapped hooks, so the behaviour of the
/// template-only world is never made more permissive by a missing piece of data.
/// <see cref="IsFriendly"/> follows the same ladder for the pairs the resolver answers (vmangos WorldObject::IsFriendlyTo,
/// GetReactionTo &gt;= REP_FRIENDLY), so the wrapped hooks' template-only friendliness (a guard is friendly to the player race)
/// never vetoes an attack the reputation allows; player-versus-player pairs and unanswered pairs keep the wrapped rule.
/// </summary>
public sealed class ReputationCombatHooks(CombatHooks fallback, ReputationReactionResolver resolver) : CombatHooks
{
    private readonly CombatHooks _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
    private readonly ReputationReactionResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public CombatHooks Fallback => _fallback;

    public ReputationReactionResolver Resolver => _resolver;

    public override bool IsFriendly(Unit a, Unit b)
    {
        if (a is Player && b is Player)
        {
            return _fallback.IsFriendly(a, b); // team, duel and PvP rules
        }

        return _resolver.TryGetReaction(a, b, out ReputationRank rank) ? rank >= ReputationRank.Friendly : _fallback.IsFriendly(a, b);
    }

    public override bool IsHostileTo(Unit a, Unit b)
        => _resolver.TryGetReaction(a, b, out ReputationRank rank) ? rank <= ReputationRank.Hostile : _fallback.IsHostileTo(a, b);

    public override bool CanAttack(Unit attacker, Unit victim)
    {
        if (!base.CanAttack(attacker, victim))
        {
            return false; // targetability, evade, friendly team rule, unflagged enemy player: the base rules stay
        }

        if (attacker is Player && victim is Player && !EitherCharmed(attacker, victim))
        {
            return true; // uncharmed player versus player is the base rule (team, PvP flag, duel); a charmed pair goes through the reaction checks
        }

        return _resolver.TryCanAttack(attacker, victim, out bool canAttack) ? canAttack : _fallback.CanAttack(attacker, victim);
    }

    /// <summary>
    /// vmangos Creature::OnEnterCombat (Creature.cpp:3678-3686), for a creature that just entered combat with <paramref name="player"/>: its faction
    /// (faction template row, then the faction's reputation list slot, Creature.cpp:639-642) is set at war so the player can attack back, remembered as
    /// temporary, and the client told (SMSG_SET_FACTION_ATWAR, Player::SendFactionAtWar). Nothing happens for a faction with no reputation slot or
    /// one that is already at war. World thread.
    /// </summary>
    public void OnCreatureEnteredCombat(Unit creature, Player player)
    {
        ArgumentNullException.ThrowIfNull(creature);
        ArgumentNullException.ThrowIfNull(player);
        if (_resolver.TemplateOf(creature) is not { } template || _resolver.Factions.Find(template.Faction) is not { CanHaveReputation: true } faction
            || _resolver.ReputationOf(player) is not { } reputation || !reputation.StartTemporaryAtWar(faction))
        {
            return;
        }

        player.Session.Send(WorldOpcode.SmsgSetFactionAtwar, ReputationPackets.SetFactionAtWar(faction.ReputationListId, atWar: true));
    }

    /// <summary>
    /// vmangos Unit::ClearInCombat for a player (Unit.cpp:6111-6121) runs Player::ClearTemporaryWarWithFactions: the factions that went to war
    /// through <see cref="OnCreatureEnteredCombat"/> are made peaceful again (when above Hostile) and the client is told per faction. World thread.
    /// </summary>
    public void OnPlayerLeftCombat(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (_resolver.ReputationOf(player) is not { } reputation)
        {
            return;
        }

        foreach (int listId in reputation.ClearTemporaryAtWar())
        {
            player.Session.Send(WorldOpcode.SmsgSetFactionAtwar, ReputationPackets.SetFactionAtWar(listId, atWar: false));
        }
    }
}

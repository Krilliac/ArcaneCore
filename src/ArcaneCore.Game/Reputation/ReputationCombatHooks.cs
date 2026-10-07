using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;

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

        if (attacker is Player && victim is Player)
        {
            return true; // player versus player is the base rule (team, PvP flag, duel)
        }

        return _resolver.TryCanAttack(attacker, victim, out bool canAttack) ? canAttack : _fallback.CanAttack(attacker, victim);
    }
}

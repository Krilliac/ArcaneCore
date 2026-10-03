using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Reputation;

/// <summary>
/// Creature aggro with player reputation (vmangos Unit::IsHostileTo, GetReactionTo &lt;= REP_HOSTILE): guards turn on a
/// Hated player, a faction at war is hostile, contested guards attack contested players. Falls back to
/// <paramref name="fallback"/> (the template-only <see cref="FactionCreatureHostility"/>) when the resolver cannot answer.
/// Aggro needs this and the combat hooks' attackability together (CreatureMapSystem.Aggro).
/// </summary>
public sealed class ReputationCreatureHostility(ICreatureHostility fallback, ReputationReactionResolver resolver) : ICreatureHostility
{
    private readonly ICreatureHostility _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
    private readonly ReputationReactionResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public bool IsHostile(Creature creature, Unit target)
    {
        ArgumentNullException.ThrowIfNull(creature);
        ArgumentNullException.ThrowIfNull(target);
        return _resolver.TryGetReaction(creature, target, out ReputationRank rank)
            ? rank <= ReputationRank.Hostile
            : _fallback.IsHostile(creature, target);
    }

    public bool CanAssist(Creature helper, Creature caller) => _fallback.CanAssist(helper, caller);
}

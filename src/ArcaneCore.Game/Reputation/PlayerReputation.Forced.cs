namespace ArcaneCore.Game.Reputation;

public sealed partial class PlayerReputation
{
    private readonly Dictionary<uint, ReputationRank> _forced = [];

    /// <summary>
    /// SPELL_AURA_FORCE_REACTION state (vmangos ReputationMgr::m_forcedReactions, keyed by faction id): the rank the
    /// player reads for that faction regardless of standing. In memory only; vmangos never persists it, the aura
    /// reapplied on login restores it.
    /// </summary>
    public IReadOnlyDictionary<uint, ReputationRank> ForcedReactions => _forced;

    /// <summary>ReputationMgr::GetForcedRankIfAny: the rank forced for <paramref name="factionId"/> by an aura, if any.</summary>
    public bool TryGetForcedRank(uint factionId, out ReputationRank rank) => _forced.TryGetValue(factionId, out rank);

    /// <summary>ReputationMgr::ApplyForceReaction(faction, rank, true).</summary>
    public void SetForcedReaction(uint factionId, ReputationRank rank) => _forced[factionId] = rank;

    /// <summary>ReputationMgr::ApplyForceReaction(faction, rank, false); true when a forced rank was removed.</summary>
    public bool ClearForcedReaction(uint factionId) => _forced.Remove(factionId);
}

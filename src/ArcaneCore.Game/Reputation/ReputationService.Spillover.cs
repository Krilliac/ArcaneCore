using ArcaneCore.Kernel.Reputation;

namespace ArcaneCore.Game.Reputation;

public sealed partial class ReputationService
{
    /// <summary>
    /// The reputation_reward_rate multiplier of a faction for a source (Player.cpp:6325-6350); null when the
    /// faction has no row. A rate of zero or less disables the gain (the caller returns zero).
    /// </summary>
    private float? FactionRate(ReputationSource source, uint faction)
        => Content.Rate(faction) is { } row
            ? source switch
            {
                ReputationSource.Kill => row.CreatureRate,
                ReputationSource.Quest => row.QuestRate,
                ReputationSource.Spell => row.SpellRate,
                _ => 0f,
            }
            : null;
}

using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Instances.Scripts;

/// <summary>World quest status seam for ScriptDev2 instance choices made as creatures enter a map.</summary>
public interface IInstanceQuestRewards : IMapUpdater
{
    bool? IsRewarded(Player player, uint questId);

    /// <summary>
    /// Whether the player has <paramref name="questId"/> in the INCOMPLETE state (cmangos Player::GetQuestStatus == QUEST_STATUS_INCOMPLETE; the
    /// gossip condition of the Blackrock Depths Jail Break prisoners). Null when the player's journal is not loaded, and by default (no seam).
    /// </summary>
    bool? IsIncomplete(Player player, uint questId) => null;
}

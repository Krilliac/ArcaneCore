using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Instances.Scripts;

/// <summary>World quest status seam for ScriptDev2 instance choices made as creatures enter a map.</summary>
public interface IInstanceQuestRewards : IMapUpdater
{
    bool? IsRewarded(Player player, uint questId);
}

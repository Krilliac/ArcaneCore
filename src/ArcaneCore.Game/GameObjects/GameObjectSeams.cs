using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.GameObjects;

/// <summary>
/// Quest-giver game objects (GAMEOBJECT_TYPE_QUESTGIVER, gameobject_questrelation /
/// gameobject_involvedrelation): the quests area opens its quest menu for the object. Until it
/// implements this, using such an object reports <see cref="GameObjectUseResult.Unsupported"/>.
/// </summary>
public interface IGameObjectQuestGiver
{
    /// <summary>Show <paramref name="go"/>'s quest menu to <paramref name="player"/>; false when it has none.</summary>
    bool OpenQuestMenu(Player player, GameObject go);
}

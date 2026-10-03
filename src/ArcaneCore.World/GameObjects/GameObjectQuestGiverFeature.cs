using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Features;
using ArcaneCore.World.Npc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.GameObjects;

/// <summary>
/// Quest-giving game objects (GAMEOBJECT_TYPE_QUESTGIVER): fills the
/// <see cref="GameObjectMapSystem.QuestGiver"/> seam of every map's game object system with the quest
/// feature, so CMSG_GAMEOBJ_USE on such an object opens its menu (vmangos GameObject::Use,
/// GameObject.cpp:1457-1471) instead of answering "unsupported". The relations, quest menu, status,
/// accept and turn-in then run through <c>QuestNpcServices</c> exactly as for creatures.
/// </summary>
public sealed class GameObjectQuestGiverFeature(IServiceProvider services, ILogger<GameObjectQuestGiverFeature> logger)
    : IWorldFeature, IGameObjectQuestGiver
{
    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        // The game object feature installs its per-map systems in a world-thread post of its own; this post runs
        // after it (features attach in type-name order), so its map systems and its MapCreated subscription exist.
        world.Post(() =>
        {
            world.MapCreated += AssignTo;
            foreach (Map map in world.Maps.ToArray())
            {
                AssignTo(map);
            }
        });
    }

    public bool OpenQuestMenu(Player player, GameObject go)
        => services.GetService<QuestNpcFeature>()?.Services.OpenGameObjectQuestMenu(player, go.Guid) ?? false;

    private void AssignTo(Map map)
    {
        if (services.GetService<GameObjectLootFeature>() is not { } objects)
        {
            return;
        }

        GameObjectMapSystem? system = objects.FindSystem(map);
        if (system is null)
        {
            try
            {
                system = objects.GetOrCreateSystem(map);
            }
            catch (InvalidOperationException ex)
            {
                logger.LogDebug(ex, "no game object system for map {Map} to attach the quest giver to", map.MapId);
                return;
            }
        }

        system.QuestGiver ??= this;
    }
}

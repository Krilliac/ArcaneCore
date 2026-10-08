using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Features;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Instances;

/// <summary>
/// Subscribes the ScriptDev2 quest, gossip, altar-spell and area-trigger hooks of the six classic dungeon ports
/// (<see cref="DungeonScriptHooks"/>) to the world's quest NPC, spell and area-trigger services. WorldFeatures discovers this class; each
/// hook checks the player's own instance script.
/// </summary>
public sealed class DungeonScriptFeature(IServiceProvider services) : IWorldFeature, IAreaTriggerListener, ISpellCastObserver
{
    /// <summary>Whether the posted wiring found the quest NPC services (quest accept and gossip hooks in place).</summary>
    public bool QuestHooksAttached { get; private set; }

    /// <summary>Whether the posted wiring found the spell system (this feature observes its casts).</summary>
    public bool SpellHooksAttached { get; private set; }

    public void Attach(WorldRuntime world)
    {
        // Attach runs before the world thread and before some service features finish creating their runtimes.
        world.Post(() =>
        {
            if (services.GetService<QuestNpcFeature>()?.Services is { } npcs)
            {
                npcs.QuestAccepted += OnQuestAccepted;
                npcs.GossipScript = DungeonScriptHooks.WrapGossip(npcs.GossipScript);
                QuestHooksAttached = true;
            }

            if (services.GetService<SpellFeature>()?.System is { } spells)
            {
                spells.RegisterObserver(this);
                SpellHooksAttached = true;
            }
        });
    }

    private static void OnQuestAccepted(Player player, ObjectGuid giverGuid, uint questId)
        => DungeonScriptHooks.OnQuestAccepted(player, giverGuid, questId);

    public void OnAreaTrigger(Player player, uint triggerId) => DungeonScriptHooks.OnAreaTrigger(player, triggerId);

    public void OnFinished(SpellCast cast, bool completed) => DungeonScriptHooks.OnSpellFinished(cast, completed);
}

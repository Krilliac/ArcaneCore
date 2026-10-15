using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Features;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Instances;

/// <summary>
/// Subscribes the ScriptDev2 quest, gossip, altar-spell and area-trigger hooks of the six classic dungeon ports
/// (<see cref="DungeonScriptHooks"/>) to the world's quest NPC, spell and area-trigger services. WorldFeatures discovers this class; each
/// hook checks the player's own instance script.
/// </summary>
public sealed class DungeonScriptFeature(IServiceProvider services, ILogger<DungeonScriptFeature>? logger = null) : IWorldFeature, IAreaTriggerListener, ISpellCastObserver
{
    private readonly ILogger? _logger = logger
        ?? services.GetService<ILogger<DungeonScriptFeature>>()
        ?? services.GetService<ILoggerFactory>()?.CreateLogger<DungeonScriptFeature>();

    /// <summary>Whether the posted wiring found the quest NPC services (quest accept and gossip hooks in place).</summary>
    public bool QuestHooksAttached { get; private set; }

    /// <summary>Whether the posted wiring found the spell system (this feature observes its casts).</summary>
    public bool SpellHooksAttached { get; private set; }

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);

        // Attach runs before the world thread and before some service features finish creating their runtimes.
        world.Post(() =>
        {
            try
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
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogFault("dungeon-script-attach", ex, "dungeon script hook attachment failed");
            }
        });
    }

    private void OnQuestAccepted(Player player, ObjectGuid giverGuid, ArcaneCore.Game.Quests.Quest quest)
    {
        if (!Invariant.CheckNotNull(player, nameof(player)) || !Invariant.CheckNotNull(quest, nameof(quest)) || !Invariant.Check(!player.Guid.IsEmpty, "DungeonScriptFeature.OnQuestAccepted: player and quest must be valid"))
        {
            return;
        }

        try
        {
            DungeonScriptHooks.OnQuestAccepted(player, giverGuid, quest.Id);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogFault("dungeon-script-quest-accept", ex, "dungeon script quest accept {QuestId} failed for {Player}", quest.Id, player.Guid);
        }
    }

    public void OnAreaTrigger(Player player, uint triggerId)
    {
        if (!Invariant.CheckNotNull(player, nameof(player)) || !Invariant.Check(!player.Guid.IsEmpty, "DungeonScriptFeature.OnAreaTrigger: player must have a valid GUID"))
        {
            return;
        }

        try
        {
            DungeonScriptHooks.OnAreaTrigger(player, triggerId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogFault("dungeon-script-areatrigger", ex, "dungeon script area trigger {TriggerId} failed for {Player}", triggerId, player.Guid);
        }
    }

    public void OnFinished(SpellCast cast, bool completed)
    {
        if (!Invariant.CheckNotNull(cast, nameof(cast)))
        {
            return;
        }

        try
        {
            DungeonScriptHooks.OnSpellFinished(cast, completed);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogFault("dungeon-script-spell-finished", ex, "dungeon script spell finished {SpellId} failed", cast.Spell.Id);
        }
    }

    private void LogFault(string site, Exception ex, string message, params object?[] args)
    {
        if (_logger is not null)
        {
            _logger.LogFault(site, ex, message, args);
        }
        else
        {
            FaultLedger.Shared.Record(site, ex);
        }
    }
}

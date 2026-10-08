using System.Reflection;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.Gnomeregan;
using ArcaneCore.Game.Npc;
using ArcaneCore.World.Features;
using ArcaneCore.World.Instances;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Instances;

/// <summary>
/// The world wiring of the ScriptDev2 dungeon hooks in a started world (the hooks themselves run against instance maps in
/// ArcaneCore.Game.Tests DungeonScriptHooksTests): the quest NPC service's QuestAccepted, its gossip script chain, the spell system's
/// observers and the area-trigger listeners all reach DungeonScriptFeature.
/// </summary>
public sealed class DungeonScriptFeatureTests
{
    [Fact]
    public void DungeonAdaptersAreDiscoveredAsAreaTriggerListeners()
    {
        Assert.Contains(typeof(DungeonScriptFeature), WorldFeatures.FeatureTypes);
        Assert.True(typeof(IAreaTriggerListener).IsAssignableFrom(typeof(DungeonScriptFeature)));
    }

    [Fact]
    public async Task StartedWorld_SubscribesTheQuestGossipSpellAndAreaTriggerHooks()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        DungeonScriptFeature feature = host.WorldServices.GetRequiredService<DungeonScriptFeature>();
        await host.WaitForWorldAsync(() => feature.QuestHooksAttached && feature.SpellHooksAttached, "the dungeon hooks to attach");

        await host.OnWorldAsync(() =>
        {
            QuestNpcServices npcs = host.WorldServices.GetRequiredService<QuestNpcFeature>().Services;
            Assert.IsType<EmiGossipScript>(npcs.GossipScript);
            Assert.Contains(QuestAcceptedHandlers(npcs), handler => handler.Method.DeclaringType == typeof(DungeonScriptFeature));
            Assert.Contains(feature, host.WorldServices.GetRequiredService<SpellFeature>().System.Observers);
            // TeleportHandlers.HandleAreaTrigger asks every world feature that is an area-trigger listener.
            Assert.Contains(feature, host.WorldServices.GetServices<IWorldFeature>().OfType<IAreaTriggerListener>());
        });
    }

    /// <summary>The handlers subscribed to the field-like event QuestNpcServices.QuestAccepted (its compiler-generated backing field).</summary>
    private static Delegate[] QuestAcceptedHandlers(QuestNpcServices npcs)
    {
        FieldInfo field = typeof(QuestNpcServices).GetField(nameof(QuestNpcServices.QuestAccepted), BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("QuestAccepted is no longer a field-like event");
        return field.GetValue(npcs) is Action<Player, ObjectGuid, uint> handlers ? handlers.GetInvocationList() : [];
    }
}

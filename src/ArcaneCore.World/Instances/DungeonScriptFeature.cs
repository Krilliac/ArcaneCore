using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.Gnomeregan;
using ArcaneCore.Game.Instances.Scripts.RazorfenDowns;
using ArcaneCore.Game.Instances.Scripts.RazorfenKraul;
using ArcaneCore.Game.Instances.Scripts.ScarletMonastery;
using ArcaneCore.Game.Instances.Scripts.Uldaman;
using ArcaneCore.Game.Instances.Scripts.ZulFarrak;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Features;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Instances;

/// <summary>ScriptDev2 quest, gossip, altar spell and area-trigger adapters for the six classic dungeons.
/// WorldFeatures discovers this class; each callback checks the player's actual instance map.</summary>
public sealed class DungeonScriptFeature(IServiceProvider services) : IWorldFeature, IAreaTriggerListener, ISpellCastObserver
{
    public void Attach(WorldRuntime world)
    {
        // Attach runs before the world thread and before some service features finish creating their runtimes.
        world.Post(() =>
        {
            if (services.GetService<QuestNpcFeature>()?.Services is { } npcs)
            {
                npcs.QuestAccepted += OnQuestAccepted;
                npcs.GossipScript = new EmiGossipScript(npcs.GossipScript);
            }
            services.GetService<SpellFeature>()?.System.RegisterObserver(this);
        });
    }

    private static void OnQuestAccepted(Player player, ObjectGuid giverGuid, uint questId)
    {
        if (player.Map?.FindObject(giverGuid) is not Creature giver) return;
        switch (questId, giver.AI)
        {
            case (WillixAi.Quest, WillixAi willix): willix.AcceptQuest(player); break;
            case (BelnistraszAi.Quest, BelnistraszAi belnistrasz): belnistrasz.AcceptQuest(player); break;
            case (KernobeeAi.Quest, KernobeeAi kernobee): kernobee.AcceptQuest(player); break;
        }
    }

    public void OnAreaTrigger(Player player, uint triggerId)
    {
        if (player.IsGameMaster || !player.IsAlive) return;
        if (triggerId == ScarletMonasteryInstance.CathedralTrigger
            && player.Map?.FindUpdater<InstanceData>() is ScarletMonasteryInstance scarlet
            && player.Inventory.GetItemCount(ScarletMonasteryInstance.CorruptedAshbringer) > 0)
            scarlet.EnterCathedral();
        else if (triggerId == ZulFarrakInstance.AntusulTrigger
                 && player.Map?.FindUpdater<InstanceData>() is ZulFarrakInstance zf
                 && zf.GetData(ZulFarrakInstance.TypeAntusul) is EncounterState.NotStarted or EncounterState.Fail
                 && zf.FindAntusul() is { IsAlive: true, AI: { } ai })
            ai.AttackStart(player);
    }

    /// <summary>ScriptDev2 ProcessEventId_event_spell_altar_boss_aggro and event_spell_unlocking,
    /// called after a successful player cast of spells 11568/10340/10738.</summary>
    public void OnFinished(SpellCast cast, bool completed)
    {
        if (!completed || cast.Caster is not Player player || player.Map is null) return;
        if (player.Map.FindUpdater<InstanceData>() is UldamanInstance uldaman)
        {
            if (cast.Spell.Id == 11568) uldaman.StartEvent(UldamanInstance.AltarKeeperEvent, player);
            else if (cast.Spell.Id == 10340) uldaman.StartEvent(UldamanInstance.AltarArchaedasEvent, player);
        }
        else if (cast.Spell.Id == 10738 && player.Map.FindUpdater<InstanceData>() is ZulFarrakInstance zf && zf.StartPyramid())
            player.Map.FindUpdater<CreatureMapSystem>()?.StartRelayScript(ZulFarrakInstance.EventRelayOffset + 2609, player, null);
    }
}

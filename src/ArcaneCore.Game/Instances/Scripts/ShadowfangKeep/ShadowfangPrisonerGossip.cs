using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>GossipHello/GossipSelect_npc_shadowfang_prisoner, mangos-classic
/// shadowfang_keep/shadowfang_keep.cpp:160-192. The one option's text comes from
/// sql/scriptdev2/scriptdev2.sql script_gossip entry -3033000 (absent from ClassicDB z2815).</summary>
public sealed class ShadowfangPrisonerGossip : INpcGossipScript
{
    private const uint DoorAction = 1001, MainSender = 1;
    private const string DoorOption = "Please unlock the courtyard door.";

    public ScriptedGossipMenu? Hello(Player player, NpcInfo npc)
    {
        if (!TryFind(player, npc, out _, out ShadowfangKeepInstance? instance))
        {
            return null;
        }

        IReadOnlyList<ScriptedGossipItem> items = instance.GetData(ShadowfangKeepInstance.TypeFreeNpc) == EncounterState.Done
            ? [] : [new ScriptedGossipItem(0, DoorOption, MainSender, DoorAction)];
        return new ScriptedGossipMenu(false, npc.Entry == ShadowfangKeepInstance.NpcAda ? 799u : 798u, items);
    }

    public uint Select(Player player, NpcInfo npc, uint sender, uint action) => 0;

    public ScriptedGossipReply SelectReply(Player player, NpcInfo npc, uint sender, uint action)
    {
        if (sender != MainSender || action != DoorAction || !TryFind(player, npc, out Creature? creature, out ShadowfangKeepInstance? instance)
            || instance.GetData(ShadowfangKeepInstance.TypeFreeNpc) == EncounterState.Done)
        {
            return default;
        }

        if (creature.AI is ShadowfangPrisonerAi escort && escort.Start())
        {
            player.Map!.FindUpdater<CreatureMapSystem>()?.SayText(creature,
                npc.Entry == ShadowfangKeepInstance.NpcAsh ? -1033000 : -1033003);
        }

        return new ScriptedGossipReply(0, Close: true);
    }

    private static bool TryFind(Player player, NpcInfo npc, out Creature creature, out ShadowfangKeepInstance instance)
    {
        creature = null!;
        instance = null!;
        if (npc.Entry is not (ShadowfangKeepInstance.NpcAda or ShadowfangKeepInstance.NpcAsh)
            || player.Map?.FindObject(npc.Guid) is not Creature found
            || player.Map.FindUpdater<InstanceData>() is not ShadowfangKeepInstance script)
        {
            return false;
        }

        creature = found;
        instance = script;
        return true;
    }
}

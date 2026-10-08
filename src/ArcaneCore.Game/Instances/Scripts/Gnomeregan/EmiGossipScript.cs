using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Kernel.Npc;

namespace ArcaneCore.Game.Instances.Scripts.Gnomeregan;

/// <summary>ScriptDev2 GossipHello/GossipSelect_npc_blastmaster_emi_shortfuse
/// (mangos-classic gnomeregan/gnomeregan.cpp). The option text is ClassicDB z2815 gossip_texts -3090000.</summary>
public sealed class EmiGossipScript(INpcGossipScript? other = null) : INpcGossipScript
{
    public const uint StartAction = 1001; // GOSSIP_ACTION_INFO_DEF + 1 (sc_gossip.h:112)

    public ScriptedGossipMenu? Hello(Player player, NpcInfo npc)
    {
        if (npc.Entry != EmiShortfuseAi.Entry) return other?.Hello(player, npc);
        if (player.Map?.FindUpdater<InstanceData>() is not GnomereganInstance data ||
            data.GetData(GnomereganInstance.TypeGrubbis) is not (EncounterState.NotStarted or EncounterState.Fail))
            return ScriptedGossipMenu.Nothing;
        return new ScriptedGossipMenu(false, 0, [new ScriptedGossipItem(0, "I am ready to begin.", 1, StartAction)]);
    }

    public uint Select(Player player, NpcInfo npc, uint sender, uint action)
        => SelectReply(player, npc, sender, action).NpcTextId;

    public ScriptedGossipReply SelectReply(Player player, NpcInfo npc, uint sender, uint action)
    {
        if (npc.Entry != EmiShortfuseAi.Entry)
            return other?.SelectReply(player, npc, sender, action) ?? default;
        if (action == StartAction && player.Map?.FindObject(npc.Guid) is Creature { AI: EmiShortfuseAi emi })
            emi.StartEvent(player);
        return new ScriptedGossipReply(0, Close: true);
    }
}

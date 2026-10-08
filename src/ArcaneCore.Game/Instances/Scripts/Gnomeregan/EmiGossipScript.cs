using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Kernel.Npc;

namespace ArcaneCore.Game.Instances.Scripts.Gnomeregan;

/// <summary>ScriptDev2 GossipHello/GossipSelect_npc_blastmaster_emi_shortfuse (mangos-classic gnomeregan/gnomeregan.cpp:601-630). The option
/// is ADD_GOSSIP_ITEM_ID(GOSSIP_ITEM_START = -3090000): its text is read from the imported gossip_texts row (carried in creature_ai_texts);
/// the menu text is the creature's own (SEND_GOSSIP_MENU(GetGossipTextId(creature)): NpcTextId 0, see QuestNpcServices.SendScriptedGossip).
/// Every other NPC goes on to the script this one wraps.</summary>
public sealed class EmiGossipScript(INpcGossipScript? other = null) : INpcGossipScript
{
    public const uint StartAction = 1001; // GOSSIP_ACTION_INFO_DEF + 1 (sc_gossip.h:112)
    public const int StartOptionText = -3090000;

    /// <summary>ClassicDB z2815 gossip_texts -3090000, used when a world database was imported before that row was carried.</summary>
    public const string StartOptionFallback = "I am ready to begin.";

    public INpcGossipScript? Next => other;

    public ScriptedGossipMenu? Hello(Player player, NpcInfo npc)
    {
        if (npc.Entry != EmiShortfuseAi.Entry) return other?.Hello(player, npc);
        if (player.Map?.FindUpdater<InstanceData>() is not GnomereganInstance data ||
            data.GetData(GnomereganInstance.TypeGrubbis) is not (EncounterState.NotStarted or EncounterState.Fail))
            return ScriptedGossipMenu.Nothing;
        string option = player.Map.FindUpdater<CreatureMapSystem>()?.Content.Ai.FindText(StartOptionText)?.Content is { Length: > 0 } text
            ? text
            : StartOptionFallback;
        return new ScriptedGossipMenu(false, 0, [new ScriptedGossipItem(0, option, 1, StartAction)]);
    }

    public uint Select(Player player, NpcInfo npc, uint sender, uint action)
        => SelectReply(player, npc, sender, action).NpcTextId;

    public ScriptedGossipReply SelectReply(Player player, NpcInfo npc, uint sender, uint action)
    {
        if (npc.Entry != EmiShortfuseAi.Entry)
            return other?.SelectReply(player, npc, sender, action) ?? default;
        if (action == StartAction && player.Map?.FindUpdater<InstanceData>() is GnomereganInstance data
            && data.GetData(GnomereganInstance.TypeGrubbis) is EncounterState.NotStarted or EncounterState.Fail
            && player.Map.FindObject(npc.Guid) is Creature { AI: EmiShortfuseAi emi })
            emi.StartEvent(player);
        return new ScriptedGossipReply(0, Close: true);
    }
}

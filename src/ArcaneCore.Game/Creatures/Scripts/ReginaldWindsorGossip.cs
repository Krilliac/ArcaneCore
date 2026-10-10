using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// GossipHello/GossipSelect_npc_reginald_windsor (stormwind_city.cpp at 3e8597afe7): once he waits outside the keep, a player with 6402
/// rewarded and 6403 not complete may tell him to go in (text 5633, option script_gossip -3000107 from classic-db z2815). Otherwise his
/// own gossip.
/// </summary>
public sealed class ReginaldWindsorGossip(Func<Player, PlayerQuestLog?> quests) : INpcGossipScript
{
    public const uint TextMasquerade = 5633, ActionStart = 1001, SenderMain = 1;
    public const string OptionReginald = "I am ready, as are my forces. Let us end this masquerade!";

    public ScriptedGossipMenu? Hello(Player player, NpcInfo npc)
    {
        if (Find(player, npc) is not { KeepEventReady: true } || quests(player) is not { } log
            || log.GetStatus(ReginaldWindsorAI.QuestTheGreatMasquerade) == QuestStatus.Complete
            || log.Get(ReginaldWindsorAI.QuestStormwindRendezvous) is not { Rewarded: true })
        {
            return null;
        }

        return new ScriptedGossipMenu((npc.NpcFlags & NpcFlags.QuestGiver) != 0, TextMasquerade,
            [new ScriptedGossipItem(0, OptionReginald, SenderMain, ActionStart)]);
    }

    public uint Select(Player player, NpcInfo npc, uint sender, uint action) => 0;

    public ScriptedGossipReply SelectReply(Player player, NpcInfo npc, uint sender, uint action)
    {
        if (action != ActionStart || Find(player, npc) is not { } windsor)
        {
            return default;
        }

        windsor.StartKeepEvent();
        return new ScriptedGossipReply(0, Close: true);
    }

    private static ReginaldWindsorAI? Find(Player player, NpcInfo npc)
        => player.Map?.FindUpdater<CreatureMapSystem>()?.Creatures.FirstOrDefault(c => c.Guid == npc.Guid)?.AI as ReginaldWindsorAI;
}

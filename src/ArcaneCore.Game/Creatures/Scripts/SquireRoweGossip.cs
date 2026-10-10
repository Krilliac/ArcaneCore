using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// GossipHello/GossipSelect_npc_squire_rowe (stormwind_city.cpp at 3e8597afe7): with 6402 complete but not rewarded, or rewarded while 6403
/// is not complete, he offers to signal Windsor (text 9065) unless Windsor is already out (9064); otherwise his default text (9063).
/// The option is script_gossip -3000106 from classic-db z2815.
/// </summary>
public sealed class SquireRoweGossip(Func<Player, PlayerQuestLog?> quests) : INpcGossipScript
{
    public const uint TextDefault = 9063, TextProgress = 9064, TextStart = 9065, ActionStart = 1001, SenderMain = 1;
    public const string OptionWindsor = "Let Marshal Windsor know that I am ready.";

    public ScriptedGossipMenu? Hello(Player player, NpcInfo npc)
    {
        if (Find(player, npc) is not { } rowe || quests(player) is not { } log)
        {
            return null;
        }

        bool rendezvousRewarded = log.Get(SquireRoweAI.QuestStormwindRendezvous) is { Rewarded: true } && log.GetStatus(SquireRoweAI.QuestStormwindRendezvous) != QuestStatus.None;
        bool ready = (log.GetStatus(SquireRoweAI.QuestStormwindRendezvous) == QuestStatus.Complete && !rendezvousRewarded)
            || (rendezvousRewarded && log.GetStatus(SquireRoweAI.QuestTheGreatMasquerade) != QuestStatus.Complete);
        bool showQuests = (npc.NpcFlags & NpcFlags.QuestGiver) != 0;
        if (!ready)
        {
            return new ScriptedGossipMenu(showQuests, TextDefault, []);
        }

        return rowe.EventInProgress
            ? new ScriptedGossipMenu(showQuests, TextProgress, [])
            : new ScriptedGossipMenu(showQuests, TextStart, [new ScriptedGossipItem(0, OptionWindsor, SenderMain, ActionStart)]);
    }

    public uint Select(Player player, NpcInfo npc, uint sender, uint action) => 0;

    public ScriptedGossipReply SelectReply(Player player, NpcInfo npc, uint sender, uint action)
    {
        if (action != ActionStart || Find(player, npc) is not { } rowe)
        {
            return default;
        }

        rowe.StartFromGossip(player);
        return new ScriptedGossipReply(0, Close: true);
    }

    private static SquireRoweAI? Find(Player player, NpcInfo npc)
        => player.Map?.FindUpdater<CreatureMapSystem>()?.Creatures.FirstOrDefault(c => c.Guid == npc.Guid)?.AI as SquireRoweAI;
}

using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Lady Katrana Prestor (entry 1749, Stormwind), "The True Masters" (quest 4185): mangos-classic ScriptDev2 <c>npc_lady_katrana_prestor</c>
/// (eastern_kingdoms/stormwind_city.cpp:454-497, registered :1368-1372). Her menu 2693 shows her quests and, with 4185 incomplete, the first of
/// four lines; each line leads to the next menu (2694, 2695, 2696) and the fourth closes the menu and completes the quest's event. The line
/// texts are the scriptdev2 <c>gossip_texts</c> -3000120..-3000123 (mangos-classic sql/scriptdev2/scriptdev2.sql:2539-2542; ClassicDB z2815
/// has no such rows).
/// </summary>
public sealed class KatranaPrestorGossip(Func<Player, uint, bool> questIncomplete, Action<Player, uint> eventHappens) : INpcGossipScript
{
    public const uint Entry = 1749, QuestTrueMasters = 4185;
    public const uint TextMenu1 = 2693, TextMenu2 = 2694, TextMenu3 = 2695, TextMenu4 = 2696, SenderMain = 1;

    /// <summary>GOSSIP_ACTION_INFO_DEF (1000) and the next three.</summary>
    public const uint Action1 = 1000, Action2 = 1001, Action3 = 1002, Action4 = 1003;

    public const string OptionKat1 = "Pardon the intrusion, Lady Prestor, but Highlord Bolvar suggested that I seek your advice.";
    public const string OptionKat2 = "My apologies, Lady Prestor.";
    public const string OptionKat3 = "Begging your pardon";
    public const string OptionKat4 = "Thank you for your time";

    /// <summary>GossipHello (:462-473).</summary>
    public ScriptedGossipMenu? Hello(Player player, NpcInfo npc)
    {
        ArgumentNullException.ThrowIfNull(npc);
        bool showQuests = (npc.NpcFlags & NpcFlags.QuestGiver) != 0;
        return new ScriptedGossipMenu(showQuests, TextMenu1, questIncomplete(player, QuestTrueMasters)
            ? [new ScriptedGossipItem(0, OptionKat1, SenderMain, Action1)]
            : []);
    }

    public uint Select(Player player, NpcInfo npc, uint sender, uint action) => SelectReply(player, npc, sender, action).NpcTextId;

    /// <summary>GossipSelect (:475-497).</summary>
    public ScriptedGossipReply SelectReply(Player player, NpcInfo npc, uint sender, uint action)
    {
        switch (action)
        {
            case Action1:
                return Next(TextMenu2, OptionKat2, Action2);
            case Action2:
                return Next(TextMenu3, OptionKat3, Action3);
            case Action3:
                return Next(TextMenu4, OptionKat4, Action4);
            case Action4:
                eventHappens(player, QuestTrueMasters);
                return new ScriptedGossipReply(0, Close: true);
            default:
                return default;
        }
    }

    private static ScriptedGossipReply Next(uint text, string option, uint action)
        => new(text) { NextMenu = new ScriptedGossipMenu(false, text, [new ScriptedGossipItem(0, option, SenderMain, action)]) };
}

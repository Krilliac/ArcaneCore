using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Loramus Thalipedes (entry 7783, Azshara, <c>npc_loramus_thalipedes</c>): completes the exploration/event objectives
/// (<c>SpecialFlags=2</c>) of quests 2744 ("The Demon Hunter") and 3141 ("Loramus") through his gossip menu chain
/// (npc_text 1796, 1812, and 1813..1817; vmangos migration 20220224185216_world.sql:5416-5448).
/// </summary>
public sealed class LoramusThalipedesGossip(
    Func<Player, uint, bool>? questIncomplete = null,
    Action<Player, uint>? eventHappens = null) : INpcGossipScript
{
    public const uint Entry = 7783;
    public const string ScriptName = "npc_loramus_thalipedes";
    public const uint QuestTheDemonHunter = 2744;
    public const uint QuestLoramus = 3141;

    public const uint TextGreeting = 1796;
    public const uint TextDemonHunter = 1812;
    public const uint TextStory1 = 1813;
    public const uint TextStory2 = 1814;
    public const uint TextStory3 = 1815;
    public const uint TextStory4 = 1816;
    public const uint TextStory5 = 1817;
    public const uint SenderMain = 1;

    public const uint ActionDemonHunter1 = 1000;
    public const uint ActionDemonHunter2 = 1001;
    public const uint ActionLoramus1 = 1002;
    public const uint ActionLoramus2 = 1003;
    public const uint ActionLoramus3 = 1004;
    public const uint ActionLoramus4 = 1005;
    public const uint ActionLoramus5 = 1006;
    public const uint ActionLoramus6 = 1007;

    public const string OptionDemonHunter1 = "Hail, Loramus. I bring word from the Blasted Lands.";
    public const string OptionDemonHunter2 = "Will you help?";
    public const string OptionLoramus1 = "I wish to hear your story.";
    public const string OptionLoramus2 = "Please continue.";
    public const string OptionLoramus3 = "I do not understand.";
    public const string OptionLoramus4 = "Indeed.";
    public const string OptionLoramus5 = "I will do this with or without your help, Loramus.";
    public const string OptionLoramus6 = "Yes.";

    private bool IsIncomplete(Player player, uint questId)
        => questIncomplete is not null
            ? questIncomplete(player, questId)
            : player.Map?.FindUpdater<CreatureMapSystem>()?.QuestIncomplete(player, questId) == true;

    private void CompleteEvent(Player player, uint questId)
    {
        if (eventHappens is not null)
        {
            eventHappens(player, questId);
        }
        else
        {
            player.Map?.FindUpdater<CreatureMapSystem>()?.QuestEventHappened(player, questId);
        }
    }

    public ScriptedGossipMenu? Hello(Player player, NpcInfo npc)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(npc);
        bool showQuests = (npc.NpcFlags & NpcFlags.QuestGiver) != 0;
        var items = new List<ScriptedGossipItem>(2);
        if (IsIncomplete(player, QuestTheDemonHunter))
        {
            items.Add(new ScriptedGossipItem(0, OptionDemonHunter1, SenderMain, ActionDemonHunter1));
        }

        if (IsIncomplete(player, QuestLoramus))
        {
            items.Add(new ScriptedGossipItem(0, OptionLoramus1, SenderMain, ActionLoramus1));
        }

        return new ScriptedGossipMenu(showQuests, TextGreeting, items);
    }

    public uint Select(Player player, NpcInfo npc, uint sender, uint action) => SelectReply(player, npc, sender, action).NpcTextId;

    public ScriptedGossipReply SelectReply(Player player, NpcInfo npc, uint sender, uint action)
    {
        ArgumentNullException.ThrowIfNull(player);
        switch (action)
        {
            case ActionDemonHunter1:
                return Next(TextDemonHunter, OptionDemonHunter2, ActionDemonHunter2);
            case ActionDemonHunter2:
                CompleteEvent(player, QuestTheDemonHunter);
                return new ScriptedGossipReply(0, Close: true);
            case ActionLoramus1:
                return Next(TextStory1, OptionLoramus2, ActionLoramus2);
            case ActionLoramus2:
                return Next(TextStory2, OptionLoramus3, ActionLoramus3);
            case ActionLoramus3:
                return Next(TextStory3, OptionLoramus4, ActionLoramus4);
            case ActionLoramus4:
                return Next(TextStory4, OptionLoramus5, ActionLoramus5);
            case ActionLoramus5:
                return Next(TextStory5, OptionLoramus6, ActionLoramus6);
            case ActionLoramus6:
                CompleteEvent(player, QuestLoramus);
                return new ScriptedGossipReply(0, Close: true);
            default:
                return default;
        }
    }

    private static ScriptedGossipReply Next(uint text, string option, uint action)
        => new(text) { NextMenu = new ScriptedGossipMenu(false, text, [new ScriptedGossipItem(0, option, SenderMain, action)]) };
}

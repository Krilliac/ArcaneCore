using ArcaneCore.MockClient.Protocol;

namespace ArcaneCore.MockClient.Scenarios;

internal static partial class ScenarioWire
{
    // Independent build5875 layouts: vmangos/core@4b3d241cffe245a1f68da11380bce96c23db48c0
    // src/game/Server/Packets/Quest.cpp QuestGiverQuestList/QuestGiverRequestItems;
    // src/game/GossipDef.cpp SendGossipMenu (32 quests, 2048-byte option text, 512-byte titles);
    // src/game/GossipDef.h GOSSIP_MAX_MENU_ITEMS=32 supplies the option bound.
    // gtker/wow_messages@70abb9deff0bb63440d8aeb4386b820653e8a176
    // wow_message_parser/wowm/world/{quest/smsg_questgiver_quest_list,gossip/smsg_gossip_message}.wowm.
    // Only field order and protocol bounds are used; no upstream implementation is copied.
    internal static MockQuestList QuestList(byte[] payload)
    {
        var cursor = new WireCursor(payload);
        ulong guid = cursor.UInt64();
        string greeting = cursor.CString(8192);
        uint delay = cursor.UInt32();
        uint emote = cursor.UInt32();
        MockQuestMenuEntry[] quests = ReadQuestMenu(cursor, cursor.Byte(), 32);
        cursor.End();
        return new MockQuestList(guid, greeting, delay, emote, quests);
    }

    internal static MockGossipMessage GossipMessage(byte[] payload)
    {
        var cursor = new WireCursor(payload);
        ulong guid = cursor.UInt64();
        uint textId = cursor.UInt32();
        uint count = cursor.UInt32();
        Require(count <= 32, "Gossip exceeded thirty-two options.");
        var options = new MockGossipOption[checked((int)count)];
        for (int index = 0; index < options.Length; index++)
        {
            uint id = cursor.UInt32();
            byte icon = cursor.Byte();
            byte coded = cursor.Byte();
            Require(coded <= 1, "Gossip coded flag must be zero or one.");
            options[index] = new MockGossipOption(id, icon, coded != 0, cursor.CString(2048));
        }

        MockQuestMenuEntry[] quests = ReadQuestMenu(cursor, cursor.UInt32(), 32);
        cursor.End();
        return new MockGossipMessage(guid, textId, options, quests);
    }

    private static MockQuestMenuEntry[] ReadQuestMenu(WireCursor cursor, uint count, uint maximum)
    {
        Require(count <= maximum, $"Quest menu exceeded {maximum} entries.");
        var quests = new MockQuestMenuEntry[checked((int)count)];
        for (int index = 0; index < quests.Length; index++)
        {
            quests[index] = new MockQuestMenuEntry(cursor.UInt32(), cursor.UInt32(), unchecked((int)cursor.UInt32()), cursor.CString(512));
        }

        return quests;
    }

    internal static MockQuestRequestItems QuestRequestItems(byte[] payload)
    {
        var cursor = new WireCursor(payload);
        ulong guid = cursor.UInt64();
        uint quest = cursor.UInt32();
        string title = cursor.CString(1024);
        string text = cursor.CString(8192);
        uint delay = cursor.UInt32();
        uint emote = cursor.UInt32();
        uint closeOnCancel = cursor.UInt32();
        uint money = cursor.UInt32();
        uint count = cursor.UInt32();
        Require(count <= 4, "Quest request exceeded four required items.");
        var items = new MockQuestReward[checked((int)count)];
        for (int index = 0; index < items.Length; index++)
        {
            items[index] = new MockQuestReward(cursor.UInt32(), cursor.UInt32(), cursor.UInt32());
        }

        uint[] flags = [cursor.UInt32(), cursor.UInt32(), cursor.UInt32(), cursor.UInt32()];
        cursor.End();
        return new MockQuestRequestItems(guid, quest, title, text, delay, emote, closeOnCancel, money, items, flags);
    }
}

internal sealed record MockQuestMenuEntry(uint QuestId, uint Icon, int Level, string Title);

internal sealed record MockQuestList(ulong Guid, string Greeting, uint EmoteDelay, uint Emote, MockQuestMenuEntry[] Quests);

internal sealed record MockGossipOption(uint Id, byte Icon, bool Coded, string Text);

internal sealed record MockGossipMessage(ulong Guid, uint TextId, MockGossipOption[] Options, MockQuestMenuEntry[] Quests);

internal sealed record MockQuestRequestItems(ulong Guid, uint QuestId, string Title, string Text, uint EmoteDelay,
    uint Emote, uint CloseOnCancel, uint RequiredMoney, MockQuestReward[] RequiredItems, uint[] CompleteFlags);

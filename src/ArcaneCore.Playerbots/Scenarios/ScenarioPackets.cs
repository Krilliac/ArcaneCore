using ArcaneCore.Game;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Playerbots.Scenarios;

/// <summary>
/// Typed client (CMSG) payload builders for build 5875 used by the playerbot scenario harness. Layouts are the ones the
/// server's own handlers parse (gtker/wow_messages 1.12, vmangos *Handler.cpp); each summary names the handler. The
/// external mock client (tools/ArcaneCore.MockClient ScenarioWire) deliberately keeps its own independent encodings as a
/// second oracle, so these builders are not shared with it; the GUID/quest layouts are byte-identical.
/// </summary>
public static class ScenarioPackets
{
    /// <summary>A bare u64 GUID (CMSG_SET_SELECTION, CMSG_ATTACKSWING, CMSG_LOOT, CMSG_LOOT_RELEASE, CMSG_INITIATE_TRADE,
    /// CMSG_DUEL_ACCEPTED/CANCELLED, CMSG_GAMEOBJ_USE, CMSG_GET_MAIL_LIST, CMSG_QUESTGIVER_HELLO ...).</summary>
    public static byte[] Guid(ulong guid)
    {
        var w = new PacketWriter(8);
        w.WriteUInt64(guid);
        return w.ToArray();
    }

    /// <summary>A bare u32 (CMSG_AREATRIGGER, CMSG_SET_TRADE_GOLD, CMSG_QUEST_QUERY ...).</summary>
    public static byte[] UInt32(uint value)
    {
        var w = new PacketWriter(4);
        w.WriteUInt32(value);
        return w.ToArray();
    }

    /// <summary>u64 GUID then u32 (quest giver accept/complete/request-reward; mail take item/money/read/delete).</summary>
    public static byte[] GuidUInt32(ulong guid, uint value)
    {
        var w = new PacketWriter(12);
        w.WriteUInt64(guid);
        w.WriteUInt32(value);
        return w.ToArray();
    }

    /// <summary>CMSG_QUESTGIVER_CHOOSE_REWARD: u64 giver, u32 quest, u32 reward choice.</summary>
    public static byte[] QuestChooseReward(ulong giver, uint quest, uint choice)
    {
        var w = new PacketWriter(16);
        w.WriteUInt64(giver);
        w.WriteUInt32(quest);
        w.WriteUInt32(choice);
        return w.ToArray();
    }

    /// <summary>CMSG_GROUP_INVITE: CString member name (GroupHandlers).</summary>
    public static byte[] GroupInvite(string name) => CString(name);

    /// <summary>CMSG_LOOT_METHOD: u32 method, u64 master looter, u32 threshold (GroupHandlers.HandleLootMethod).</summary>
    public static byte[] LootMethod(uint method, ulong masterLooter, uint threshold)
    {
        var w = new PacketWriter(16);
        w.WriteUInt32(method);
        w.WriteUInt64(masterLooter);
        w.WriteUInt32(threshold);
        return w.ToArray();
    }

    /// <summary>CMSG_SET_TRADE_ITEM: u8 trade slot, u8 bag, u8 slot (EconomyHandlers).</summary>
    public static byte[] SetTradeItem(byte tradeSlot, byte bag, byte slot) => [tradeSlot, bag, slot];

    /// <summary>CMSG_AUTOSTORE_LOOT_ITEM: u8 loot slot.</summary>
    public static byte[] LootSlot(byte slot) => [slot];

    /// <summary>
    /// CMSG_SEND_MAIL: u64 mailbox, CString receiver, subject, body, u32 stationery, u32 package, u64 item, u32 money,
    /// u32 COD, u64 unknown (EconomyHandlers.HandleSendMail skips the two u32 and the trailing u64).
    /// </summary>
    public static byte[] SendMail(ulong mailbox, string receiver, string subject, string body, ulong item, uint money, uint cod)
    {
        var w = new PacketWriter(64 + receiver.Length + subject.Length + body.Length);
        w.WriteUInt64(mailbox);
        w.WriteCString(receiver);
        w.WriteCString(subject);
        w.WriteCString(body);
        w.WriteUInt32(41); // stationery: standard
        w.WriteUInt32(0);
        w.WriteUInt64(item);
        w.WriteUInt32(money);
        w.WriteUInt32(cod);
        w.WriteUInt64(0);
        return w.ToArray();
    }

    /// <summary>CMSG_CAST_SPELL: u32 spell then the cast targets (SpellHandlers.HandleCastSpell).</summary>
    public static byte[] CastSpell(uint spellId, SpellCastTargets targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var w = new PacketWriter(24);
        w.WriteUInt32(spellId);
        targets.Write(w);
        return w.ToArray();
    }

    /// <summary>CMSG_CAST_SPELL at a unit.</summary>
    public static byte[] CastSpellAt(uint spellId, ulong unit) => CastSpell(spellId, SpellCastTargets.ForUnit(new ObjectGuid(unit)));

    /// <summary>
    /// CMSG_MESSAGECHAT: u32 type, u32 language, CString target for whispers and channels, CString message
    /// (ChatHandlers.HandleMessageChat).
    /// </summary>
    public static byte[] Chat(ChatType type, Language language, string message, string? target = null)
    {
        var w = new PacketWriter(16 + message.Length + (target?.Length ?? 0));
        w.WriteUInt32((uint)type);
        w.WriteUInt32((uint)language);
        if (type is ChatType.Whisper or ChatType.Channel)
        {
            w.WriteCString(target ?? throw new ArgumentNullException(nameof(target), "whispers and channels need a target"));
        }

        w.WriteCString(message);
        return w.ToArray();
    }

    private static byte[] CString(string value)
    {
        var w = new PacketWriter(value.Length + 1);
        w.WriteCString(value);
        return w.ToArray();
    }
}

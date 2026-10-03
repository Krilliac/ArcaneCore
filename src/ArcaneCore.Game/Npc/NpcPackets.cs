using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Npc;

/// <summary>One line of SMSG_LIST_INVENTORY (vmangos WorldSession::SendListInventory).</summary>
public readonly record struct VendorListEntry(uint Index, uint Item, uint DisplayId, uint MaxCount, uint Price, uint MaxDurability, uint BuyCount);

/// <summary>One line of SMSG_TRAINER_LIST (vmangos SendTrainerSpellHelper).</summary>
public readonly record struct TrainerListEntry(
    uint Spell, TrainerSpellState State, uint Cost, bool CanLearnPrimaryProf, bool PrimaryProfFirstRank,
    byte ReqLevel, uint ReqSkill, uint ReqSkillValue, uint ChainNode1, uint ChainNode2);

/// <summary>vmangos TrainerSpellState.</summary>
public enum TrainerSpellState : byte
{
    Green = 0,
    Red = 1,
    Gray = 2,
    GreenDisabled = 10,
}

/// <summary>vmangos Player.h:90-95; wow_messages smsg_buy_bank_slot_result.wowm:3-8.</summary>
public enum BankSlotResult : uint
{
    TooMany = 0,
    InsufficientFunds = 1,
    NotBanker = 2,
    Ok = 3,
}

/// <summary>
/// NPC service packet bodies (build 5875), after vmangos src/game/Protocol/Packets/Npc.cpp,
/// Taxi.cpp, Item.cpp, Misc.cpp and Handlers/NPCHandler.cpp; gtker/wow_messages cross-checked.
/// </summary>
public static class NpcPackets
{
    /// <summary>SMSG_BUY_BANK_SLOT_RESULT is one little-endian u32 (wow_messages smsg_buy_bank_slot_result.wowm:10-12).</summary>
    public static PacketWriter BuyBankSlotResult(BankSlotResult result)
    {
        var writer = new PacketWriter(4);
        writer.WriteUInt32((uint)result);
        return writer;
    }

    /// <summary>vmangos GossipDef.cpp SendTalking: the text sent for an unknown npc_text id.</summary>
    public const string DefaultGreeting = "Greetings $N";

    /// <summary>vmangos mangos_string LANG_NPC_TAINER_HELLO (the trainer greeting when no npc_trainer_greeting row exists).</summary>
    public const string TrainerHello = "Hello! Ready for some training?";

    /// <summary>
    /// SMSG_GOSSIP_MESSAGE (vmangos PlayerMenu::SendGossipMenu, build &gt; 1.5.1): u64 guid,
    /// u32 text id, u32 option count, per option u32 index, u8 icon, u8 coded, CString message;
    /// u32 quest count, per quest u32 id, u32 icon, u32 level, CString title. gtker agrees.
    /// </summary>
    public static PacketWriter GossipMessage(ObjectGuid npc, uint textId, IReadOnlyList<GossipMenuItem> options, IReadOnlyList<(Quest Quest, DialogStatus Icon)> quests)
    {
        var w = new PacketWriter(64 + (options.Count * 40) + (quests.Count * 48));
        w.WriteUInt64(npc.Value);
        w.WriteUInt32(textId);
        w.WriteUInt32((uint)options.Count);
        for (int i = 0; i < options.Count; i++)
        {
            w.WriteUInt32((uint)i);
            w.WriteByte(options[i].Icon);
            w.WriteByte(options[i].Coded ? (byte)1 : (byte)0);
            w.WriteCString(options[i].Message);
        }

        w.WriteUInt32((uint)quests.Count);
        foreach ((Quest quest, DialogStatus icon) in quests)
        {
            w.WriteUInt32(quest.Id);
            w.WriteUInt32((uint)icon);
            w.WriteInt32(quest.QuestLevel);
            w.WriteCString(quest.Title);
        }

        return w;
    }

    /// <summary>
    /// SMSG_NPC_TEXT_UPDATE (vmangos PlayerMenu::SendTalking + Query.cpp NpcTextUpdate): u32 id,
    /// then 8 × (f32 probability, CString male text, CString female text, u32 language,
    /// 3 × (u32 delay, u32 emote)). Unknown id: every variant is probability 0 with
    /// "Greetings $N". An empty male/female text falls back to the other one (vmangos).
    /// </summary>
    public static PacketWriter NpcTextUpdate(uint textId, NpcText? text)
    {
        var w = new PacketWriter(512);
        w.WriteUInt32(textId);
        for (int i = 0; i < 8; i++)
        {
            NpcTextOption? o = text is not null && i < text.Options.Count ? text.Options[i] : null;
            if (o is null)
            {
                w.WriteSingle(0);
                w.WriteCString(DefaultGreeting);
                w.WriteCString(DefaultGreeting);
                for (int k = 0; k < 7; k++)
                {
                    w.WriteUInt32(0);
                }

                continue;
            }

            w.WriteSingle(o.Probability);
            w.WriteCString(o.Text0.Length == 0 ? o.Text1 : o.Text0);
            w.WriteCString(o.Text1.Length == 0 ? o.Text0 : o.Text1);
            w.WriteUInt32(o.Language);
            w.WriteUInt32(o.EmoteDelay0);
            w.WriteUInt32(o.Emote0);
            w.WriteUInt32(o.EmoteDelay1);
            w.WriteUInt32(o.Emote1);
            w.WriteUInt32(o.EmoteDelay2);
            w.WriteUInt32(o.Emote2);
        }

        return w;
    }

    /// <summary>SMSG_GOSSIP_POI: u32 flags, f32 x, f32 y, u32 icon, u32 data, CString name (vmangos PlayerMenu::SendPointOfInterest).</summary>
    public static PacketWriter GossipPoi(PointOfInterest poi)
    {
        var w = new PacketWriter(32 + poi.IconName.Length);
        w.WriteUInt32(poi.Flags);
        w.WriteSingle(poi.X);
        w.WriteSingle(poi.Y);
        w.WriteUInt32(poi.Icon);
        w.WriteUInt32(poi.Data);
        w.WriteCString(poi.IconName);
        return w;
    }

    /// <summary>
    /// SMSG_TRAINER_LIST (vmangos SendTrainerList + SendTrainerSpellHelper): u64 guid, u32 trainer
    /// type, u32 count, per spell u32 spell, u8 state (GREEN_DISABLED sent as GREEN), u32 cost,
    /// u32 can-learn-primary-profession, u32 primary-profession-first-rank, u8 level, u32 skill,
    /// u32 skill value, u32 chain node 1, u32 chain node 2, u32 0; then CString greeting.
    /// gtker smsg_trainer_list agrees.
    /// </summary>
    public static PacketWriter TrainerList(ObjectGuid npc, TrainerType type, IReadOnlyList<TrainerListEntry> spells, string greeting)
    {
        var w = new PacketWriter(32 + (spells.Count * 38) + greeting.Length);
        w.WriteUInt64(npc.Value);
        w.WriteUInt32((uint)type);
        w.WriteUInt32((uint)spells.Count);
        foreach (TrainerListEntry s in spells)
        {
            w.WriteUInt32(s.Spell);
            w.WriteByte(s.State == TrainerSpellState.GreenDisabled ? (byte)TrainerSpellState.Green : (byte)s.State);
            w.WriteUInt32(s.Cost);
            w.WriteUInt32(s.PrimaryProfFirstRank && s.CanLearnPrimaryProf ? 1u : 0u);
            w.WriteUInt32(s.PrimaryProfFirstRank ? 1u : 0u);
            w.WriteByte(s.ReqLevel);
            w.WriteUInt32(s.ReqSkill);
            w.WriteUInt32(s.ReqSkillValue);
            w.WriteUInt32(s.ChainNode1);
            w.WriteUInt32(s.ChainNode2);
            w.WriteUInt32(0);
        }

        w.WriteCString(greeting);
        return w;
    }

    /// <summary>SMSG_TRAINER_BUY_SUCCEEDED: u64 guid, u32 spell (vmangos TrainerBuySucceeded).</summary>
    public static PacketWriter TrainerBuySucceeded(ObjectGuid npc, uint spell)
    {
        var w = new PacketWriter(12);
        w.WriteUInt64(npc.Value);
        w.WriteUInt32(spell);
        return w;
    }

    /// <summary>SMSG_TRAINER_BUY_FAILED: u64 guid, u32 spell, u32 error (TRAIN_FAIL_UNAVAILABLE 0, NOT_ENOUGH_MONEY 1, NOT_ENOUGH_SKILL 2).</summary>
    public static PacketWriter TrainerBuyFailed(ObjectGuid npc, uint spell, uint error)
    {
        var w = new PacketWriter(16);
        w.WriteUInt64(npc.Value);
        w.WriteUInt32(spell);
        w.WriteUInt32(error);
        return w;
    }

    /// <summary>
    /// SMSG_LIST_INVENTORY (vmangos WorldSession::SendListInventory): u64 guid, u8 count, per item
    /// u32 index (from 1), u32 item, u32 display, u32 count left (0xFFFFFFFF unlimited), u32 price,
    /// u32 max durability, u32 buy count. An empty list is u64 guid, u8 0, u8 0 (vmangos sends the
    /// extra error byte, 0 = "vendor has no inventory").
    /// </summary>
    /// <remarks>gtker smsg_list_inventory names the sixth field "durability"; vmangos sends MaxDurability.</remarks>
    public static PacketWriter ListInventory(ObjectGuid npc, IReadOnlyList<VendorListEntry> items)
    {
        var w = new PacketWriter(16 + (items.Count * 28));
        w.WriteUInt64(npc.Value);
        w.WriteByte((byte)items.Count);
        if (items.Count == 0)
        {
            w.WriteByte(0);
            return w;
        }

        foreach (VendorListEntry e in items)
        {
            w.WriteUInt32(e.Index);
            w.WriteUInt32(e.Item);
            w.WriteUInt32(e.DisplayId);
            w.WriteUInt32(e.MaxCount);
            w.WriteUInt32(e.Price);
            w.WriteUInt32(e.MaxDurability);
            w.WriteUInt32(e.BuyCount);
        }

        return w;
    }

    /// <summary>SMSG_BUY_ITEM: u64 vendor, u32 vendor slot (from 1), u32 new count (0xFFFFFFFF unlimited), u32 count (vmangos BuyItem).</summary>
    public static PacketWriter BuyItem(ObjectGuid vendor, uint vendorSlot, uint newCount, uint count)
    {
        var w = new PacketWriter(20);
        w.WriteUInt64(vendor.Value);
        w.WriteUInt32(vendorSlot);
        w.WriteUInt32(newCount);
        w.WriteUInt32(count);
        return w;
    }

    /// <summary>SMSG_BUY_FAILED: u64 vendor, u32 item, u8 reason (vmangos Player::SendBuyError).</summary>
    public static PacketWriter BuyFailed(ObjectGuid vendor, uint item, BuyResult reason)
    {
        var w = new PacketWriter(13);
        w.WriteUInt64(vendor.Value);
        w.WriteUInt32(item);
        w.WriteByte((byte)reason);
        return w;
    }

    /// <summary>SMSG_SELL_ITEM: u64 vendor (0 when not found), u64 item, u8 reason (vmangos Player::SendSellError).</summary>
    public static PacketWriter SellFailed(ObjectGuid vendor, ObjectGuid item, SellResult reason)
    {
        var w = new PacketWriter(17);
        w.WriteUInt64(vendor.Value);
        w.WriteUInt64(item.Value);
        w.WriteByte((byte)reason);
        return w;
    }

    /// <summary>A body of one u64 GUID (SMSG_BINDER_CONFIRM, vmangos Player::SetBindPoint).</summary>
    public static PacketWriter Guid(ObjectGuid guid)
    {
        var w = new PacketWriter(8);
        w.WriteUInt64(guid.Value);
        return w;
    }

    /// <summary>SMSG_BINDPOINTUPDATE: f32 x, f32 y, f32 z, u32 map, u32 area (vmangos Spell::EffectBind / Misc.cpp BindpointUpdate).</summary>
    public static PacketWriter BindPointUpdate(float x, float y, float z, uint map, uint area)
    {
        var w = new PacketWriter(20);
        w.WriteSingle(x);
        w.WriteSingle(y);
        w.WriteSingle(z);
        w.WriteUInt32(map);
        w.WriteUInt32(area);
        return w;
    }

    /// <summary>SMSG_PLAYERBOUND: u64 binder, u32 area (vmangos Spell::EffectBind / Misc.cpp PlayerBound).</summary>
    public static PacketWriter PlayerBound(ObjectGuid binder, uint area)
    {
        var w = new PacketWriter(12);
        w.WriteUInt64(binder.Value);
        w.WriteUInt32(area);
        return w;
    }

    /// <summary>SMSG_TAXINODE_STATUS: u64 guid, u8 known (vmangos Taxi.cpp TaxiNodeStatus, bool written as one byte).</summary>
    public static PacketWriter TaxiNodeStatus(ObjectGuid npc, bool known)
    {
        var w = new PacketWriter(9);
        w.WriteUInt64(npc.Value);
        w.WriteByte(known ? (byte)1 : (byte)0);
        return w;
    }

    /// <summary>
    /// SMSG_SHOWTAXINODES: u32 1 (show UI), u64 guid, u32 current node, u32[8] known mask
    /// (vmangos Taxi.cpp ShowTaxiNodes). gtker smsg_showtaxinodes declares the mask as an
    /// unsized u32 array; the 1.12 client reads eight words, as vmangos sends.
    /// </summary>
    public static PacketWriter ShowTaxiNodes(ObjectGuid npc, uint currentNode, IReadOnlyList<uint> mask)
    {
        var w = new PacketWriter(16 + (NpcStore.TaxiMaskSize * 4));
        w.WriteUInt32(1);
        w.WriteUInt64(npc.Value);
        w.WriteUInt32(currentNode);
        for (int i = 0; i < NpcStore.TaxiMaskSize; i++)
        {
            w.WriteUInt32(i < mask.Count ? mask[i] : 0);
        }

        return w;
    }

    /// <summary>SMSG_ACTIVATETAXIREPLY: u32 reply (vmangos Taxi.cpp ActivateTaxiReply, ActivateTaxiReplies).</summary>
    public static PacketWriter ActivateTaxiReply(ActivateTaxiReply reply)
    {
        var w = new PacketWriter(4);
        w.WriteUInt32((uint)reply);
        return w;
    }
}

/// <summary>vmangos BuyResult (SMSG_BUY_FAILED reason).</summary>
public enum BuyResult : byte
{
    CantFindItem = 0,
    ItemAlreadySold = 1,
    NotEnoughMoney = 2,
    SellerDontLikeYou = 4,
    DistanceTooFar = 5,
    ItemSoldOut = 7,
    CantCarryMore = 8,
    RankRequire = 11,
    ReputationRequire = 12,
}

/// <summary>vmangos ActivateTaxiReplies (SMSG_ACTIVATETAXIREPLY).</summary>
public enum ActivateTaxiReply : uint
{
    Ok = 0,
    UnspecifiedServerError = 1,
    NoSuchPath = 2,
    NotEnoughMoney = 3,
    TooFarAway = 4,
    NoVendorNearby = 5,
    NotVisited = 6,
    PlayerBusy = 7,
    PlayerAlreadyMounted = 8,
    PlayerShapeshifted = 9,
    PlayerMoving = 10,
    SameNode = 11,
    NotStanding = 12,
}

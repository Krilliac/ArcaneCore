using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Items;

/// <summary>
/// Payloads of the small item replies. Layouts follow vmangos (the primary reference) where
/// gtker/wow_messages differs, and each difference is stated at the builder.
/// </summary>
public static class ItemMiscPackets
{
    /// <summary>
    /// SMSG_ITEM_NAME_QUERY_RESPONSE (vmangos Server/Packets/Item.cpp:153-157, wow_messages 1.12): u32 entry, cstring name.
    /// The inventory type that follows in 2.4.3+ is not sent for 1.12.
    /// </summary>
    public static byte[] ItemNameQueryResponse(uint entry, string name)
    {
        var writer = new PacketWriter(8 + name.Length);
        writer.WriteUInt32(entry);
        writer.WriteCString(name);
        return writer.ToArray();
    }

    /// <summary>The text vmangos sends for a page that does not exist (QueryHandler.cpp:274).</summary>
    public const string MissingPageText = "Item page missing.";

    /// <summary>
    /// SMSG_PAGE_TEXT_QUERY_RESPONSE (vmangos WorldPackets::Query::PageTextQueryResponse, QueryHandler.cpp:263-299; wow_messages
    /// smsg_page_text_query_response, all versions): u32 page id, cstring text, u32 next page id (0 ends the book).
    /// </summary>
    public static byte[] PageTextQueryResponse(uint pageId, string text, uint nextPageId)
    {
        ArgumentNullException.ThrowIfNull(text);
        var writer = new PacketWriter(9 + text.Length);
        writer.WriteUInt32(pageId);
        writer.WriteCString(text);
        writer.WriteUInt32(nextPageId);
        return writer.ToArray();
    }

    /// <summary>
    /// SMSG_READ_ITEM_OK: the item GUID written twice (vmangos Item.cpp:164-168). wow_messages
    /// smsg_read_item_ok lists the GUID once; vmangos is followed and the difference is recorded
    /// in docs/areas/items.md as awaiting a real-client capture.
    /// </summary>
    public static byte[] ReadItemOk(ObjectGuid item)
    {
        var writer = new PacketWriter(16);
        writer.WriteUInt64(item.Value);
        writer.WriteUInt64(item.Value);
        return writer.ToArray();
    }

    /// <summary>
    /// SMSG_READ_ITEM_FAILED: u64 guid, u8 reason (always 0 in vmangos: the field is never set), u64 guid
    /// (vmangos Item.cpp:176-181). wow_messages lists only the GUID.
    /// </summary>
    public static byte[] ReadItemFailed(ObjectGuid item)
    {
        var writer = new PacketWriter(17);
        writer.WriteUInt64(item.Value);
        writer.WriteByte(0);
        writer.WriteUInt64(item.Value);
        return writer.ToArray();
    }
}

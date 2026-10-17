namespace ArcaneCore.World.Playerbots.Scenarios;

/// <summary>
/// Inventory payloads of the scenario harness (kept apart from <see cref="ScenarioPackets"/>), in the layouts the server's item handlers parse
/// (gtker/wow_messages 1.12).
/// </summary>
public static class ScenarioItemWire
{
    /// <summary>CMSG_WRAP_ITEM: u8 gift bag, u8 gift slot, u8 item bag, u8 item slot (cmsg_wrap_item).</summary>
    public static byte[] Wrap(byte giftBag, byte giftSlot, byte itemBag, byte itemSlot) => [giftBag, giftSlot, itemBag, itemSlot];

    /// <summary>CMSG_OPEN_ITEM: u8 bag, u8 slot (cmsg_open_item).</summary>
    public static byte[] Open(byte bag, byte slot) => [bag, slot];

    /// <summary>CMSG_READ_ITEM: u8 bag, u8 slot (cmsg_read_item).</summary>
    public static byte[] Read(byte bag, byte slot) => [bag, slot];

    /// <summary>CMSG_PAGE_TEXT_QUERY: u32 page id (cmsg_page_text_query, 1.12).</summary>
    public static byte[] PageTextQuery(uint pageId) => BitConverter.IsLittleEndian
        ? BitConverter.GetBytes(pageId)
        : [(byte)pageId, (byte)(pageId >> 8), (byte)(pageId >> 16), (byte)(pageId >> 24)];
}

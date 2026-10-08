using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_AUCTION_LIST_ITEMS: VanillaClientMessage, IWorldMessage {
    public required ulong Auctioneer { get; set; }
    public required uint ListStartItem { get; set; }
    public required string SearchedName { get; set; }
    public required byte MinimumLevel { get; set; }
    public required byte MaximumLevel { get; set; }
    public required uint AuctionSlotId { get; set; }
    public required uint AuctionMainCategory { get; set; }
    public required uint AuctionSubCategory { get; set; }
    public required Vanilla.ItemQuality AuctionQuality { get; set; }
    public required byte Usable { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Auctioneer, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(ListStartItem, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(SearchedName, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(MinimumLevel, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(MaximumLevel, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(AuctionSlotId, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(AuctionMainCategory, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(AuctionSubCategory, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)AuctionQuality, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(Usable, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 600, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 600, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_AUCTION_LIST_ITEMS> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var auctioneer = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var listStartItem = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var searchedName = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        var minimumLevel = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var maximumLevel = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var auctionSlotId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var auctionMainCategory = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var auctionSubCategory = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var auctionQuality = (Vanilla.ItemQuality)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var usable = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        return new CMSG_AUCTION_LIST_ITEMS {
            Auctioneer = auctioneer,
            ListStartItem = listStartItem,
            SearchedName = searchedName,
            MinimumLevel = minimumLevel,
            MaximumLevel = maximumLevel,
            AuctionSlotId = auctionSlotId,
            AuctionMainCategory = auctionMainCategory,
            AuctionSubCategory = auctionSubCategory,
            AuctionQuality = auctionQuality,
            Usable = usable,
        };
    }

    internal int Size() {
        var size = 0;

        // auctioneer: Generator.Generated.DataTypeGuid
        size += 8;

        // list_start_item: Generator.Generated.DataTypeInteger
        size += 4;

        // searched_name: Generator.Generated.DataTypeCstring
        size += SearchedName.Length + 1;

        // minimum_level: Generator.Generated.DataTypeInteger
        size += 1;

        // maximum_level: Generator.Generated.DataTypeInteger
        size += 1;

        // auction_slot_id: Generator.Generated.DataTypeInteger
        size += 4;

        // auction_main_category: Generator.Generated.DataTypeInteger
        size += 4;

        // auction_sub_category: Generator.Generated.DataTypeInteger
        size += 4;

        // auction_quality: Generator.Generated.DataTypeEnum
        size += 4;

        // usable: Generator.Generated.DataTypeInteger
        size += 1;

        return size;
    }

}


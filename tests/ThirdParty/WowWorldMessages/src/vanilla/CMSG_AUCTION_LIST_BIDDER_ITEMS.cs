using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_AUCTION_LIST_BIDDER_ITEMS: VanillaClientMessage, IWorldMessage {
    public required ulong Auctioneer { get; set; }
    public required uint StartFromPage { get; set; }
    public required List<uint> OutbidItemIds { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Auctioneer, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(StartFromPage, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)OutbidItemIds.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in OutbidItemIds) {
            await w.WriteUInt(v, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 612, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 612, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_AUCTION_LIST_BIDDER_ITEMS> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var auctioneer = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var startFromPage = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        // ReSharper disable once UnusedVariable.Compiler
        var amountOfOutbidItems = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var outbidItemIds = new List<uint>();
        for (var i = 0; i < amountOfOutbidItems; ++i) {
            outbidItemIds.Add(await r.ReadUInt(cancellationToken).ConfigureAwait(false));
        }

        return new CMSG_AUCTION_LIST_BIDDER_ITEMS {
            Auctioneer = auctioneer,
            StartFromPage = startFromPage,
            OutbidItemIds = outbidItemIds,
        };
    }

    internal int Size() {
        var size = 0;

        // auctioneer: Generator.Generated.DataTypeGuid
        size += 8;

        // start_from_page: Generator.Generated.DataTypeInteger
        size += 4;

        // amount_of_outbid_items: Generator.Generated.DataTypeInteger
        size += 4;

        // outbid_item_ids: Generator.Generated.DataTypeArray
        size += OutbidItemIds.Sum(e => 4);

        return size;
    }

}


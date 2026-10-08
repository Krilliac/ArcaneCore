using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_AUCTION_BIDDER_LIST_RESULT: VanillaServerMessage, IWorldMessage {
    public required List<AuctionListItem> Auctions { get; set; }
    public required uint TotalAmountOfAuctions { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt((uint)Auctions.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in Auctions) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

        await w.WriteUInt(TotalAmountOfAuctions, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 613, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 613, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_AUCTION_BIDDER_LIST_RESULT> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        // ReSharper disable once UnusedVariable.Compiler
        var count = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var auctions = new List<AuctionListItem>();
        for (var i = 0; i < count; ++i) {
            auctions.Add(await Vanilla.AuctionListItem.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
        }

        var totalAmountOfAuctions = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_AUCTION_BIDDER_LIST_RESULT {
            Auctions = auctions,
            TotalAmountOfAuctions = totalAmountOfAuctions,
        };
    }

    internal int Size() {
        var size = 0;

        // count: Generator.Generated.DataTypeInteger
        size += 4;

        // auctions: Generator.Generated.DataTypeArray
        size += Auctions.Sum(e => 64);

        // total_amount_of_auctions: Generator.Generated.DataTypeInteger
        size += 4;

        return size;
    }

}


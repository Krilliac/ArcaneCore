using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_AUCTION_OWNER_NOTIFICATION: VanillaServerMessage, IWorldMessage {
    public required uint AuctionId { get; set; }
    /// <summary>
    /// vmangos/cmangos/mangoszero: if 0, client shows ERR_AUCTION_EXPIRED_S, else ERR_AUCTION_SOLD_S (works only when guid==0)
    /// </summary>
    public required uint Bid { get; set; }
    public required uint AuctionOutBid { get; set; }
    public required ulong Bidder { get; set; }
    public required uint Item { get; set; }
    public required uint ItemRandomPropertyId { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(AuctionId, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Bid, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(AuctionOutBid, cancellationToken).ConfigureAwait(false);

        await w.WriteULong(Bidder, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Item, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(ItemRandomPropertyId, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 30, 607, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 30, 607, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_AUCTION_OWNER_NOTIFICATION> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var auctionId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var bid = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var auctionOutBid = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var bidder = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var item = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var itemRandomPropertyId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_AUCTION_OWNER_NOTIFICATION {
            AuctionId = auctionId,
            Bid = bid,
            AuctionOutBid = auctionOutBid,
            Bidder = bidder,
            Item = item,
            ItemRandomPropertyId = itemRandomPropertyId,
        };
    }

}


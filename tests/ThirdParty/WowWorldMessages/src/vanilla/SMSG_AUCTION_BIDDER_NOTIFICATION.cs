using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_AUCTION_BIDDER_NOTIFICATION: VanillaServerMessage, IWorldMessage {
    public required Vanilla.AuctionHouse AuctionHouse { get; set; }
    public required uint AuctionId { get; set; }
    public required ulong Bidder { get; set; }
    /// <summary>
    /// vmangos/cmangos: if 0, client shows ERR_AUCTION_WON_S, else ERR_AUCTION_OUTBID_S
    /// </summary>
    public required uint Won { get; set; }
    public required uint OutBid { get; set; }
    public required uint ItemTemplate { get; set; }
    public required uint ItemRandomPropertyId { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt((uint)AuctionHouse, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(AuctionId, cancellationToken).ConfigureAwait(false);

        await w.WriteULong(Bidder, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Won, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(OutBid, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(ItemTemplate, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(ItemRandomPropertyId, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 34, 606, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 34, 606, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_AUCTION_BIDDER_NOTIFICATION> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var auctionHouse = (Vanilla.AuctionHouse)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var auctionId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var bidder = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var won = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var outBid = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var itemTemplate = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var itemRandomPropertyId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_AUCTION_BIDDER_NOTIFICATION {
            AuctionHouse = auctionHouse,
            AuctionId = auctionId,
            Bidder = bidder,
            Won = won,
            OutBid = outBid,
            ItemTemplate = itemTemplate,
            ItemRandomPropertyId = itemRandomPropertyId,
        };
    }

}


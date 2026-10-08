using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_AUCTION_PLACE_BID: VanillaClientMessage, IWorldMessage {
    public required ulong Auctioneer { get; set; }
    public required uint AuctionId { get; set; }
    public required uint Price { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Auctioneer, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(AuctionId, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Price, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, 20, 602, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, 20, 602, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_AUCTION_PLACE_BID> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var auctioneer = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var auctionId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var price = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new CMSG_AUCTION_PLACE_BID {
            Auctioneer = auctioneer,
            AuctionId = auctionId,
            Price = price,
        };
    }

}


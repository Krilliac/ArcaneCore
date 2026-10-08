using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_LOOT_ROLL: VanillaClientMessage, IWorldMessage {
    public required ulong Item { get; set; }
    public required uint ItemSlot { get; set; }
    public required Vanilla.RollVote Vote { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Item, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(ItemSlot, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)Vote, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, 17, 672, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, 17, 672, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_LOOT_ROLL> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var item = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var itemSlot = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var vote = (Vanilla.RollVote)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        return new CMSG_LOOT_ROLL {
            Item = item,
            ItemSlot = itemSlot,
            Vote = vote,
        };
    }

}


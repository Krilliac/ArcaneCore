using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_REPAIR_ITEM: VanillaClientMessage, IWorldMessage {
    public required ulong Npc { get; set; }
    public required ulong Item { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Npc, cancellationToken).ConfigureAwait(false);

        await w.WriteULong(Item, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, 20, 680, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, 20, 680, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_REPAIR_ITEM> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var npc = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var item = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        return new CMSG_REPAIR_ITEM {
            Npc = npc,
            Item = item,
        };
    }

}


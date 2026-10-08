using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_QUESTLOG_SWAP_QUEST: VanillaClientMessage, IWorldMessage {
    public required byte Slot1 { get; set; }
    public required byte Slot2 { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteByte(Slot1, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(Slot2, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, 6, 403, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, 6, 403, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_QUESTLOG_SWAP_QUEST> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var slot1 = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var slot2 = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        return new CMSG_QUESTLOG_SWAP_QUEST {
            Slot1 = slot1,
            Slot2 = slot2,
        };
    }

}


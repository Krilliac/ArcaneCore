using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_SETSHEATHED: VanillaClientMessage, IWorldMessage {
    public required Vanilla.SheathState Sheathed { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt((uint)Sheathed, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, 8, 480, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, 8, 480, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_SETSHEATHED> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var sheathed = (Vanilla.SheathState)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new CMSG_SETSHEATHED {
            Sheathed = sheathed,
        };
    }

}


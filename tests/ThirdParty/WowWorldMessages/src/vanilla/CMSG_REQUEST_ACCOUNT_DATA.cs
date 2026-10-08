using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_REQUEST_ACCOUNT_DATA: VanillaClientMessage, IWorldMessage {
    /// <summary>
    /// The type of account data being requested. You can check this against the [CacheMask] to know if this is character-specific data or account-wide data.
    /// </summary>
    public required uint DataType { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(DataType, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, 8, 522, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, 8, 522, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_REQUEST_ACCOUNT_DATA> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var dataType = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new CMSG_REQUEST_ACCOUNT_DATA {
            DataType = dataType,
        };
    }

}


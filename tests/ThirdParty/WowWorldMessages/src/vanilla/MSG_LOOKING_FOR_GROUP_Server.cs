using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class MSG_LOOKING_FOR_GROUP_Server: VanillaServerMessage, IWorldMessage {
    /// <summary>
    /// vmangos sets to 0. cmangos/mangoszero don't implement
    /// </summary>
    public required uint Unknown1 { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(Unknown1, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 6, 511, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 6, 511, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<MSG_LOOKING_FOR_GROUP_Server> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var unknown1 = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new MSG_LOOKING_FOR_GROUP_Server {
            Unknown1 = unknown1,
        };
    }

}


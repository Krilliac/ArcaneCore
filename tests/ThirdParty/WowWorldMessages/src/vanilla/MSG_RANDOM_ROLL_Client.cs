using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class MSG_RANDOM_ROLL_Client: VanillaClientMessage, IWorldMessage {
    public required uint Minimum { get; set; }
    public required uint Maximum { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(Minimum, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Maximum, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, 12, 507, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, 12, 507, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<MSG_RANDOM_ROLL_Client> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var minimum = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var maximum = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new MSG_RANDOM_ROLL_Client {
            Minimum = minimum,
            Maximum = maximum,
        };
    }

}


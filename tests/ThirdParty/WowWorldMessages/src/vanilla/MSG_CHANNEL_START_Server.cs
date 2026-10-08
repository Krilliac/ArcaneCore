using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class MSG_CHANNEL_START_Server: VanillaServerMessage, IWorldMessage {
    public required uint Spell { get; set; }
    public required uint Duration { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(Spell, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Duration, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 10, 313, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 10, 313, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<MSG_CHANNEL_START_Server> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var spell = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var duration = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new MSG_CHANNEL_START_Server {
            Spell = spell,
            Duration = duration,
        };
    }

}


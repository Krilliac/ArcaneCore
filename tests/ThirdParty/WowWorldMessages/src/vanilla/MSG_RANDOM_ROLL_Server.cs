using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class MSG_RANDOM_ROLL_Server: VanillaServerMessage, IWorldMessage {
    public required uint Minimum { get; set; }
    public required uint Maximum { get; set; }
    public required uint ActualRoll { get; set; }
    public required ulong Guid { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(Minimum, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Maximum, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(ActualRoll, cancellationToken).ConfigureAwait(false);

        await w.WriteULong(Guid, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 22, 507, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 22, 507, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<MSG_RANDOM_ROLL_Server> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var minimum = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var maximum = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var actualRoll = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var guid = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        return new MSG_RANDOM_ROLL_Server {
            Minimum = minimum,
            Maximum = maximum,
            ActualRoll = actualRoll,
            Guid = guid,
        };
    }

}


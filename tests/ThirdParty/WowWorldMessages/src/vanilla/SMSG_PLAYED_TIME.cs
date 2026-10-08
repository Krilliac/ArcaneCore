using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_PLAYED_TIME: VanillaServerMessage, IWorldMessage {
    public required uint TotalPlayedTime { get; set; }
    public required uint LevelPlayedTime { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(TotalPlayedTime, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(LevelPlayedTime, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 10, 461, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 10, 461, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_PLAYED_TIME> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var totalPlayedTime = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var levelPlayedTime = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_PLAYED_TIME {
            TotalPlayedTime = totalPlayedTime,
            LevelPlayedTime = levelPlayedTime,
        };
    }

}


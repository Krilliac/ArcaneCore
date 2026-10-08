using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_AREA_SPIRIT_HEALER_TIME: VanillaServerMessage, IWorldMessage {
    public required ulong Guid { get; set; }
    public required uint NextResurrectTime { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Guid, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(NextResurrectTime, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 14, 740, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 14, 740, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_AREA_SPIRIT_HEALER_TIME> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var guid = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var nextResurrectTime = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_AREA_SPIRIT_HEALER_TIME {
            Guid = guid,
            NextResurrectTime = nextResurrectTime,
        };
    }

}


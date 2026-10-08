using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_SPLINE_SET_RUN_BACK_SPEED: VanillaServerMessage, IWorldMessage {
    public required ulong Guid { get; set; }
    public required float Speed { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WritePackedGuid(Guid, cancellationToken).ConfigureAwait(false);

        await w.WriteFloat(Speed, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 767, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 767, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_SPLINE_SET_RUN_BACK_SPEED> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var guid = await r.ReadPackedGuid(cancellationToken).ConfigureAwait(false);

        var speed = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

        return new SMSG_SPLINE_SET_RUN_BACK_SPEED {
            Guid = guid,
            Speed = speed,
        };
    }

    internal int Size() {
        var size = 0;

        // guid: Generator.Generated.DataTypePackedGuid
        size += Guid.PackedGuidLength();

        // speed: Generator.Generated.DataTypeFloatingPoint
        size += 4;

        return size;
    }

}


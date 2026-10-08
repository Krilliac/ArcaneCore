using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_SPLINE_MOVE_SET_HOVER: VanillaServerMessage, IWorldMessage {
    public required ulong Guid { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WritePackedGuid(Guid, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 775, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 775, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_SPLINE_MOVE_SET_HOVER> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var guid = await r.ReadPackedGuid(cancellationToken).ConfigureAwait(false);

        return new SMSG_SPLINE_MOVE_SET_HOVER {
            Guid = guid,
        };
    }

    internal int Size() {
        var size = 0;

        // guid: Generator.Generated.DataTypePackedGuid
        size += Guid.PackedGuidLength();

        return size;
    }

}


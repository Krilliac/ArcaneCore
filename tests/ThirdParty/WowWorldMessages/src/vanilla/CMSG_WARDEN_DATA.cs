using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_WARDEN_DATA: VanillaClientMessage, IWorldMessage {
    public required List<byte> EncryptedData { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        foreach (var v in EncryptedData) {
            await w.WriteByte(v, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 743, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 743, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_WARDEN_DATA> ReadBodyAsync(Stream r, uint bodySize, CancellationToken cancellationToken = default) {
        // ReSharper disable once InconsistentNaming
        var __size = 0;
        var encryptedData = new List<byte>();
        while (__size < bodySize) {
            encryptedData.Add(await r.ReadByte(cancellationToken).ConfigureAwait(false));
            __size += 1;
        }

        return new CMSG_WARDEN_DATA {
            EncryptedData = encryptedData,
        };
    }

    internal int Size() {
        var size = 0;

        // encrypted_data: Generator.Generated.DataTypeArray
        size += EncryptedData.Sum(e => 1);

        return size;
    }

}


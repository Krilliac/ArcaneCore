using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class MSG_PETITION_RENAME: VanillaClientMessage, VanillaServerMessage, IWorldMessage {
    public required ulong Petition { get; set; }
    public required string NewName { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Petition, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(NewName, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 705, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 705, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 705, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 705, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<MSG_PETITION_RENAME> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var petition = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var newName = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        return new MSG_PETITION_RENAME {
            Petition = petition,
            NewName = newName,
        };
    }

    internal int Size() {
        var size = 0;

        // petition: Generator.Generated.DataTypeGuid
        size += 8;

        // new_name: Generator.Generated.DataTypeCstring
        size += NewName.Length + 1;

        return size;
    }

}


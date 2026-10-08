using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_CHAR_RENAME: VanillaClientMessage, IWorldMessage {
    public required ulong Character { get; set; }
    public required string NewName { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Character, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(NewName, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 711, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 711, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_CHAR_RENAME> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var character = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var newName = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        return new CMSG_CHAR_RENAME {
            Character = character,
            NewName = newName,
        };
    }

    internal int Size() {
        var size = 0;

        // character: Generator.Generated.DataTypeGuid
        size += 8;

        // new_name: Generator.Generated.DataTypeCstring
        size += NewName.Length + 1;

        return size;
    }

}


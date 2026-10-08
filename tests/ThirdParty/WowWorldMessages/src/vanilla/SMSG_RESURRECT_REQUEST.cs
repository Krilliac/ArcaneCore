using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_RESURRECT_REQUEST: VanillaServerMessage, IWorldMessage {
    public required ulong Guid { get; set; }
    public required string Name { get; set; }
    public required bool Player { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Guid, cancellationToken).ConfigureAwait(false);

        await w.WriteSizedCString(Name, cancellationToken).ConfigureAwait(false);

        await w.WriteBool8(Player, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 347, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 347, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_RESURRECT_REQUEST> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var guid = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var name = await r.ReadSizedCString(cancellationToken).ConfigureAwait(false);

        var player = await r.ReadBool8(cancellationToken).ConfigureAwait(false);

        return new SMSG_RESURRECT_REQUEST {
            Guid = guid,
            Name = name,
            Player = player,
        };
    }

    internal int Size() {
        var size = 0;

        // guid: Generator.Generated.DataTypeGuid
        size += 8;

        // name: Generator.Generated.DataTypeSizedCstring
        size += Name.Length + 5;

        // player: Generator.Generated.DataTypeBool
        size += 1;

        return size;
    }

}


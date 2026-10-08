using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_GROUP_SET_LEADER: VanillaServerMessage, IWorldMessage {
    public required string Name { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteCString(Name, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 121, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 121, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_GROUP_SET_LEADER> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var name = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        return new SMSG_GROUP_SET_LEADER {
            Name = name,
        };
    }

    internal int Size() {
        var size = 0;

        // name: Generator.Generated.DataTypeCstring
        size += Name.Length + 1;

        return size;
    }

}


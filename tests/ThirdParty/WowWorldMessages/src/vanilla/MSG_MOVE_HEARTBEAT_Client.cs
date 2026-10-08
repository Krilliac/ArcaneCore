using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class MSG_MOVE_HEARTBEAT_Client: VanillaClientMessage, IWorldMessage {
    public required MovementInfo Info { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await Info.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 238, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 238, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<MSG_MOVE_HEARTBEAT_Client> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var info = await MovementInfo.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false);

        return new MSG_MOVE_HEARTBEAT_Client {
            Info = info,
        };
    }

    internal int Size() {
        var size = 0;

        // info: Generator.Generated.DataTypeStruct
        size += Info.Size();

        return size;
    }

}


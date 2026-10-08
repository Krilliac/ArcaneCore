using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_ACTION_BUTTONS: VanillaServerMessage, IWorldMessage {
    public const int DataLength = 120;
    public required uint[] Data { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        foreach (var v in Data) {
            await w.WriteUInt(v, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 482, 297, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 482, 297, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_ACTION_BUTTONS> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var data = new uint[DataLength];
        for (var i = 0; i < DataLength; ++i) {
            data[i] = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
        }

        return new SMSG_ACTION_BUTTONS {
            Data = data,
        };
    }

}


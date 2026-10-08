using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_CHANNEL_NOTIFY: VanillaServerMessage, IWorldMessage {
    public required Vanilla.ChatNotify NotifyType { get; set; }
    public required string ChannelName { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteByte((byte)NotifyType, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(ChannelName, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 153, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 153, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_CHANNEL_NOTIFY> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var notifyType = (Vanilla.ChatNotify)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var channelName = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        return new SMSG_CHANNEL_NOTIFY {
            NotifyType = notifyType,
            ChannelName = channelName,
        };
    }

    internal int Size() {
        var size = 0;

        // notify_type: Generator.Generated.DataTypeEnum
        size += 1;

        // channel_name: Generator.Generated.DataTypeCstring
        size += ChannelName.Length + 1;

        return size;
    }

}


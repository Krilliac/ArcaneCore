using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_JOIN_CHANNEL: VanillaClientMessage, IWorldMessage {
    public required string ChannelName { get; set; }
    public required string ChannelPassword { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteCString(ChannelName, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(ChannelPassword, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 151, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 151, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_JOIN_CHANNEL> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var channelName = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        var channelPassword = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        return new CMSG_JOIN_CHANNEL {
            ChannelName = channelName,
            ChannelPassword = channelPassword,
        };
    }

    internal int Size() {
        var size = 0;

        // channel_name: Generator.Generated.DataTypeCstring
        size += ChannelName.Length + 1;

        // channel_password: Generator.Generated.DataTypeCstring
        size += ChannelPassword.Length + 1;

        return size;
    }

}


using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_GUILD_MOTD: VanillaClientMessage, IWorldMessage {
    public required string MessageOfTheDay { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteCString(MessageOfTheDay, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 145, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 145, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_GUILD_MOTD> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var messageOfTheDay = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        return new CMSG_GUILD_MOTD {
            MessageOfTheDay = messageOfTheDay,
        };
    }

    internal int Size() {
        var size = 0;

        // message_of_the_day: Generator.Generated.DataTypeCstring
        size += MessageOfTheDay.Length + 1;

        return size;
    }

}


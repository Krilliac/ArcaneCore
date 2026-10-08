using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_LOGIN_SETTIMESPEED: VanillaServerMessage, IWorldMessage {
    /// <summary>
    /// Current server datetime.
    /// Running the client with `-console` verifies that this message in this format sets the correct datetime. [SMSG_QUERY_TIME_RESPONSE] will not set this.
    /// </summary>
    public required uint Datetime { get; set; }
    /// <summary>
    /// How many minutes should pass by every second.
    /// vmangos/cmangos/mangoszero set this to 0.01666667. This means that 1/60 minutes pass every second (one second passes every second). Setting this to 1.0 will make the client advance one minute every second.
    /// </summary>
    public required float Timescale { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(Datetime, cancellationToken).ConfigureAwait(false);

        await w.WriteFloat(Timescale, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 10, 66, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 10, 66, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_LOGIN_SETTIMESPEED> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var datetime = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var timescale = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

        return new SMSG_LOGIN_SETTIMESPEED {
            Datetime = datetime,
            Timescale = timescale,
        };
    }

}


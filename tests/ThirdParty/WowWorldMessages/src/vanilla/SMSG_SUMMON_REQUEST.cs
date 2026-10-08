using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_SUMMON_REQUEST: VanillaServerMessage, IWorldMessage {
    public required ulong Summoner { get; set; }
    public required Vanilla.Area Area { get; set; }
    public required uint AutoDeclineTime { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Summoner, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)Area, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(AutoDeclineTime, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 18, 683, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 18, 683, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_SUMMON_REQUEST> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var summoner = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var area = (Vanilla.Area)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var autoDeclineTime = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_SUMMON_REQUEST {
            Summoner = summoner,
            Area = area,
            AutoDeclineTime = autoDeclineTime,
        };
    }

}


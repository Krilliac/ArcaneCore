using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_RAID_GROUP_ONLY: VanillaServerMessage, IWorldMessage {
    public required uint HomebindTimer { get; set; }
    public required Vanilla.RaidGroupError Error { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(HomebindTimer, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)Error, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 10, 646, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 10, 646, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_RAID_GROUP_ONLY> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var homebindTimer = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var error = (Vanilla.RaidGroupError)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_RAID_GROUP_ONLY {
            HomebindTimer = homebindTimer,
            Error = error,
        };
    }

}


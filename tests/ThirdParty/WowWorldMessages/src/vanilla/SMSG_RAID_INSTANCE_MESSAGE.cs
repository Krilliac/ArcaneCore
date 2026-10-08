using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_RAID_INSTANCE_MESSAGE: VanillaServerMessage, IWorldMessage {
    public required Vanilla.RaidInstanceMessage MessageType { get; set; }
    public required Vanilla.Map Map { get; set; }
    public required uint TimeLeft { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt((uint)MessageType, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)Map, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(TimeLeft, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 14, 762, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 14, 762, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_RAID_INSTANCE_MESSAGE> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var messageType = (Vanilla.RaidInstanceMessage)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var map = (Vanilla.Map)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var timeLeft = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_RAID_INSTANCE_MESSAGE {
            MessageType = messageType,
            Map = map,
            TimeLeft = timeLeft,
        };
    }

}


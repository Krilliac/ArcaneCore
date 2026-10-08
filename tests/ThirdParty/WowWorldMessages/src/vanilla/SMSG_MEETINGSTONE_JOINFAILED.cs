using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_MEETINGSTONE_JOINFAILED: VanillaServerMessage, IWorldMessage {
    public required Vanilla.MeetingStoneFailure Reason { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteByte((byte)Reason, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 3, 699, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 3, 699, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_MEETINGSTONE_JOINFAILED> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var reason = (Vanilla.MeetingStoneFailure)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        return new SMSG_MEETINGSTONE_JOINFAILED {
            Reason = reason,
        };
    }

}


using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_START_MIRROR_TIMER: VanillaServerMessage, IWorldMessage {
    public required Vanilla.TimerType Timer { get; set; }
    public required uint TimeRemaining { get; set; }
    public required uint Duration { get; set; }
    public required uint Scale { get; set; }
    public required bool IsFrozen { get; set; }
    public required uint Id { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt((uint)Timer, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(TimeRemaining, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Duration, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Scale, cancellationToken).ConfigureAwait(false);

        await w.WriteBool8(IsFrozen, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Id, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 23, 473, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 23, 473, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_START_MIRROR_TIMER> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var timer = (Vanilla.TimerType)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var timeRemaining = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var duration = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var scale = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var isFrozen = await r.ReadBool8(cancellationToken).ConfigureAwait(false);

        var id = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_START_MIRROR_TIMER {
            Timer = timer,
            TimeRemaining = timeRemaining,
            Duration = duration,
            Scale = scale,
            IsFrozen = isFrozen,
            Id = id,
        };
    }

}


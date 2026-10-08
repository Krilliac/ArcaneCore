using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_WEATHER: VanillaServerMessage, IWorldMessage {
    public required Vanilla.WeatherType WeatherType { get; set; }
    public required float Grade { get; set; }
    public required uint SoundId { get; set; }
    public required Vanilla.WeatherChangeType Change { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt((uint)WeatherType, cancellationToken).ConfigureAwait(false);

        await w.WriteFloat(Grade, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(SoundId, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)Change, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 15, 756, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 15, 756, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_WEATHER> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var weatherType = (Vanilla.WeatherType)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var grade = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

        var soundId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var change = (Vanilla.WeatherChangeType)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        return new SMSG_WEATHER {
            WeatherType = weatherType,
            Grade = grade,
            SoundId = soundId,
            Change = change,
        };
    }

}


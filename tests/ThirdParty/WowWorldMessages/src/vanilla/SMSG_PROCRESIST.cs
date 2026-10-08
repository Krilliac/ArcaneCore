using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_PROCRESIST: VanillaServerMessage, IWorldMessage {
    public required ulong Caster { get; set; }
    public required ulong Target { get; set; }
    public required uint Id { get; set; }
    public required Vanilla.LogFormat LogFormat { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Caster, cancellationToken).ConfigureAwait(false);

        await w.WriteULong(Target, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Id, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)LogFormat, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 23, 608, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 23, 608, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_PROCRESIST> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var caster = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var target = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var id = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var logFormat = (Vanilla.LogFormat)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        return new SMSG_PROCRESIST {
            Caster = caster,
            Target = target,
            Id = id,
            LogFormat = logFormat,
        };
    }

}


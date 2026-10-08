using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_SET_PCT_SPELL_MODIFIER: VanillaServerMessage, IWorldMessage {
    public required byte Eff { get; set; }
    public required byte Op { get; set; }
    public required uint Value { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteByte(Eff, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(Op, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Value, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 8, 615, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 8, 615, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_SET_PCT_SPELL_MODIFIER> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var eff = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var op = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var value = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_SET_PCT_SPELL_MODIFIER {
            Eff = eff,
            Op = op,
            Value = value,
        };
    }

}


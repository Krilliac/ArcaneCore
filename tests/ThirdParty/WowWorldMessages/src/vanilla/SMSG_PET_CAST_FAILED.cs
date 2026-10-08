using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_PET_CAST_FAILED: VanillaServerMessage, IWorldMessage {
    public required uint Id { get; set; }
    /// <summary>
    /// vmangos sets to 2 and cmangos sets to 0.
    /// </summary>
    public required byte Unknown1 { get; set; }
    public required Vanilla.SpellCastResult Result { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(Id, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(Unknown1, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)Result, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 8, 312, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 8, 312, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_PET_CAST_FAILED> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var id = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var unknown1 = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var result = (Vanilla.SpellCastResult)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        return new SMSG_PET_CAST_FAILED {
            Id = id,
            Unknown1 = unknown1,
            Result = result,
        };
    }

}


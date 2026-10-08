using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_SUPERCEDED_SPELL: VanillaServerMessage, IWorldMessage {
    public required ushort NewSpellId { get; set; }
    public required ushort OldSpellId { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUShort(NewSpellId, cancellationToken).ConfigureAwait(false);

        await w.WriteUShort(OldSpellId, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 6, 300, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 6, 300, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_SUPERCEDED_SPELL> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var newSpellId = await r.ReadUShort(cancellationToken).ConfigureAwait(false);

        var oldSpellId = await r.ReadUShort(cancellationToken).ConfigureAwait(false);

        return new SMSG_SUPERCEDED_SPELL {
            NewSpellId = newSpellId,
            OldSpellId = oldSpellId,
        };
    }

}


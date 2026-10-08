using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_UPDATE_AURA_DURATION: VanillaServerMessage, IWorldMessage {
    public required byte AuraSlot { get; set; }
    public required uint AuraDuration { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteByte(AuraSlot, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(AuraDuration, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 7, 311, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 7, 311, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_UPDATE_AURA_DURATION> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var auraSlot = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var auraDuration = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_UPDATE_AURA_DURATION {
            AuraSlot = auraSlot,
            AuraDuration = auraDuration,
        };
    }

}


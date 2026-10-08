using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_BUY_BANK_SLOT_RESULT: VanillaServerMessage, IWorldMessage {
    public required Vanilla.BuyBankSlotResult Result { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt((uint)Result, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 6, 442, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 6, 442, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_BUY_BANK_SLOT_RESULT> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var result = (Vanilla.BuyBankSlotResult)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_BUY_BANK_SLOT_RESULT {
            Result = result,
        };
    }

}


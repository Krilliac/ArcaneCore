using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_PARTYKILLLOG: VanillaServerMessage, IWorldMessage {
    public required ulong PlayerWithKillingBlow { get; set; }
    public required ulong Victim { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(PlayerWithKillingBlow, cancellationToken).ConfigureAwait(false);

        await w.WriteULong(Victim, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 18, 501, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 18, 501, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_PARTYKILLLOG> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var playerWithKillingBlow = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var victim = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        return new SMSG_PARTYKILLLOG {
            PlayerWithKillingBlow = playerWithKillingBlow,
            Victim = victim,
        };
    }

}


using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_LOGOUT_RESPONSE: VanillaServerMessage, IWorldMessage {
    public required Vanilla.LogoutResult Result { get; set; }
    public required Vanilla.LogoutSpeed Speed { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt((uint)Result, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)Speed, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 7, 76, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 7, 76, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_LOGOUT_RESPONSE> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var result = (Vanilla.LogoutResult)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var speed = (Vanilla.LogoutSpeed)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        return new SMSG_LOGOUT_RESPONSE {
            Result = result,
            Speed = speed,
        };
    }

}


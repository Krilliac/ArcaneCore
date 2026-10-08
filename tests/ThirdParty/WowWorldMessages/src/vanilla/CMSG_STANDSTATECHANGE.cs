using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_STANDSTATECHANGE: VanillaClientMessage, IWorldMessage {
    public required Vanilla.UnitStandState AnimationState { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt((uint)AnimationState, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, 8, 257, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, 8, 257, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_STANDSTATECHANGE> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var animationState = (Vanilla.UnitStandState)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new CMSG_STANDSTATECHANGE {
            AnimationState = animationState,
        };
    }

}


// This async method lacks 'await' operators and will run synchronously. Consider using the 'await' operator to await non-blocking API calls, or 'await Task.Run(...)' to do CPU-bound work on a background thread.
// Empty reads will have an unnecessary async keyword
#pragma warning disable 1998
using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class MSG_LOOKING_FOR_GROUP_Client: VanillaClientMessage, IWorldMessage {

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, 4, 511, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, 4, 511, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<MSG_LOOKING_FOR_GROUP_Client> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        return new MSG_LOOKING_FOR_GROUP_Client {
        };
    }

}


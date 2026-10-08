// This async method lacks 'await' operators and will run synchronously. Consider using the 'await' operator to await non-blocking API calls, or 'await Task.Run(...)' to do CPU-bound work on a background thread.
// Empty reads will have an unnecessary async keyword
#pragma warning disable 1998
using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_TOGGLE_PVP: VanillaClientMessage, IWorldMessage {
    public struct OptionalSet {
        public required bool EnablePvp { get; set; }
    }
    public required OptionalSet? Set { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        if (Set is { } set) {
            await w.WriteBool8(set.EnablePvp, cancellationToken).ConfigureAwait(false);

        }
    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 595, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 595, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_TOGGLE_PVP> ReadBodyAsync(Stream r, uint bodySize, CancellationToken cancellationToken = default) {
        // ReSharper disable once InconsistentNaming
        var __size = 0;
        OptionalSet? optionalSet = null;
        if (__size < bodySize) {
            var enablePvp = await r.ReadBool8(cancellationToken).ConfigureAwait(false);
            __size += 1;

            optionalSet = new OptionalSet {
                EnablePvp = enablePvp,
            };
        }

        return new CMSG_TOGGLE_PVP {
            Set = optionalSet,
        };
    }

    internal int Size() {
        var size = 0;

        if (Set is { } set) {
            // enable_pvp: Generator.Generated.DataTypeBool
            size += 1;

        }
        return size;
    }

}


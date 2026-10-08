// This async method lacks 'await' operators and will run synchronously. Consider using the 'await' operator to await non-blocking API calls, or 'await Task.Run(...)' to do CPU-bound work on a background thread.
// Empty reads will have an unnecessary async keyword
#pragma warning disable 1998
using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class MSG_RAID_READY_CHECK_Client: VanillaClientMessage, IWorldMessage {
    public struct OptionalAnswer {
        public required byte State { get; set; }
    }
    public required OptionalAnswer? Answer { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        if (Answer is { } answer) {
            await w.WriteByte(answer.State, cancellationToken).ConfigureAwait(false);

        }
    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 802, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 802, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<MSG_RAID_READY_CHECK_Client> ReadBodyAsync(Stream r, uint bodySize, CancellationToken cancellationToken = default) {
        // ReSharper disable once InconsistentNaming
        var __size = 0;
        OptionalAnswer? optionalAnswer = null;
        if (__size < bodySize) {
            var state = await r.ReadByte(cancellationToken).ConfigureAwait(false);
            __size += 1;

            optionalAnswer = new OptionalAnswer {
                State = state,
            };
        }

        return new MSG_RAID_READY_CHECK_Client {
            Answer = optionalAnswer,
        };
    }

    internal int Size() {
        var size = 0;

        if (Answer is { } answer) {
            // state: Generator.Generated.DataTypeInteger
            size += 1;

        }
        return size;
    }

}


// This async method lacks 'await' operators and will run synchronously. Consider using the 'await' operator to await non-blocking API calls, or 'await Task.Run(...)' to do CPU-bound work on a background thread.
// Empty reads will have an unnecessary async keyword
#pragma warning disable 1998
using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class MSG_RAID_READY_CHECK_Server: VanillaServerMessage, IWorldMessage {
    public struct OptionalStateCheck {
        public required ulong Guid { get; set; }
        public required byte State { get; set; }
    }
    public required OptionalStateCheck? StateCheck { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        if (StateCheck is { } stateCheck) {
            await w.WriteULong(stateCheck.Guid, cancellationToken).ConfigureAwait(false);

            await w.WriteByte(stateCheck.State, cancellationToken).ConfigureAwait(false);

        }
    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 802, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 802, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<MSG_RAID_READY_CHECK_Server> ReadBodyAsync(Stream r, uint bodySize, CancellationToken cancellationToken = default) {
        // ReSharper disable once InconsistentNaming
        var __size = 0;
        OptionalStateCheck? optionalStateCheck = null;
        if (__size < bodySize) {
            var guid = await r.ReadULong(cancellationToken).ConfigureAwait(false);
            __size += 8;

            var state = await r.ReadByte(cancellationToken).ConfigureAwait(false);
            __size += 1;

            optionalStateCheck = new OptionalStateCheck {
                Guid = guid,
                State = state,
            };
        }

        return new MSG_RAID_READY_CHECK_Server {
            StateCheck = optionalStateCheck,
        };
    }

    internal int Size() {
        var size = 0;

        if (StateCheck is { } stateCheck) {
            // guid: Generator.Generated.DataTypeGuid
            size += 8;

            // state: Generator.Generated.DataTypeInteger
            size += 1;

        }
        return size;
    }

}


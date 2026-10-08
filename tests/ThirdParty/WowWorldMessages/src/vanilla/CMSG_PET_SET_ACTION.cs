using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_PET_SET_ACTION: VanillaClientMessage, IWorldMessage {
    public required ulong Guid { get; set; }
    public required uint Position1 { get; set; }
    public required uint Data1 { get; set; }
    public struct OptionalExtra {
        public required uint Position2 { get; set; }
        public required uint Data2 { get; set; }
    }
    public required OptionalExtra? Extra { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Guid, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Position1, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Data1, cancellationToken).ConfigureAwait(false);

        if (Extra is { } extra) {
            await w.WriteUInt(extra.Position2, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt(extra.Data2, cancellationToken).ConfigureAwait(false);

        }
    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 372, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 372, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_PET_SET_ACTION> ReadBodyAsync(Stream r, uint bodySize, CancellationToken cancellationToken = default) {
        // ReSharper disable once InconsistentNaming
        var __size = 0;
        var guid = await r.ReadULong(cancellationToken).ConfigureAwait(false);
        __size += 8;

        var position1 = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
        __size += 4;

        var data1 = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
        __size += 4;

        OptionalExtra? optionalExtra = null;
        if (__size < bodySize) {
            var position2 = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var data2 = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            optionalExtra = new OptionalExtra {
                Position2 = position2,
                Data2 = data2,
            };
        }

        return new CMSG_PET_SET_ACTION {
            Guid = guid,
            Position1 = position1,
            Data1 = data1,
            Extra = optionalExtra,
        };
    }

    internal int Size() {
        var size = 0;

        // guid: Generator.Generated.DataTypeGuid
        size += 8;

        // position1: Generator.Generated.DataTypeInteger
        size += 4;

        // data1: Generator.Generated.DataTypeInteger
        size += 4;

        if (Extra is { } extra) {
            // position2: Generator.Generated.DataTypeInteger
            size += 4;

            // data2: Generator.Generated.DataTypeInteger
            size += 4;

        }
        return size;
    }

}


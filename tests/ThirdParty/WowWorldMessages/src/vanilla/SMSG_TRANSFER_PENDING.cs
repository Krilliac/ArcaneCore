using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_TRANSFER_PENDING: VanillaServerMessage, IWorldMessage {
    public required Vanilla.Map Map { get; set; }
    public struct OptionalHasTransport {
        public required uint Transport { get; set; }
        public required Vanilla.Map TransportMap { get; set; }
    }
    public required OptionalHasTransport? HasTransport { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt((uint)Map, cancellationToken).ConfigureAwait(false);

        if (HasTransport is { } hasTransport) {
            await w.WriteUInt(hasTransport.Transport, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt((uint)hasTransport.TransportMap, cancellationToken).ConfigureAwait(false);

        }
    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 63, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 63, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_TRANSFER_PENDING> ReadBodyAsync(Stream r, uint bodySize, CancellationToken cancellationToken = default) {
        // ReSharper disable once InconsistentNaming
        var __size = 0;
        var map = (Vanilla.Map)await r.ReadUInt(cancellationToken).ConfigureAwait(false);
        __size += 4;

        OptionalHasTransport? optionalHasTransport = null;
        if (__size < bodySize) {
            var transport = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var transportMap = (Vanilla.Map)await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            optionalHasTransport = new OptionalHasTransport {
                Transport = transport,
                TransportMap = transportMap,
            };
        }

        return new SMSG_TRANSFER_PENDING {
            Map = map,
            HasTransport = optionalHasTransport,
        };
    }

    internal int Size() {
        var size = 0;

        // map: Generator.Generated.DataTypeEnum
        size += 4;

        if (HasTransport is { } hasTransport) {
            // transport: Generator.Generated.DataTypeInteger
            size += 4;

            // transport_map: Generator.Generated.DataTypeEnum
            size += 4;

        }
        return size;
    }

}


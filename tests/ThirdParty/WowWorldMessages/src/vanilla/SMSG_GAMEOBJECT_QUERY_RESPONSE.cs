using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_GAMEOBJECT_QUERY_RESPONSE: VanillaServerMessage, IWorldMessage {
    /// <summary>
    /// When the `found` optional is not present all emulators bitwise OR the entry with `0x80000000`.``
    /// </summary>
    public required uint EntryId { get; set; }
    public struct OptionalFound {
        public required uint InfoType { get; set; }
        public required uint DisplayId { get; set; }
        public required string Name1 { get; set; }
        public required string Name2 { get; set; }
        public required string Name3 { get; set; }
        public required string Name4 { get; set; }
        public required string Name5 { get; set; }
        public const int RawDataLength = 6;
        public required uint[] RawData { get; set; }
    }
    public required OptionalFound? Found { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(EntryId, cancellationToken).ConfigureAwait(false);

        if (Found is { } found) {
            await w.WriteUInt(found.InfoType, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt(found.DisplayId, cancellationToken).ConfigureAwait(false);

            await w.WriteCString(found.Name1, cancellationToken).ConfigureAwait(false);

            await w.WriteCString(found.Name2, cancellationToken).ConfigureAwait(false);

            await w.WriteCString(found.Name3, cancellationToken).ConfigureAwait(false);

            await w.WriteCString(found.Name4, cancellationToken).ConfigureAwait(false);

            await w.WriteCString(found.Name5, cancellationToken).ConfigureAwait(false);

            foreach (var v in found.RawData) {
                await w.WriteUInt(v, cancellationToken).ConfigureAwait(false);
            }

        }
    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 95, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 95, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_GAMEOBJECT_QUERY_RESPONSE> ReadBodyAsync(Stream r, uint bodySize, CancellationToken cancellationToken = default) {
        // ReSharper disable once InconsistentNaming
        var __size = 0;
        var entryId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
        __size += 4;

        OptionalFound? optionalFound = null;
        if (__size < bodySize) {
            var infoType = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var displayId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var name1 = await r.ReadCString(cancellationToken).ConfigureAwait(false);
            __size += name1.Length + 1;

            var name2 = await r.ReadCString(cancellationToken).ConfigureAwait(false);
            __size += name2.Length + 1;

            var name3 = await r.ReadCString(cancellationToken).ConfigureAwait(false);
            __size += name3.Length + 1;

            var name4 = await r.ReadCString(cancellationToken).ConfigureAwait(false);
            __size += name4.Length + 1;

            var name5 = await r.ReadCString(cancellationToken).ConfigureAwait(false);
            __size += name5.Length + 1;

            var rawData = new uint[OptionalFound.RawDataLength];
            for (var i = 0; i < OptionalFound.RawDataLength; ++i) {
                rawData[i] = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
                __size += 4;
            }

            optionalFound = new OptionalFound {
                InfoType = infoType,
                DisplayId = displayId,
                Name1 = name1,
                Name2 = name2,
                Name3 = name3,
                Name4 = name4,
                Name5 = name5,
                RawData = rawData,
            };
        }

        return new SMSG_GAMEOBJECT_QUERY_RESPONSE {
            EntryId = entryId,
            Found = optionalFound,
        };
    }

    internal int Size() {
        var size = 0;

        // entry_id: Generator.Generated.DataTypeInteger
        size += 4;

        if (Found is { } found) {
            // info_type: Generator.Generated.DataTypeInteger
            size += 4;

            // display_id: Generator.Generated.DataTypeInteger
            size += 4;

            // name1: Generator.Generated.DataTypeCstring
            size += found.Name1.Length + 1;

            // name2: Generator.Generated.DataTypeCstring
            size += found.Name2.Length + 1;

            // name3: Generator.Generated.DataTypeCstring
            size += found.Name3.Length + 1;

            // name4: Generator.Generated.DataTypeCstring
            size += found.Name4.Length + 1;

            // name5: Generator.Generated.DataTypeCstring
            size += found.Name5.Length + 1;

            // raw_data: Generator.Generated.DataTypeArray
            size += found.RawData.Sum(e => 4);

        }
        return size;
    }

}


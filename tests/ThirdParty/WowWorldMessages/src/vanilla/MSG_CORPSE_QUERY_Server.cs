using WowSrp.Header;
using WowWorldMessages.All;

namespace WowWorldMessages.Vanilla;

using CorpseQueryResultType = OneOf.OneOf<MSG_CORPSE_QUERY_Server.CorpseQueryResultFound, CorpseQueryResult>;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class MSG_CORPSE_QUERY_Server: VanillaServerMessage, IWorldMessage {
    public class CorpseQueryResultFound {
        public required Vanilla.Map CorpseMap { get; set; }
        public required Vanilla.Map Map { get; set; }
        public required Vector3d Position { get; set; }
    }
    public required CorpseQueryResultType Result { get; set; }
    internal CorpseQueryResult ResultValue => Result.Match(
        _ => Vanilla.CorpseQueryResult.Found,
        v => v
    );

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteByte((byte)ResultValue, cancellationToken).ConfigureAwait(false);

        if (Result.Value is MSG_CORPSE_QUERY_Server.CorpseQueryResultFound corpseQueryResultFound) {
            await w.WriteUInt((uint)corpseQueryResultFound.Map, cancellationToken).ConfigureAwait(false);

            await corpseQueryResultFound.Position.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt((uint)corpseQueryResultFound.CorpseMap, cancellationToken).ConfigureAwait(false);

        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 534, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 534, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<MSG_CORPSE_QUERY_Server> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        CorpseQueryResultType result = (Vanilla.CorpseQueryResult)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        if (result.Value is Vanilla.CorpseQueryResult.Found) {
            var map = (Vanilla.Map)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            var position = await Vector3d.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false);

            var corpseMap = (Vanilla.Map)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            result = new CorpseQueryResultFound {
                CorpseMap = corpseMap,
                Map = map,
                Position = position,
            };
        }

        return new MSG_CORPSE_QUERY_Server {
            Result = result,
        };
    }

    internal int Size() {
        var size = 0;

        // result: Generator.Generated.DataTypeEnum
        size += 1;

        if (Result.Value is MSG_CORPSE_QUERY_Server.CorpseQueryResultFound corpseQueryResultFound) {
            // map: Generator.Generated.DataTypeEnum
            size += 4;

            // position: Generator.Generated.DataTypeStruct
            size += 12;

            // corpse_map: Generator.Generated.DataTypeEnum
            size += 4;

        }

        return size;
    }

}


using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_GOSSIP_SELECT_OPTION: VanillaClientMessage, IWorldMessage {
    public required ulong Guid { get; set; }
    public required uint GossipListId { get; set; }
    public struct OptionalUnknown {
        /// <summary>
        /// vmangos: if (_player->PlayerTalkClass->GossipOptionCoded(gossipListId))
        /// </summary>
        public required string Code { get; set; }
    }
    public required OptionalUnknown? Unknown { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Guid, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(GossipListId, cancellationToken).ConfigureAwait(false);

        if (Unknown is { } unknown) {
            await w.WriteCString(unknown.Code, cancellationToken).ConfigureAwait(false);

        }
    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 380, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 380, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_GOSSIP_SELECT_OPTION> ReadBodyAsync(Stream r, uint bodySize, CancellationToken cancellationToken = default) {
        // ReSharper disable once InconsistentNaming
        var __size = 0;
        var guid = await r.ReadULong(cancellationToken).ConfigureAwait(false);
        __size += 8;

        var gossipListId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
        __size += 4;

        OptionalUnknown? optionalUnknown = null;
        if (__size < bodySize) {
            var code = await r.ReadCString(cancellationToken).ConfigureAwait(false);
            __size += code.Length + 1;

            optionalUnknown = new OptionalUnknown {
                Code = code,
            };
        }

        return new CMSG_GOSSIP_SELECT_OPTION {
            Guid = guid,
            GossipListId = gossipListId,
            Unknown = optionalUnknown,
        };
    }

    internal int Size() {
        var size = 0;

        // guid: Generator.Generated.DataTypeGuid
        size += 8;

        // gossip_list_id: Generator.Generated.DataTypeInteger
        size += 4;

        if (Unknown is { } unknown) {
            // code: Generator.Generated.DataTypeCstring
            size += unknown.Code.Length + 1;

        }
        return size;
    }

}


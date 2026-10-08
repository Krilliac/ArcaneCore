using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_SHOWTAXINODES: VanillaServerMessage, IWorldMessage {
    /// <summary>
    /// Set to 1 in mangoszero
    /// </summary>
    public required uint Unknown1 { get; set; }
    public required ulong Guid { get; set; }
    public required uint NearestNode { get; set; }
    public required List<uint> Nodes { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(Unknown1, cancellationToken).ConfigureAwait(false);

        await w.WriteULong(Guid, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(NearestNode, cancellationToken).ConfigureAwait(false);

        foreach (var v in Nodes) {
            await w.WriteUInt(v, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 425, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 425, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_SHOWTAXINODES> ReadBodyAsync(Stream r, uint bodySize, CancellationToken cancellationToken = default) {
        // ReSharper disable once InconsistentNaming
        var __size = 0;
        var unknown1 = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
        __size += 4;

        var guid = await r.ReadULong(cancellationToken).ConfigureAwait(false);
        __size += 8;

        var nearestNode = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
        __size += 4;

        var nodes = new List<uint>();
        while (__size < bodySize) {
            nodes.Add(await r.ReadUInt(cancellationToken).ConfigureAwait(false));
            __size += 4;
        }

        return new SMSG_SHOWTAXINODES {
            Unknown1 = unknown1,
            Guid = guid,
            NearestNode = nearestNode,
            Nodes = nodes,
        };
    }

    internal int Size() {
        var size = 0;

        // unknown1: Generator.Generated.DataTypeInteger
        size += 4;

        // guid: Generator.Generated.DataTypeGuid
        size += 8;

        // nearest_node: Generator.Generated.DataTypeInteger
        size += 4;

        // nodes: Generator.Generated.DataTypeArray
        size += Nodes.Sum(e => 4);

        return size;
    }

}


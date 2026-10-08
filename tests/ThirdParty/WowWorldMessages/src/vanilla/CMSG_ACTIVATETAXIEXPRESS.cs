using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_ACTIVATETAXIEXPRESS: VanillaClientMessage, IWorldMessage {
    public required ulong Guid { get; set; }
    /// <summary>
    /// vmangos/mangosone: Never used.
    /// </summary>
    public required uint TotalCost { get; set; }
    public required List<uint> Nodes { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Guid, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(TotalCost, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)Nodes.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in Nodes) {
            await w.WriteUInt(v, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 786, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 786, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_ACTIVATETAXIEXPRESS> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var guid = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var totalCost = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        // ReSharper disable once UnusedVariable.Compiler
        var nodeCount = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var nodes = new List<uint>();
        for (var i = 0; i < nodeCount; ++i) {
            nodes.Add(await r.ReadUInt(cancellationToken).ConfigureAwait(false));
        }

        return new CMSG_ACTIVATETAXIEXPRESS {
            Guid = guid,
            TotalCost = totalCost,
            Nodes = nodes,
        };
    }

    internal int Size() {
        var size = 0;

        // guid: Generator.Generated.DataTypeGuid
        size += 8;

        // total_cost: Generator.Generated.DataTypeInteger
        size += 4;

        // node_count: Generator.Generated.DataTypeInteger
        size += 4;

        // nodes: Generator.Generated.DataTypeArray
        size += Nodes.Sum(e => 4);

        return size;
    }

}


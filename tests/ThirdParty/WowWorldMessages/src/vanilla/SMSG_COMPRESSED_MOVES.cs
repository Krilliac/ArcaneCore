using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_COMPRESSED_MOVES: VanillaServerMessage, IWorldMessage {
    public required List<CompressedMove> Moves { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        foreach (var v in Moves) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        var output = new MemoryStream();
        await WriteBodyAsync(output, cancellationToken).ConfigureAwait(false);
        var compressedOutput = new MemoryStream();
        var zlib = new System.IO.Compression.ZLibStream(compressedOutput, System.IO.Compression.CompressionMode.Compress);
        zlib.Write(output.ToArray());
        zlib.Flush();

        await encrypter.WriteServerHeaderAsync(w, (uint)compressedOutput.Length + 4 + 2, 763, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)output.Length, cancellationToken).ConfigureAwait(false);
        await w.WriteAsync(compressedOutput.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        var output = new MemoryStream();
        await WriteBodyAsync(output, cancellationToken).ConfigureAwait(false);
        var compressedOutput = new MemoryStream();
        var zlib = new System.IO.Compression.ZLibStream(compressedOutput, System.IO.Compression.CompressionMode.Compress);
        zlib.Write(output.ToArray());
        zlib.Flush();

        await encrypter.WriteServerHeaderAsync(w, (uint)compressedOutput.Length + 4 + 2, 763, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)output.Length, cancellationToken).ConfigureAwait(false);
        await w.WriteAsync(compressedOutput.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_COMPRESSED_MOVES> ReadBodyAsync(Stream r, uint bodySize, CancellationToken cancellationToken = default) {
        var decompressedLength = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
        bodySize -= 4;

        var decompressed = new byte[decompressedLength];
        var remaining = new byte[bodySize];
        await r.ReadExactlyAsync(remaining, cancellationToken).ConfigureAwait(false);

        var zlib = new System.IO.Compression.ZLibStream(new MemoryStream(remaining), System.IO.Compression.CompressionMode.Decompress);
        zlib.ReadAtLeast(decompressed, int.Min((int)bodySize, (int)decompressedLength));

        r = new MemoryStream(decompressed);
        bodySize = decompressedLength;

        // ReSharper disable once InconsistentNaming
        var __size = 0;
        var moves = new List<CompressedMove>();
        while (__size < bodySize) {
            moves.Add(await Vanilla.CompressedMove.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
            __size += moves[^1].Size();
        }

        return new SMSG_COMPRESSED_MOVES {
            Moves = moves,
        };
    }

    internal int Size() {
        var size = 0;

        // moves: Generator.Generated.DataTypeArray
        size += Moves.Sum(e => e.Size());

        return size;
    }

}


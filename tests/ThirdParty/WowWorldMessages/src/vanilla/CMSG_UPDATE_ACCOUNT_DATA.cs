using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_UPDATE_ACCOUNT_DATA: VanillaClientMessage, IWorldMessage {
    /// <summary>
    /// Exact meaning unknown. Seems to be between 0 and 7. Block 6 is changed when changing `layout-cache.txt` inside the WTF folder.
    /// </summary>
    public required Vanilla.AccountDataType DataType { get; set; }
    public required List<byte> CompressedData { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt((uint)DataType, cancellationToken).ConfigureAwait(false);

        if (CompressedData.Count != 0) {
            var oldStream = w;
            w = new MemoryStream();
            foreach (var v in CompressedData) {
                await w.WriteByte(v, cancellationToken).ConfigureAwait(false);
            }
            var uncompressedLength = w.Position;

            var compressedOutput = new MemoryStream();
            var zlib = new System.IO.Compression.ZLibStream(compressedOutput, System.IO.Compression.CompressionMode.Compress);
            zlib.Write((w as MemoryStream)!.ToArray());
            zlib.Flush();

            w = oldStream;
            await w.WriteUInt((uint)uncompressedLength, cancellationToken).ConfigureAwait(false);
            await w.WriteAsync(compressedOutput.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        else {
            await w.WriteUInt(0, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 523, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 523, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_UPDATE_ACCOUNT_DATA> ReadBodyAsync(Stream r, uint bodySize, CancellationToken cancellationToken = default) {
        // ReSharper disable once InconsistentNaming
        var __size = 0;
        var dataType = (Vanilla.AccountDataType)await r.ReadUInt(cancellationToken).ConfigureAwait(false);
        __size += 4;

        var decompressedLength = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
        __size += 4;

        var decompressed = new byte[decompressedLength];
        var remaining = new byte[bodySize - __size];
        await r.ReadExactlyAsync(remaining, cancellationToken).ConfigureAwait(false);

        var zlib = new System.IO.Compression.ZLibStream(new MemoryStream(remaining), System.IO.Compression.CompressionMode.Decompress);
        zlib.ReadAtLeast(decompressed, remaining.Length);

        r = new MemoryStream(decompressed);
        var compressedData = new List<byte>();
        while (r.Position < r.Length) {
            compressedData.Add(await r.ReadByte(cancellationToken).ConfigureAwait(false));
            __size += 1;
        }

        return new CMSG_UPDATE_ACCOUNT_DATA {
            DataType = dataType,
            CompressedData = compressedData,
        };
    }

    internal int Size() {
        var memory = new MemoryStream();
        Task.WaitAll(WriteBodyAsync(memory));
        return (int)memory.Position;
    }

}


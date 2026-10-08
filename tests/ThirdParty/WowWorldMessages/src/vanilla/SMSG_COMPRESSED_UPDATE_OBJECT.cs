using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_COMPRESSED_UPDATE_OBJECT: VanillaServerMessage, IWorldMessage {
    public required byte HasTransport { get; set; }
    public required List<Object> Objects { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt((uint)Objects.Count, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(HasTransport, cancellationToken).ConfigureAwait(false);

        foreach (var v in Objects) {
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

        await encrypter.WriteServerHeaderAsync(w, (uint)compressedOutput.Length + 4 + 2, 502, cancellationToken).ConfigureAwait(false);

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

        await encrypter.WriteServerHeaderAsync(w, (uint)compressedOutput.Length + 4 + 2, 502, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)output.Length, cancellationToken).ConfigureAwait(false);
        await w.WriteAsync(compressedOutput.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_COMPRESSED_UPDATE_OBJECT> ReadBodyAsync(Stream r, uint bodySize, CancellationToken cancellationToken = default) {
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
        // ReSharper disable once UnusedVariable.Compiler
        var amountOfObjects = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
        __size += 4;

        var hasTransport = await r.ReadByte(cancellationToken).ConfigureAwait(false);
        __size += 1;

        var objects = new List<Object>();
        for (var i = 0; i < amountOfObjects; ++i) {
            objects.Add(await Vanilla.Object.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
            __size += objects[^1].Size();
        }

        return new SMSG_COMPRESSED_UPDATE_OBJECT {
            HasTransport = hasTransport,
            Objects = objects,
        };
    }

    internal int Size() {
        var size = 0;

        // amount_of_objects: Generator.Generated.DataTypeInteger
        size += 4;

        // has_transport: Generator.Generated.DataTypeInteger
        size += 1;

        // objects: Generator.Generated.DataTypeArray
        size += Objects.Sum(e => e.Size());

        return size;
    }

}


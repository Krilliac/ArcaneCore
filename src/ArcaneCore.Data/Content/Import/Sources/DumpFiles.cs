using System.Security.Cryptography;
using System.Text;

namespace ArcaneCore.Data.Content.Import;

/// <summary>
/// One input file as provenance: its file name only (never the directory), size, SHA-256 of the
/// bytes on disk and whether it is gzip-compressed.
/// </summary>
public sealed record SourceFileInfo(string Name, long Bytes, string Sha256, bool Gzip);

/// <summary>
/// Opens SQL dump files. classic-db ships its Full_DB snapshot as <c>*.sql.gz</c>, so gzip is
/// recognised by its magic number (<c>1F 8B</c>), not by the file name, and a plain file works
/// the same way. Both yield UTF-8 text.
/// </summary>
public static class DumpFiles
{
    /// <summary>Open <paramref name="path"/> as text, decompressing gzip on the fly (streaming).</summary>
    public static TextReader OpenText(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        FileStream file = OpenRead(path);
        try
        {
            Stream stream = IsGzip(file) ? new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress) : file;
            return new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 16);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>Name, size, SHA-256 and compression of a file (reads it once).</summary>
    public static SourceFileInfo Describe(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using FileStream file = OpenRead(path);
        bool gzip = IsGzip(file);
        string hash = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
        return new SourceFileInfo(Path.GetFileName(path), file.Length, hash, gzip);
    }

    private static FileStream OpenRead(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);

    /// <summary>True when the stream starts with the gzip magic number; the position is restored.</summary>
    private static bool IsGzip(FileStream file)
    {
        Span<byte> magic = stackalloc byte[2];
        int read = file.Read(magic);
        file.Position = 0;
        return read == 2 && magic[0] == 0x1F && magic[1] == 0x8B;
    }
}

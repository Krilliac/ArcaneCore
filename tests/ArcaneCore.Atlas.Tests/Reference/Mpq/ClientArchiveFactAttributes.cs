using ArcaneCore.Atlas.Core;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Mpq;

/// <summary>A fact that needs the real 1.12.1 client's MPQs (<c>ARCANECORE_TEST_CLIENT_DIR</c>); skipped, visibly, without them.</summary>
public sealed class ClientArchiveFactAttribute : FactAttribute
{
    public ClientArchiveFactAttribute()
    {
        if (ClientArchiveData.SkipReason() is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>
/// A fact that needs the real client's MPQs and a StormLib extraction of them (<c>ARCANECORE_TEST_CLIENT_FILES_DIR</c>)
/// to compare against; skipped, visibly, without either.
/// </summary>
public sealed class ClientExtractFactAttribute : FactAttribute
{
    public ClientExtractFactAttribute()
    {
        if ((ClientArchiveData.SkipReason() ?? ClientArchiveData.ExtractSkipReason()) is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>
/// A fact that needs the real client's MPQs and the client DBC folder with its <c>SHA256SUMS</c>
/// (<c>ARCANECORE_TEST_DBC_DIR</c>); skipped, visibly, without either.
/// </summary>
public sealed class ClientDbcFactAttribute : FactAttribute
{
    public ClientDbcFactAttribute()
    {
        if ((ClientArchiveData.SkipReason() ?? ClientArchiveData.DbcSkipReason()) is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>
/// A fact that needs the real client's MPQs and a StormLib extraction of terrain.MPQ alone
/// (<c>ARCANECORE_TEST_TERRAIN_MPQ_EXTRACT_DIR</c>, made with <c>mpqcli extract -k</c>); skipped, visibly, without either.
/// </summary>
public sealed class TerrainArchiveExtractFactAttribute : FactAttribute
{
    public TerrainArchiveExtractFactAttribute()
    {
        if ((ClientArchiveData.SkipReason() ?? ClientArchiveData.TerrainExtractSkipReason()) is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>Where the real-data tests find the client, its StormLib extraction and the DBC checksum list.</summary>
internal static class ClientArchiveData
{
    /// <summary>Directory with the DBC checksum list (<c>SHA256SUMS</c>) next to the extracted DBCs.</summary>
    public const string DbcDirectoryVariable = "ARCANECORE_TEST_DBC_DIR";

    public static string? ClientRoot => Normalize(Environment.GetEnvironmentVariable(AtlasPaths.ClientEnvironmentVariable));

    public static string? ExtractRoot => Normalize(Environment.GetEnvironmentVariable(DirectoryClientFiles.TestEnvironmentVariable));

    public static string? SkipReason()
    {
        string? root = ClientRoot;
        return root is null || !Directory.Exists(Path.Combine(root, "Data"))
            ? $"{AtlasPaths.ClientEnvironmentVariable} is not set to a 1.12.1 client root (the folder holding Data/*.MPQ)."
            : null;
    }

    public static string? ExtractSkipReason()
    {
        string? root = ExtractRoot;
        return root is null || !Directory.Exists(root)
            ? $"{DirectoryClientFiles.TestEnvironmentVariable} is not set to a StormLib extraction of the 1.12.1 client."
            : null;
    }

    /// <summary>Directory holding a StormLib extraction (folder structure kept) of terrain.MPQ on its own.</summary>
    public const string TerrainExtractVariable = "ARCANECORE_TEST_TERRAIN_MPQ_EXTRACT_DIR";

    public static string? TerrainExtractRoot => Normalize(Environment.GetEnvironmentVariable(TerrainExtractVariable));

    public static string? TerrainExtractSkipReason()
    {
        string? root = TerrainExtractRoot;
        return root is null || !Directory.Exists(Path.Combine(root, "World"))
            ? $"{TerrainExtractVariable} is not set to a StormLib extraction of terrain.MPQ alone (mpqcli extract -k)."
            : null;
    }

    public static string? DbcDirectory => Normalize(Environment.GetEnvironmentVariable(DbcDirectoryVariable));

    public static string? DbcSkipReason()
    {
        string? directory = DbcDirectory;
        return directory is null || !File.Exists(Path.Combine(directory, "SHA256SUMS"))
            ? $"{DbcDirectoryVariable} is not set to a client DBC folder holding SHA256SUMS."
            : null;
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value);
}

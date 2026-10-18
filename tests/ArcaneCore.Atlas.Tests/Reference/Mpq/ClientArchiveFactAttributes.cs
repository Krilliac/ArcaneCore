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

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value);
}

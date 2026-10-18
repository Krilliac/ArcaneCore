namespace ArcaneCore.Atlas.Core;

/// <summary>
/// Where Atlas reads data. <see cref="TerrainData"/> is the server's extracted data root (holding <c>maps</c>,
/// <c>vmaps</c>, <c>mmaps</c>), the same directory a world daemon's <c>Maps:DataDirectory</c> names.
/// <see cref="ClientRoot"/> is a 1.12.1 (build 5875) client installation (holding <c>Data\*.MPQ</c>). Either may be
/// absent; layers that need it then report <see cref="TileState.Missing"/> with the reason.
/// </summary>
public sealed record AtlasPaths(string? TerrainData, string? ClientRoot)
{
    /// <summary>Environment variable the real-data tests use for the extracted data root.</summary>
    public const string TerrainEnvironmentVariable = "ARCANECORE_TEST_TERRAIN_DIR";

    /// <summary>Environment variable the real-data tests use for the 1.12.1 client root.</summary>
    public const string ClientEnvironmentVariable = "ARCANECORE_TEST_CLIENT_DIR";

    public string? MapsDirectory => Sub(TerrainData, "maps");

    public string? VMapsDirectory => Sub(TerrainData, "vmaps");

    public string? MMapsDirectory => Sub(TerrainData, "mmaps");

    public string? ClientDataDirectory => Sub(ClientRoot, "Data");

    /// <summary>Reasons the configured directories are unusable (empty when both are usable or unset).</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (TerrainData is not null && !Directory.Exists(TerrainData))
        {
            problems.Add($"Terrain data directory '{TerrainData}' does not exist.");
        }
        else if (TerrainData is not null && MapsDirectory is null && VMapsDirectory is null && MMapsDirectory is null)
        {
            problems.Add($"Terrain data directory '{TerrainData}' has none of maps, vmaps or mmaps.");
        }

        if (ClientRoot is not null && ClientDataDirectory is null)
        {
            problems.Add($"Client directory '{ClientRoot}' has no Data folder.");
        }

        return problems;
    }

    /// <summary>Paths from the test environment variables (unset variables stay null).</summary>
    public static AtlasPaths FromEnvironment()
        => new(Normalize(Environment.GetEnvironmentVariable(TerrainEnvironmentVariable)),
            Normalize(Environment.GetEnvironmentVariable(ClientEnvironmentVariable)));

    private static string? Sub(string? root, string name)
    {
        if (root is null)
        {
            return null;
        }

        string path = Path.Combine(root, name);
        return Directory.Exists(path) ? path : null;
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value);
}

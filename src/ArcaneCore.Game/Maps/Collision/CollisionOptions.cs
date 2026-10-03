namespace ArcaneCore.Game.Maps.Collision;

/// <summary>
/// Collision data settings, bound from the <c>World:Collision</c> configuration section
/// (docs/integration/vmap-los.md). Every directory is optional: without data the open
/// line-of-sight and straight-line path defaults are used and nothing fails.
/// </summary>
public sealed class CollisionOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "World:Collision";

    /// <summary>
    /// Directory of the extracted vmaps (<c>NNN.vmtree</c>, <c>NNN_YY_XX.vmtile</c>, <c>*.vmo</c>).
    /// Empty: <c>vmaps/</c> under <c>World:Maps:DataDirectory</c> (vmangos <c>DataDir/vmaps</c>).
    /// </summary>
    public string VMapDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Directory of the generated navmeshes (<c>NNN.mmap</c>, <c>NNNXXYY.mmtile</c>).
    /// Empty: <c>mmaps/</c> under <c>World:Maps:DataDirectory</c> (vmangos <c>DataDir/mmaps</c>).
    /// </summary>
    public string MMapDirectory { get; set; } = string.Empty;

    /// <summary>Use vmaps for line of sight (vmangos <c>vmap.enableLOS</c>, default on).</summary>
    public bool EnableLineOfSight { get; set; } = true;

    /// <summary>Use vmaps for floor heights (vmangos <c>vmap.enableHeight</c>, default on).</summary>
    public bool EnableHeight { get; set; } = true;

    /// <summary>Use navmeshes for paths (vmangos <c>mmap.enabled</c>, default on).</summary>
    public bool EnablePathfinding { get; set; } = true;

    /// <summary>The vmap directory actually used: the setting, else <c>vmaps/</c> under the terrain data directory; null when neither is set.</summary>
    public string? ResolveVMapDirectory(string? dataDirectory) => Resolve(VMapDirectory, dataDirectory, "vmaps");

    /// <summary>The mmap directory actually used: the setting, else <c>mmaps/</c> under the terrain data directory; null when neither is set.</summary>
    public string? ResolveMMapDirectory(string? dataDirectory) => Resolve(MMapDirectory, dataDirectory, "mmaps");

    private static string? Resolve(string configured, string? dataDirectory, string folder)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return string.IsNullOrWhiteSpace(dataDirectory) ? null : Path.Combine(dataDirectory, folder);
    }
}

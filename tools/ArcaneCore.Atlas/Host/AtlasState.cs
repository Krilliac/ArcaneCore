namespace ArcaneCore.Atlas.Host;

/// <summary>
/// Where one Atlas user keeps its state: running-instance discovery files, saved bookmarks and captured images. The
/// root is <c>ARCANECORE_ATLAS_STATE_DIR</c> when that variable is set (tests and sandboxes), else
/// <c>&lt;LocalApplicationData&gt;/ArcaneCore/atlas</c>. The variable is read directly; it is not configuration binding.
/// </summary>
public sealed class AtlasState
{
    /// <summary>Environment variable that overrides the state root.</summary>
    public const string EnvironmentVariable = "ARCANECORE_ATLAS_STATE_DIR";

    public AtlasState(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    /// <summary>Directory of <c>&lt;pid&gt;.json</c> discovery files, one per running <c>serve</c>.</summary>
    public string InstancesDirectory => Path.Combine(Root, "instances");

    public string BookmarksFile => Path.Combine(Root, "bookmarks.json");

    /// <summary>Default output folder of the <c>capture</c> command.</summary>
    public string CapturesDirectory => Path.Combine(Root, "captures");

    /// <summary>The state root of this process: the environment override, else the per-user application data folder.</summary>
    public static AtlasState FromEnvironment()
    {
        string? overridden = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return new AtlasState(overridden);
        }

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(appData))
        {
            // A service account or container without a profile has no application data folder.
            appData = Path.GetTempPath();
        }

        return new AtlasState(Path.Combine(appData, "ArcaneCore", "atlas"));
    }
}

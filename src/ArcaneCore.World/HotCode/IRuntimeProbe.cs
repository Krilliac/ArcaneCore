using System.Reflection.Metadata;

namespace ArcaneCore.World.HotCode;

/// <summary>
/// What the launch guard needs to know about the process. A seam so tests never touch the
/// real process environment.
/// </summary>
public interface IRuntimeProbe
{
    /// <summary>The host environment name (DOTNET_ENVIRONMENT; "Production" when unset).</summary>
    string EnvironmentName { get; }

    /// <summary><see cref="MetadataUpdater.IsSupported"/>: the runtime can apply code deltas.</summary>
    bool MetadataUpdatesSupported { get; }

    string? GetEnvironmentVariable(string name);
}

/// <summary>The real process.</summary>
public sealed class SystemRuntimeProbe(string environmentName) : IRuntimeProbe
{
    public string EnvironmentName { get; } = environmentName;

    public bool MetadataUpdatesSupported => MetadataUpdater.IsSupported;

    public string? GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);
}

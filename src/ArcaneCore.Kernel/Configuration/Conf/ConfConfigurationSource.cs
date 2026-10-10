using Microsoft.Extensions.Configuration;

namespace ArcaneCore.Kernel.Configuration.Conf;

/// <summary>
/// A .conf file as a configuration source. Add it to the host's source list (<see cref="ConfFiles.Attach"/> does): every
/// <c>Build</c>, including the throwaway one of <c>.reload config</c>, reads the file again.
/// </summary>
public sealed class ConfConfigurationSource : IConfigurationSource
{
    /// <summary>The full path of the file.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>True: a missing file is an empty source. False: a missing file fails the first load.</summary>
    public bool Optional { get; set; }

    /// <summary>Watch the file and swap the values in when it is edited (follows <c>hostBuilder:reloadConfigOnChange</c> like appsettings.json).</summary>
    public bool ReloadOnChange { get; set; }

    /// <summary>How long to wait after a change notification before reading (editors write in several steps).</summary>
    public TimeSpan ReloadDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Told about a reload that failed (<c>world.conf:12: ...</c>); the provider keeps its last good values.</summary>
    public Action<string>? ReloadError { get; set; }

    public IConfigurationProvider Build(IConfigurationBuilder builder) => new ConfConfigurationProvider(this);
}

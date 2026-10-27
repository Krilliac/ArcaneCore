using System.Globalization;
using ArcaneCore.Atlas.Core.Commands;

namespace ArcaneCore.Atlas.Live;

/// <summary>Finds running world daemons that serve a map feed.</summary>
public interface IFeedDiscovery
{
    /// <summary>The process ids of every feed currently being served, ascending. Throws <see cref="CommandException"/> (unavailable) when this host cannot look.</summary>
    IReadOnlyList<int> FindFeeds();

    /// <summary>Whether a pipe of this name exists, or null when this host cannot tell cheaply (the connect attempt then decides).</summary>
    bool? PipeExists(string pipeName);
}

/// <summary>
/// Discovery by listing the Windows named-pipe namespace (<c>\\.\pipe\</c>) for <c>arcanecore-mapfeed-{pid}</c>. Listing the namespace is
/// read-only: it opens no pipe instance, so it cannot take the slot of the single instance a feed serves a client with (a plain file-exists
/// probe on a pipe path does open one). Other platforms refuse discovery with <see cref="CommandException.Unavailable"/>; an explicit
/// pid still connects there.
/// </summary>
public sealed class PipeFeedDiscovery : IFeedDiscovery
{
    // ref: ATLAS-2026-10-18.md:211-212 (pipe "arcanecore-mapfeed-{pid}", same-user, local only)
    // ref: src/ArcaneCore.World/Ops/Control/WorldControlPipeOptions.cs:26 (the control pipe it is modelled on is "arcanecore-world-" + PidToken)
    private readonly Func<IEnumerable<string>>? _pipeNames;

    /// <summary>Discovery over the real pipe namespace (Windows only).</summary>
    public PipeFeedDiscovery()
    {
    }

    /// <summary>Discovery over a given listing of pipe names (tests, and hosts with another way to list pipes).</summary>
    public PipeFeedDiscovery(Func<IEnumerable<string>> pipeNames)
    {
        _pipeNames = pipeNames ?? throw new ArgumentNullException(nameof(pipeNames));
    }

    /// <inheritdoc />
    public IReadOnlyList<int> FindFeeds()
    {
        var pids = new SortedSet<int>();
        foreach (string name in ListPipes(LiveProtocol.PipePrefix + "*"))
        {
            if (TryParsePid(name, out int pid))
            {
                pids.Add(pid);
            }
        }

        return [.. pids];
    }

    /// <inheritdoc />
    public bool? PipeExists(string pipeName)
    {
        ArgumentException.ThrowIfNullOrEmpty(pipeName);
        if (_pipeNames is null && !OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return ListPipes(pipeName).Any(n => string.Equals(n, pipeName, StringComparison.OrdinalIgnoreCase));
        }
        catch (CommandException)
        {
            return null;
        }
    }

    /// <summary>The process id in a feed pipe name, or false when <paramref name="pipeName"/> is not <c>arcanecore-mapfeed-{digits}</c>.</summary>
    public static bool TryParsePid(string pipeName, out int pid)
    {
        ArgumentNullException.ThrowIfNull(pipeName);
        pid = 0;
        ReadOnlySpan<char> name = pipeName;
        return name.StartsWith(LiveProtocol.PipePrefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(name[LiveProtocol.PipePrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out pid)
            && pid > 0;
    }

    private IEnumerable<string> ListPipes(string pattern)
    {
        if (_pipeNames is not null)
        {
            return _pipeNames();
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new CommandException(
                CommandException.Unavailable,
                "automatic map-feed discovery lists Windows named pipes (\\\\.\\pipe\\) and is not available on this platform; pass the world daemon's pid explicitly.");
        }

        try
        {
            // Names only (no path), matched by the file system against the pattern; the namespace root has no subdirectories to recurse into.
            return [.. Directory.EnumerateFileSystemEntries(@"\\.\pipe\", pattern).Select(Path.GetFileName).OfType<string>()];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new CommandException(CommandException.Unavailable, $"could not list \\\\.\\pipe\\ to find a map feed: {e.Message}", e);
        }
    }
}

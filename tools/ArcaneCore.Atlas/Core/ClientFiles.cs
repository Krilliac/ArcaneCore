namespace ArcaneCore.Atlas.Core;

/// <summary>
/// Read access to the 1.12.1 client's files by in-archive path, with patch archives already applied (a later
/// archive's copy replaces an earlier one's). Paths compare case-insensitively with either slash. Implementations
/// are thread-safe.
/// </summary>
public interface IClientFileSource
{
    /// <summary>What is open, for <c>status</c> (for example the archives in load order and their file counts).</summary>
    string Description { get; }

    bool Exists(string path);

    /// <summary>The file's bytes, or null when no archive has it. Throws <see cref="InvalidDataException"/> for corrupt data.</summary>
    byte[]? Read(string path);

    /// <summary>Known paths starting with <paramref name="prefix"/> (from the archives' file lists), sorted ordinally.</summary>
    IReadOnlyList<string> List(string prefix);

    /// <summary>The archive that supplied a file, or null when absent.</summary>
    string? ArchiveOf(string path);
}

/// <summary>Path normalization shared by every <see cref="IClientFileSource"/>.</summary>
public static class ClientPath
{
    /// <summary>Backslash separators, no leading separator, upper-case invariant (the comparison key).</summary>
    public static string Key(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Replace('/', '\\').TrimStart('\\').ToUpperInvariant();
    }
}

/// <summary>An in-memory <see cref="IClientFileSource"/> for synthetic fixtures (tests never commit client files).</summary>
public sealed class MemoryClientFiles : IClientFileSource
{
    private readonly Dictionary<string, (string Path, byte[] Data)> _files = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public string Description => $"memory ({_files.Count} files)";

    public void Add(string path, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        lock (_gate)
        {
            _files[ClientPath.Key(path)] = (path.Replace('/', '\\').TrimStart('\\'), data);
        }
    }

    public bool Exists(string path)
    {
        lock (_gate)
        {
            return _files.ContainsKey(ClientPath.Key(path));
        }
    }

    public byte[]? Read(string path)
    {
        lock (_gate)
        {
            return _files.TryGetValue(ClientPath.Key(path), out (string Path, byte[] Data) file) ? (byte[])file.Data.Clone() : null;
        }
    }

    public IReadOnlyList<string> List(string prefix)
    {
        string key = ClientPath.Key(prefix);
        lock (_gate)
        {
            return [.. _files.Where(p => p.Key.StartsWith(key, StringComparison.Ordinal)).Select(p => p.Value.Path).Order(StringComparer.Ordinal)];
        }
    }

    public string? ArchiveOf(string path) => Exists(path) ? "memory" : null;
}

/// <summary>
/// An <see cref="IClientFileSource"/> over a directory tree that mirrors the in-archive paths (an extracted client:
/// <c>World\Maps\...</c>, <c>DBFilesClient\...</c>). The tree is indexed once at construction, so lookups are
/// case-insensitive on every file system.
/// <para>
/// An extracted directory produced by <c>mpqcli</c> may contain 0-byte files for paths deleted by <c>patch.MPQ</c>
/// (whereas <c>MpqFileSystem</c> reports deleted entries as absent); callers reading extracted directories should check
/// for 0-byte files where appropriate.
/// </para>
/// </summary>
public sealed class DirectoryClientFiles : IClientFileSource
{
    /// <summary>Environment variable naming an extracted client tree for real-data tests.</summary>
    public const string TestEnvironmentVariable = "ARCANECORE_TEST_CLIENT_FILES_DIR";

    private readonly Dictionary<string, (string Path, string FullPath)> _files = new(StringComparer.Ordinal);
    private readonly string[] _sortedKeys;

    public DirectoryClientFiles(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
        if (!Directory.Exists(Root))
        {
            throw new DirectoryNotFoundException($"Client file directory '{Root}' does not exist.");
        }

        foreach (string full in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(Root, full).Replace(Path.DirectorySeparatorChar, '\\').Replace('/', '\\');
            _files.TryAdd(ClientPath.Key(relative), (relative, full));
        }

        _sortedKeys = [.. _files.Keys.Order(StringComparer.Ordinal)];
    }

    public string Root { get; }

    public string Description => $"directory {Root} ({_files.Count} files)";

    /// <summary>True when <paramref name="root"/> looks like an extracted client (has World or DBFilesClient).</summary>
    public static bool LooksExtracted(string root)
        => Directory.Exists(Path.Combine(root, "World")) || Directory.Exists(Path.Combine(root, "DBFilesClient"));

    public bool Exists(string path) => _files.ContainsKey(ClientPath.Key(path));

    public byte[]? Read(string path)
        => _files.TryGetValue(ClientPath.Key(path), out (string Path, string FullPath) file) ? File.ReadAllBytes(file.FullPath) : null;

    public IReadOnlyList<string> List(string prefix)
    {
        string key = ClientPath.Key(prefix);
        int start = Array.BinarySearch(_sortedKeys, key, StringComparer.Ordinal);
        if (start < 0)
        {
            start = ~start;
        }

        var result = new List<string>();
        for (int i = start; i < _sortedKeys.Length && _sortedKeys[i].StartsWith(key, StringComparison.Ordinal); i++)
        {
            result.Add(_files[_sortedKeys[i]].Path);
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }

    public string? ArchiveOf(string path) => Exists(path) ? Root : null;
}

/// <summary>One map from the client's Map.dbc.</summary>
/// <param name="Directory">The map's folder under <c>World\Maps\</c> (for example <c>Azeroth</c>).</param>
/// <param name="InstanceType">Map.dbc's instance type field (0 for open world in 1.12.1).</param>
public sealed record ClientMap(uint Id, string Directory, string Name, uint InstanceType);

/// <summary>Map and area names from the client DBCs (registered when a client directory is configured).</summary>
public interface IMapDirectory
{
    IReadOnlyList<ClientMap> Maps { get; }

    bool TryGetMap(uint mapId, out ClientMap map);

    /// <summary>AreaTable.dbc name for an area id, or null when unknown.</summary>
    string? AreaName(uint areaId);
}

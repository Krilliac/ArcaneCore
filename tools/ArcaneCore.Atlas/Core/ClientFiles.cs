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

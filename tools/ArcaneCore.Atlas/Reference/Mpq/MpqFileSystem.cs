using System.Globalization;
using System.Text.RegularExpressions;
using ArcaneCore.Atlas.Core;

namespace ArcaneCore.Atlas.Reference.Mpq;

/// <summary>An archive file in the client's Data folder that <see cref="MpqFileSystem"/> did not mount, and why.</summary>
/// <param name="Path">Full path of the file.</param>
/// <param name="Reason">Why it was left out.</param>
public sealed record SkippedArchive(string Path, string Reason);

/// <summary>
/// The 1.12.1 client's archives as one read-only file tree: every <c>*.MPQ</c> in the <c>Data</c> folder, mounted in the
/// client's load order so that a later archive overrides an earlier one. Thread-safe: archives are immutable after opening
/// and every read is positional.
/// </summary>
/// <remarks>
/// <para>Load order, lowest to highest priority:</para>
/// <list type="number">
/// <item>The base archives in the order the client chain lists them: <c>base, dbc, interface, misc, model, sound, speech,
/// terrain, texture, wmo</c>. (Same list in D:\WoW-RE-Research\specs\asset-path-scheme.md:144 and D:\New folder\Worldforge\src\client_data.cpp:10-15; the vmangos
/// extractors open <c>model, dbc, terrain, patch, patch-2</c> and <c>terrain, model, texture, wmo, base, misc, patch, patch-2</c>
/// with each archive searched before the ones opened earlier, vmangos@0e3ff01 contrib/extractor/System.cpp:102-109,
/// vmangos@0e3ff01 contrib/vmap_extractor/vmapextract/vmapexport.cpp:331-347, vmangos@0e3ff01 dep/libmpq/libmpq/mpq_libmpq.cpp:54,69.)
/// Any other archive that is not a patch (this client also has <c>fonts.MPQ</c>) follows, by name. The base archives of the
/// retail client hold disjoint file sets (scan in the Atlas tests), so their relative order never changes a result.</item>
/// <item>The patch archives by level: <c>patch.MPQ</c> (level 1), then <c>patch-2.MPQ</c>, <c>patch-3.MPQ</c> and so on up to 99
/// (vmangos@0e3ff01 contrib/vmap_extractor/vmapextract/vmapexport.cpp:294-317, the same naming and range).</item>
/// </list>
/// <para>
/// <c>backup.MPQ</c> is not mounted. It is an installer copy of the launcher payload (executables, DLLs, the Documentation
/// images, 127 of its 135 listed names are also in <c>base.MPQ</c>) and holds no game asset; none of the chains above names it.
/// <c>base.MPQ</c> is mounted first, as in those chains; it holds the same launcher payload, so it only ever contributes
/// names no game path uses. Locale folders (<c>Data\enUS\locale-enUS.MPQ</c> and friends) are not read: the client under
/// test has none, and a client that has them is reported through <see cref="Skipped"/> rather than silently half-read.
/// </para>
/// <para>
/// A deletion marker (block flag <c>0x02000000</c>, zero length) in a patch archive hides the name: it resolves to "absent"
/// even though an earlier archive still holds a copy. patch.MPQ carries 5708 of them, every one over a file an earlier archive
/// holds (StormLib.h:225 "the file no longer exists"; the vmangos reader treats such an entry as a dummy file,
/// mpq_libmpq.cpp:78-85).
/// </para>
/// </remarks>
public sealed partial class MpqFileSystem : IClientFileSource, IDisposable
{
    // ref: D:\WoW-RE-Research\specs\asset-path-scheme.md:144 ; D:\New folder\Worldforge\src\client_data.cpp:10-15
    private static readonly string[] BaseChain =
    [
        "base.MPQ", "dbc.MPQ", "interface.MPQ", "misc.MPQ", "model.MPQ", "sound.MPQ", "speech.MPQ", "terrain.MPQ", "texture.MPQ", "wmo.MPQ",
    ];

    private readonly MpqArchive[] _archives;
    private readonly Lazy<FileIndex> _index;
    private readonly Lock _scanGate = new();
    private IReadOnlyList<MpqScanReport>? _cheapScan;
    private IReadOnlyList<MpqScanReport>? _deepScan;

    private MpqFileSystem(string dataDirectory, MpqArchive[] archives, IReadOnlyList<SkippedArchive> skipped)
    {
        DataDirectory = dataDirectory;
        _archives = archives;
        Skipped = skipped;
        _index = new Lazy<FileIndex>(BuildIndex, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The folder the archives were read from.</summary>
    public string DataDirectory { get; }

    /// <summary>The mounted archives, lowest priority first (the last one wins).</summary>
    public IReadOnlyList<MpqArchive> Archives => _archives;

    /// <summary>Archives found in or under the data folder and left out, with the reason.</summary>
    public IReadOnlyList<SkippedArchive> Skipped { get; }

    /// <inheritdoc />
    public string Description
        => $"mpq {DataDirectory}: {_archives.Length} archives, lowest to highest priority {string.Join(" < ", _archives.Select(a => a.Name))}";

    /// <summary>Number of distinct visible file names the archives' listfiles name (builds the file index on first use).</summary>
    public int ListedFileCount => _index.Value.Keys.Length;

    /// <summary>True when <paramref name="dataDirectory"/> directly contains at least one <c>*.MPQ</c> file.</summary>
    public static bool HasArchives(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        return Directory.Exists(dataDirectory) && FindArchiveFiles(dataDirectory).Count > 0;
    }

    /// <summary>
    /// Orders archive file names for mounting. Names that cannot be mounted are returned with the reason in
    /// <paramref name="skipped"/>. Pure function of the names (no I/O).
    /// </summary>
    /// <param name="fileNames">File names (with or without a folder), any case.</param>
    /// <param name="skipped">Receives (name, reason) for each name left out.</param>
    /// <returns>The names to mount, lowest priority first.</returns>
    public static IReadOnlyList<string> LoadOrder(IEnumerable<string> fileNames, out IReadOnlyList<(string Name, string Reason)> skipped)
    {
        ArgumentNullException.ThrowIfNull(fileNames);
        var left = new List<(string, string)>();
        var ranked = new List<(int Class, int Rank, string Name)>();
        foreach (string full in fileNames)
        {
            string name = Path.GetFileName(full);
            if (string.Equals(name, "backup.MPQ", StringComparison.OrdinalIgnoreCase))
            {
                left.Add((full, "backup.MPQ is an installer copy of the launcher payload and holds no game assets"));
                continue;
            }

            int chain = Array.FindIndex(BaseChain, b => string.Equals(b, name, StringComparison.OrdinalIgnoreCase));
            if (chain >= 0)
            {
                ranked.Add((0, chain, full));
            }
            else if (PatchLevel(name) is int level)
            {
                ranked.Add((2, level, full));
            }
            else
            {
                ranked.Add((1, 0, full));
            }
        }

        skipped = left;
        return [.. ranked.OrderBy(r => r.Class).ThenBy(r => r.Rank).ThenBy(r => Path.GetFileName(r.Name), StringComparer.OrdinalIgnoreCase).Select(r => r.Name)];
    }

    /// <summary>
    /// Opens every mountable archive in <paramref name="dataDirectory"/>.
    /// </summary>
    /// <param name="dataDirectory">The client's <c>Data</c> folder.</param>
    /// <param name="verifyChecksums">Check sector checksums on every read (see <see cref="MpqArchive.VerifyChecksums"/>).</param>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
    /// <exception cref="InvalidDataException">The folder holds no mountable archive, or one of them is damaged.</exception>
    public static MpqFileSystem Open(string dataDirectory, bool verifyChecksums = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        string directory = Path.GetFullPath(dataDirectory);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Client data directory '{directory}' does not exist.");
        }

        IReadOnlyList<string> order = LoadOrder(FindArchiveFiles(directory), out IReadOnlyList<(string Name, string Reason)> left);
        var skipped = new List<SkippedArchive>(left.Select(l => new SkippedArchive(l.Name, l.Reason)));
        foreach (string subdirectory in Directory.EnumerateDirectories(directory))
        {
            foreach (string nested in FindArchiveFiles(subdirectory))
            {
                skipped.Add(new SkippedArchive(nested, "archives in a sub-folder (locale archives) are not mounted"));
            }
        }

        if (order.Count == 0)
        {
            throw new InvalidDataException($"'{directory}' has no mountable *.MPQ archive.");
        }

        var opened = new List<MpqArchive>(order.Count);
        try
        {
            foreach (string path in order)
            {
                try
                {
                    opened.Add(MpqArchive.Open(path, verifyChecksums));
                }
                catch (Exception e) when (e is InvalidDataException or NotSupportedException)
                {
                    throw new InvalidDataException($"Cannot mount '{path}': {e.Message}", e);
                }
            }
        }
        catch
        {
            foreach (MpqArchive archive in opened)
            {
                archive.Dispose();
            }

            throw;
        }

        return new MpqFileSystem(directory, [.. opened], skipped);
    }

    /// <inheritdoc />
    public bool Exists(string path) => Resolve(path, out _, out _) == Resolution.Found;

    /// <inheritdoc />
    /// <exception cref="InvalidDataException">The file is damaged (including a failed sector checksum).</exception>
    /// <exception cref="NotSupportedException">The file uses a method this reader does not implement (the message names it).</exception>
    public byte[]? Read(string path)
        => Resolve(path, out int archive, out MpqEntry entry) == Resolution.Found ? _archives[archive].Read(entry, Normalize(path)) : null;

    /// <inheritdoc />
    public string? ArchiveOf(string path)
        => Resolve(path, out int archive, out _) == Resolution.Found ? _archives[archive].Name : null;

    /// <summary>
    /// The archive whose deletion marker hides <paramref name="path"/>, or null when the path is not deleted (it may still
    /// be absent).
    /// </summary>
    public string? DeletedBy(string path)
        => Resolve(path, out int archive, out _) == Resolution.Deleted ? _archives[archive].Name : null;

    /// <inheritdoc />
    public IReadOnlyList<string> List(string prefix)
    {
        FileIndex index = _index.Value;
        string key = ClientPath.Key(prefix ?? string.Empty);
        int start = Array.BinarySearch(index.Keys, key, StringComparer.Ordinal);
        if (start < 0)
        {
            start = ~start;
        }

        var result = new List<string>();
        for (int i = start; i < index.Keys.Length && index.Keys[i].StartsWith(key, StringComparison.Ordinal); i++)
        {
            result.Add(index.Names[i]);
        }

        // IClientFileSource.List promises ordinal order of the returned names; the index is ordered by upper-cased key.
        // ref: tools/ArcaneCore.Atlas/Core/ClientFiles.cs:18
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    /// <summary>
    /// Statistics for each mounted archive (block counts, flag combinations, locales). With <paramref name="sectors"/> every
    /// compressed file's sectors are read as well, which reads the whole client (tens of seconds). Results are cached.
    /// </summary>
    /// <param name="sectors">True for the per-method sector statistics and checksum census.</param>
    /// <param name="cancellationToken">Observed between files.</param>
    public IReadOnlyList<MpqScanReport> Scan(bool sectors, CancellationToken cancellationToken = default)
    {
        // One scan at a time: a second caller waits for the first and then takes its cached result instead of rescanning.
        lock (_scanGate)
        {
            if ((sectors ? _deepScan : _cheapScan) is { } cached)
            {
                return cached;
            }

            var reports = new MpqScanReport[_archives.Length];
            Parallel.For(
                0,
                _archives.Length,
                new ParallelOptions { MaxDegreeOfParallelism = sectors ? Math.Min(4, Environment.ProcessorCount) : 1, CancellationToken = cancellationToken },
                i => reports[i] = MpqScanner.Scan(_archives[i], sectors, cancellationToken));
            if (sectors)
            {
                _deepScan = reports;
            }
            else
            {
                _cheapScan = reports;
            }

            return reports;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (MpqArchive archive in _archives)
        {
            archive.Dispose();
        }
    }

    private static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Replace('/', '\\').TrimStart('\\');
    }

    // Level 1 is patch.MPQ, level N (2-99) is patch-N.MPQ.
    // ref: vmangos@0e3ff01 contrib/vmap_extractor/vmapextract/vmapexport.cpp:294-317
    private static int? PatchLevel(string fileName)
    {
        if (string.Equals(fileName, "patch.MPQ", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        Match m = PatchName().Match(fileName);
        if (!m.Success)
        {
            return null;
        }

        int level = int.Parse(m.Groups[1].ValueSpan, CultureInfo.InvariantCulture);
        return level is >= 2 and <= 99 ? level : null;
    }

    // The extractors build these names with "%s-%d.MPQ" for 2..99 (vmapexport.cpp:299-308), so there is no leading zero.
    [GeneratedRegex(@"^patch-([2-9]|[1-9][0-9])\.MPQ$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PatchName();

    private static List<string> FindArchiveFiles(string directory)
    {
        var options = new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, RecurseSubdirectories = false };
        return [.. Directory.EnumerateFiles(directory, "*.MPQ", options)];
    }

    private enum Resolution
    {
        Absent,
        Found,
        Deleted,
    }

    // Highest-priority archive that has the name decides; a deletion marker there means the name is gone.
    private Resolution Resolve(string path, out int archive, out MpqEntry entry)
    {
        archive = -1;
        entry = default;
        string normalized = Normalize(path);
        if (normalized.Length == 0)
        {
            return Resolution.Absent;
        }

        return Resolve(MpqNameHash.Of(normalized), out archive, out entry);
    }

    private Resolution Resolve(in MpqNameHash hash, out int archive, out MpqEntry entry)
    {
        for (int i = _archives.Length - 1; i >= 0; i--)
        {
            if (_archives[i].TryFind(hash, out entry))
            {
                archive = i;
                return (entry.Block.Flags & MpqFileFlags.DeleteMarker) != 0 ? Resolution.Deleted : Resolution.Found;
            }
        }

        archive = -1;
        entry = default;
        return Resolution.Absent;
    }

    private FileIndex BuildIndex()
    {
        // Names come from the archives' listfiles; the winner of each is then resolved exactly as Read does, so List never
        // names a file Exists would deny (and a name only a later archive lists still resolves to that archive).
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (MpqArchive archive in _archives)
        {
            foreach (string name in archive.ListFile)
            {
                if (IsInternalName(name))
                {
                    continue;
                }

                names[ClientPath.Key(name)] = Normalize(name);
            }
        }

        var keys = new List<string>(names.Count);
        foreach ((string key, string name) in names)
        {
            if (Resolve(MpqNameHash.Of(name), out _, out _) == Resolution.Found)
            {
                keys.Add(key);
            }
        }

        keys.Sort(StringComparer.Ordinal);
        return new FileIndex([.. keys], [.. keys.Select(k => names[k])]);
    }

    // ref: D:\mangos-unified\dep\StormLib\src\SFileListFile.cpp:338 ; D:\New folder\Worldforge\src\mpq.cpp:108 (internal "(name)" entries are not files)
    private static bool IsInternalName(string name) => name.Length > 2 && name[0] == '(' && name[^1] == ')' && !name.Contains('\\');

    private sealed record FileIndex(string[] Keys, string[] Names);
}

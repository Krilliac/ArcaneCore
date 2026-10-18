using System.Collections.Concurrent;
using ArcaneCore.Atlas.Core;

namespace ArcaneCore.Atlas.Reference.Client.Models;

/// <summary>Why a model could not be loaded.</summary>
public enum ModelProblemKind
{
    /// <summary>No archive holds the file.</summary>
    Missing,

    /// <summary>The file exists but is empty, corrupt or not a 1.12.1 file; <see cref="ModelProblem.Message"/> says which.</summary>
    Rejected,
}

/// <summary>A model that could not be loaded. Never reported as empty geometry.</summary>
public sealed record ModelProblem(ModelProblemKind Kind, string Path, string Message);

/// <summary>The outcome of loading one model: exactly one of <see cref="Model"/> and <see cref="Problem"/> is set.</summary>
public sealed record ModelLoad<T>(T? Model, ModelProblem? Problem)
    where T : class
{
    public bool IsOk => Model is not null;
}

/// <summary>
/// A WMO with all its group files: the root, one <see cref="WmoGroupFile"/> per group index, and the vmap extractor's
/// per-group skip rule. Immutable and safe to share between threads.
/// </summary>
public sealed class WmoModel(string path, WmoRootFile root, IReadOnlyList<WmoGroupFile> groups)
{
    /// <summary>The client path the model was loaded from.</summary>
    public string Path { get; } = path;

    public WmoRootFile Root { get; } = root;

    /// <summary>Group files by group index (MOHD's group count of them).</summary>
    public IReadOnlyList<WmoGroupFile> Groups { get; } = groups;

    /// <summary>Approximate heap size, for the model cache.</summary>
    public long EstimatedBytes => Root.EstimatedBytes + Groups.Sum(g => g.EstimatedBytes);

    /// <summary>Why the extractor drops a group (it contributes no triangles to the server's vmap), or null when it keeps it.</summary>
    public string? SkipReason(int groupIndex)
    {
        WmoGroupFile group = Groups[groupIndex];

        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/wmo.cpp:512-526 (WMOGroup::ShouldSkip: flag 0x80, flag 0x4000000, or the group name "antiportal")
        if ((group.Flags & WmoGroupFlags.Unreachable) != 0)
        {
            return "unreachable";
        }

        if ((group.Flags & WmoGroupFlags.Antiportal) != 0)
        {
            return "antiportal";
        }

        return Root.GroupNameAt(group.NameOffset) == "antiportal" ? "antiportal-name" : null;
    }

    public string? GroupName(int groupIndex) => Root.GroupNameAt(Groups[groupIndex].NameOffset);

    /// <summary>
    /// The triangle count the extractor records for the model (precise mode keeps every triangle of every kept group); a
    /// WMO with none is dropped by the extractor.
    /// </summary>
    public int ExtractorTriangleCount
    {
        get
        {
            // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/vmapexport.cpp:211-218 (skipped groups add nothing; Wmo_nVertices sums the kept groups' triangles)
            // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/wmo.cpp:583-584 (a WMO instance whose recorded count is 0 is not written)
            int total = 0;
            for (int i = 0; i < Groups.Count; i++)
            {
                if (SkipReason(i) is null)
                {
                    total += Groups[i].TriangleCount;
                }
            }

            return total;
        }
    }

    /// <summary>
    /// MODD indices referenced by a kept group's MODR (and inside the MODD list). The extractor only places a doodad some
    /// kept group references.
    /// </summary>
    public IReadOnlySet<int> ReferencedDoodads()
    {
        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/vmapexport.cpp:219-229 (References = MODR entries of kept groups that are inside the MODD list)
        var references = new SortedSet<int>();
        for (int i = 0; i < Groups.Count; i++)
        {
            if (SkipReason(i) is not null)
            {
                continue;
            }

            foreach (ushort reference in Groups[i].DoodadReferences)
            {
                if (reference < Root.Doodads.Count)
                {
                    references.Add(reference);
                }
            }
        }

        return references;
    }
}

/// <summary>
/// Loads and keeps parsed client models. A load that finds a missing or corrupt file is cached too (as a
/// <see cref="ModelProblem"/>), so a broken model is parsed once. Concurrent requests for the same file share one load;
/// an I/O failure is not cached. Bounded by approximate heap size: the least recently used models are dropped first.
/// Thread-safe.
/// </summary>
public sealed class ClientModelCache(long maxBytes = ClientModelCache.DefaultMaxBytes)
{
    public const long DefaultMaxBytes = 512L * 1024 * 1024;

    // A corrupt MOHD must not make the loader try millions of group files.
    private const uint MaxGroups = 4096;

    private readonly ConcurrentDictionary<(IClientFileSource Source, string Key), Lazy<Entry>> _entries = new();
    private readonly Lock _evictGate = new();
    private long _bytes;
    private long _clock;

    private sealed class Entry(object? model, ModelProblem? problem, long bytes)
    {
        public object? Model { get; } = model;

        public ModelProblem? Problem { get; } = problem;

        public long Bytes { get; } = bytes;

        public long LastUse;
    }

    public long Bytes => Interlocked.Read(ref _bytes);

    public int Count => _entries.Count;

    /// <summary>Loads a WMO (root plus every group file) by client path.</summary>
    public ModelLoad<WmoModel> GetWmo(IClientFileSource files, string clientPath)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientPath);
        Entry entry = Resolve(files, "W:" + ClientPath.Key(clientPath), () => LoadWmo(files, clientPath));
        return new ModelLoad<WmoModel>(entry.Model as WmoModel, entry.Problem);
    }

    /// <summary>Loads an M2 by client path (a <c>.mdx</c> name is mapped to the <c>.m2</c> file).</summary>
    public ModelLoad<M2ModelFile> GetM2(IClientFileSource files, string storedPath)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(storedPath);
        string clientPath = ClientModelNames.ClientFilePath(storedPath);
        Entry entry = Resolve(files, "M:" + ClientPath.Key(clientPath), () => LoadM2(files, clientPath));
        return new ModelLoad<M2ModelFile>(entry.Model as M2ModelFile, entry.Problem);
    }

    /// <summary>Drops every cached model.</summary>
    public void Clear()
    {
        _entries.Clear();
        Interlocked.Exchange(ref _bytes, 0);
    }

    private static Entry LoadWmo(IClientFileSource files, string clientPath)
    {
        try
        {
            byte[]? rootBytes = files.Read(clientPath);
            if (rootBytes is null)
            {
                return Problem(ModelProblemKind.Missing, clientPath, "no archive holds this WMO.");
            }

            WmoRootFile root = WmoRootFile.Parse(rootBytes);
            if (root.Header.GroupCount > MaxGroups)
            {
                throw new InvalidDataException($"WMO root lists {root.Header.GroupCount} groups (limit {MaxGroups}).");
            }

            var groups = new List<WmoGroupFile>((int)root.Header.GroupCount);
            for (int i = 0; i < root.Header.GroupCount; i++)
            {
                string groupPath = WmoRootFile.GroupFilePath(clientPath, i);
                byte[]? groupBytes = files.Read(groupPath);
                if (groupBytes is null)
                {
                    throw new InvalidDataException($"group file {groupPath} is missing.");
                }

                try
                {
                    groups.Add(WmoGroupFile.Parse(groupBytes));
                }
                catch (InvalidDataException e)
                {
                    throw new InvalidDataException($"group file {groupPath}: {e.Message}", e);
                }
            }

            var model = new WmoModel(clientPath, root, groups);
            return new Entry(model, null, model.EstimatedBytes);
        }
        catch (InvalidDataException e)
        {
            return Problem(ModelProblemKind.Rejected, clientPath, e.Message);
        }
    }

    private static Entry LoadM2(IClientFileSource files, string clientPath)
    {
        try
        {
            byte[]? bytes = files.Read(clientPath);
            if (bytes is null)
            {
                return Problem(ModelProblemKind.Missing, clientPath, "no archive holds this model.");
            }

            M2ModelFile model = M2ModelFile.Parse(bytes);
            return new Entry(model, null, model.EstimatedBytes);
        }
        catch (InvalidDataException e)
        {
            return Problem(ModelProblemKind.Rejected, clientPath, e.Message);
        }
    }

    private static Entry Problem(ModelProblemKind kind, string path, string message)
        => new(null, new ModelProblem(kind, path, message), 256);

    private Entry Resolve(IClientFileSource files, string key, Func<Entry> load)
    {
        (IClientFileSource, string) id = (files, key);
        Lazy<Entry> lazy = _entries.GetOrAdd(id, _ => new Lazy<Entry>(
            () =>
            {
                Entry created = load();
                Interlocked.Add(ref _bytes, created.Bytes);
                return created;
            },
            LazyThreadSafetyMode.ExecutionAndPublication));
        Entry entry;
        try
        {
            entry = lazy.Value;
        }
        catch
        {
            // An I/O failure is not cached: the next caller loads again.
            _entries.TryRemove(new KeyValuePair<(IClientFileSource, string), Lazy<Entry>>(id, lazy));
            throw;
        }

        entry.LastUse = Interlocked.Increment(ref _clock);
        Evict(id);
        return entry;
    }

    private void Evict((IClientFileSource, string) keep)
    {
        if (Interlocked.Read(ref _bytes) <= maxBytes)
        {
            return;
        }

        lock (_evictGate)
        {
            long over = Interlocked.Read(ref _bytes) - maxBytes;
            if (over <= 0)
            {
                return;
            }

            var candidates = _entries
                .Where(p => p.Value.IsValueCreated && !p.Key.Equals(keep))
                .Select(p => (p.Key, Entry: p.Value.Value))
                .OrderBy(c => c.Entry.LastUse)
                .ToList();
            foreach ((var key, Entry entry) in candidates)
            {
                if (over <= 0)
                {
                    break;
                }

                if (_entries.TryRemove(key, out _))
                {
                    Interlocked.Add(ref _bytes, -entry.Bytes);
                    over -= entry.Bytes;
                }
            }
        }
    }
}

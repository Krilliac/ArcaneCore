using System.Numerics;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Client.Models;
using ArcaneCore.Game.Maps.Collision.VMaps;

namespace ArcaneCore.Atlas.Extract;

/// <summary>One group of an extracted model: what the reference extractor's raw model file holds for it.</summary>
/// <param name="MogpFlags">The WMO group's MOGP flags (0 for an M2).</param>
/// <param name="GroupWmoId">The WMO group's id (0 for an M2).</param>
/// <param name="BoundLow">First stored bound corner of the WMO group (zero for an M2).</param>
/// <param name="BoundHigh">Second stored bound corner.</param>
/// <param name="Vertices">Model-space vertices.</param>
/// <param name="Indices">Triangle corner indices, three per triangle.</param>
/// <param name="Liquid">The group's liquid, or null.</param>
internal sealed record VmapRawGroup(uint MogpFlags, uint GroupWmoId, Vector3 BoundLow, Vector3 BoundHigh, Vector3[] Vertices, ushort[] Indices, WmoLiquid? Liquid);

/// <summary>An extracted model before its trees are built.</summary>
/// <param name="RootWmoId">The WMO's id from its root header (0 for an M2).</param>
/// <param name="Groups">The kept groups, in file order.</param>
internal sealed record VmapRawModel(uint RootWmoId, IReadOnlyList<VmapRawGroup> Groups)
{
    /// <summary>The model as the server reads it: a triangle tree per group and a tree over the groups, built as the reference assembler builds them.</summary>
    // ref: vmangos@0e3ff01 src/game/vmap/TileAssembler.cpp:308-340 (convertRawFile) and src/game/vmap/WorldModel.cpp:261-267, :412-416
    public WorldModel ToWorldModel()
    {
        var groups = new GroupModel[Groups.Count];
        var groupBounds = new (Vector3 Low, Vector3 High)[Groups.Count];
        for (int g = 0; g < groups.Length; g++)
        {
            VmapRawGroup raw = Groups[g];
            var triangles = new MeshTriangle[raw.Indices.Length / 3];
            var bounds = new (Vector3 Low, Vector3 High)[triangles.Length];
            for (int t = 0; t < triangles.Length; t++)
            {
                triangles[t] = new MeshTriangle(raw.Indices[3 * t], raw.Indices[(3 * t) + 1], raw.Indices[(3 * t) + 2]);
                bounds[t] = TriangleBounds(raw.Vertices[raw.Indices[3 * t]], raw.Vertices[raw.Indices[(3 * t) + 1]], raw.Vertices[raw.Indices[(3 * t) + 2]]);
            }

            groups[g] = new GroupModel(raw.BoundLow, raw.BoundHigh, raw.MogpFlags, raw.GroupWmoId, raw.Vertices, triangles, VmapBih.Build(bounds), raw.Liquid);
            groupBounds[g] = (raw.BoundLow, raw.BoundHigh);
        }

        return new WorldModel(RootWmoId, groups, VmapBih.Build(groupBounds, VmapBih.GroupLeafSize));
    }

    // ref: vmangos@0e3ff01 src/game/vmap/WorldModel.cpp:94-103 (lo = (v0.min(v1)).min(v2), hi likewise; min and max keep the earlier value on a tie)
    private static (Vector3 Low, Vector3 High) TriangleBounds(Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 lo = Min(Min(a, b), c);
        Vector3 hi = Max(Max(a, b), c);
        return (lo, hi);
    }

    private static Vector3 Min(Vector3 current, Vector3 v) => new(current.X < v.X ? current.X : v.X, current.Y < v.Y ? current.Y : v.Y, current.Z < v.Z ? current.Z : v.Z);

    private static Vector3 Max(Vector3 current, Vector3 v) => new(v.X < current.X ? current.X : v.X, v.Y < current.Y ? current.Y : v.Y, v.Z < current.Z ? current.Z : v.Z);
}

/// <summary>The doodads of a WMO that its kept groups reference and whose models were extracted.</summary>
/// <param name="Sets">The WMO's doodad sets.</param>
/// <param name="Doodads">Every MODD entry.</param>
/// <param name="References">Indices into <paramref name="Doodads"/> that a kept group references, ascending.</param>
internal sealed record VmapWmoDoodads(IReadOnlyList<WmoDoodadSet> Sets, IReadOnlyList<WmoDoodad> Doodads, IReadOnlyList<ushort> References)
{
    public static VmapWmoDoodads None { get; } = new([], [], []);
}

/// <summary>What the placement pass needs to know about an extracted model.</summary>
/// <param name="Name">The model's server name (the plain file name, fixed case and underscores).</param>
/// <param name="ClientPath">The client file it was read from.</param>
/// <param name="IsWmo">A WMO (else an M2).</param>
/// <param name="HeaderCount">
/// The count the reference stores in the raw file's header and tests before placing the model: the triangles of the kept
/// groups for a WMO, the bounding vertices for an M2. A model with 0 here is never placed.
/// </param>
/// <param name="Doodads">A WMO's doodad data.</param>
/// <param name="Hull">An M2's bounding vertices, kept for the spawn bounds (empty for a WMO).</param>
internal sealed record VmapModelInfo(string Name, string ClientPath, bool IsWmo, int HeaderCount, VmapWmoDoodads Doodads, Vector3[] Hull);

/// <summary>
/// The models of one extraction run, by server name: finds a model's client file, converts it the way the reference
/// extractor does (WMO: every triangle of every group that is not unreachable or an antiportal, plus liquid; M2: the
/// bounding hull) and remembers the outcome, so each model is read once. Not thread-safe: the placement pass that fills
/// it runs on one thread.
/// </summary>
internal sealed class VmapModelLibrary
{
    // ref: vmangos@0e3ff01 contrib/vmap_extractor/vmapextract/wmo.cpp:512-526 (ShouldSkip: unreachable 0x80, antiportal 0x4000000, a group named "antiportal")
    private const string AntiportalName = "antiportal";

    // ref: vmangos@0e3ff01 contrib/vmap_extractor/vmapextract/wmo.cpp:440-445 (a root with flag 4 stores LiquidType.dbc ids in its groups)
    private const uint RootUsesLiquidTypeIds = 0x4;

    // ref: vmangos@0e3ff01 contrib/vmap_extractor/vmapextract/wmo.cpp:473 (MOGP flag 0x80000 selects ocean over water)
    private const uint GroupIsOcean = 0x80000;

    // ref: vmangos@0e3ff01 contrib/vmap_extractor/vmapextract/wmo.cpp:482-485 (Stratholme_raid.wmo, root id 4489: slime becomes Naxxramas slime, entry 21)
    private const uint NaxxramasRootId = 4489;

    private readonly IClientFileSource _files;
    private readonly Dictionary<string, VmapModelInfo?> _models = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _wmoRoots = new(StringComparer.Ordinal);
    private readonly List<string> _problems = [];

    /// <param name="files">The client files.</param>
    public VmapModelLibrary(IClientFileSource files)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));

        // The reference extracts every root WMO of the archives before it reads any map, each under its plain name; when
        // two folders hold a WMO of the same plain name, the first one it meets is the one placements get. Here the first
        // is the first in ordinal path order (the reference follows its archive listing order).
        // ref: vmangos@0e3ff01 contrib/vmap_extractor/vmapextract/vmapexport.cpp:115-137 (ExtractWmo: every listed name containing ".wmo")
        // ref: vmangos@0e3ff01 contrib/vmap_extractor/vmapextract/vmapexport.cpp:139-169 (plain name, fixed; an existing file is kept; "_ddd" names are group files)
        foreach (string path in files.List(string.Empty))
        {
            if (!path.Contains(".wmo", StringComparison.Ordinal))
            {
                continue;
            }

            string name = ClientModelNames.FixNameSpaces(ClientModelNames.FixNameCase(ClientModelNames.PlainName(path.Replace('/', '\\'))));
            if (IsGroupFileName(name))
            {
                continue;
            }

            if (_wmoRoots.TryGetValue(name, out string? first))
            {
                _problems.Add($"WMO name '{name}' is used by both '{first}' and '{path}'; placements get the first.");
            }
            else
            {
                _wmoRoots.Add(name, path);
            }
        }
    }

    /// <summary>Things the run should report: name collisions and models that could not be converted.</summary>
    public IReadOnlyList<string> Problems => _problems;

    /// <summary>Every model looked up so far that was extracted, by server name.</summary>
    public IEnumerable<VmapModelInfo> Extracted => _models.Values.Where(m => m is not null)!;

    /// <summary>
    /// The WMO placed under <paramref name="name"/> (a plain name already fixed the way the caller's file fixes it), or null
    /// when no extracted WMO has exactly that name.
    /// </summary>
    public VmapModelInfo? Wmo(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (_models.TryGetValue(name, out VmapModelInfo? known))
        {
            return known is { IsWmo: true } ? known : null;
        }

        VmapModelInfo? info = null;
        if (_wmoRoots.TryGetValue(name, out string? path))
        {
            info = Guard(path, () => ConvertWmo(name, path));
        }

        _models[name] = info;
        return info;
    }

    /// <summary>
    /// The M2 a doodad path names: the server name it is extracted under, and the model when it was extracted (it exists
    /// and has a bounding hull). <paramref name="storedPath"/> is the path as an ADT's MMDX or a WMO's MODN stores it.
    /// </summary>
    // ref: vmangos@0e3ff01 contrib/vmap_extractor/vmapextract/gameobject_extract.cpp:19-77 (ExtractSingleModel)
    public (string Name, VmapModelInfo? Model) Doodad(string storedPath)
    {
        ArgumentNullException.ThrowIfNull(storedPath);
        if (storedPath.Length < 4)
        {
            return (string.Empty, null);
        }

        // .mdx and .mdl name the .m2 file of the same name.
        string original = storedPath;
        string plain = ClientModelNames.PlainName(original);
        int dot = plain.LastIndexOf('.');
        string extension = dot < 0 ? string.Empty : plain[dot..];
        if (extension is ".mdx" or ".MDX" or ".mdl" or ".MDL")
        {
            original = string.Concat(original.AsSpan(0, original.Length - 2), "2");
            plain = ClientModelNames.PlainName(original);
        }

        string name = ClientModelNames.FixNameSpaces(ClientModelNames.FixNameCase(plain));
        if (_models.TryGetValue(name, out VmapModelInfo? known))
        {
            // An existing file of that name answers for every later path with the same plain name.
            return (name, known is { IsWmo: false } ? known : null);
        }

        // The reference tries the path with the fixed file name, then the path as stored, then the fixed name with its
        // underscores turned back into spaces; the first that opens and has a bounding hull is converted.
        string folder = original[..(original.Length - plain.Length)];
        string fixedPath = folder + name;
        VmapModelInfo? info = Guard(fixedPath, () => ConvertM2(name, fixedPath));
        if (info is null && !string.Equals(original, fixedPath, StringComparison.Ordinal) && _files.Exists(original))
        {
            info = Guard(original, () => ConvertM2(name, original));
        }

        if (info is null && folder.Length > 0)
        {
            string spaced = folder + name.Replace('_', ' ');
            if (!string.Equals(spaced, fixedPath, StringComparison.Ordinal) && !string.Equals(spaced, original, StringComparison.Ordinal) && _files.Exists(spaced))
            {
                info = Guard(spaced, () => ConvertM2(name, spaced));
            }
        }

        _models[name] = info;
        return (name, info);
    }

    /// <summary>Reads a placed model again and converts it in full, for writing its model file. Thread-safe.</summary>
    /// <exception cref="InvalidDataException">The client file can no longer be read as it was during the placement pass.</exception>
    public VmapRawModel Load(VmapModelInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        VmapRawModel? raw = info.IsWmo ? ReadWmo(info.ClientPath, withDoodads: false, out _, out _) : ReadM2(info.ClientPath);
        return raw ?? throw new InvalidDataException($"'{info.ClientPath}' changed while it was being extracted.");
    }

    // ref: vmangos@0e3ff01 contrib/vmap_extractor/vmapextract/vmapexport.cpp:154-169 (a name whose last '_' is followed by three digits is a group file)
    private static bool IsGroupFileName(string plainName)
    {
        int underscore = plainName.LastIndexOf('_');
        if (underscore < 0)
        {
            return false;
        }

        int digits = 0;
        for (int i = underscore; i < underscore + 4 && i < plainName.Length; i++)
        {
            digits += char.IsAsciiDigit(plainName[i]) ? 1 : 0;
        }

        return digits == 3;
    }

    private VmapModelInfo? Guard(string path, Func<VmapModelInfo?> convert)
    {
        try
        {
            return convert();
        }
        catch (Exception e) when (e is InvalidDataException or IOException or NotSupportedException)
        {
            _problems.Add($"'{path}' was not extracted: {e.Message}");
            return null;
        }
    }

    private VmapModelInfo? ConvertWmo(string name, string path)
    {
        VmapRawModel? raw = ReadWmo(path, withDoodads: true, out VmapWmoDoodads doodads, out int triangles);
        return raw is null ? null : new VmapModelInfo(name, path, IsWmo: true, triangles, doodads, []);
    }

    private VmapModelInfo? ConvertM2(string name, string path)
    {
        VmapRawModel? raw = ReadM2(path);
        return raw is null ? null : new VmapModelInfo(name, path, IsWmo: false, raw.Groups[0].Vertices.Length, VmapWmoDoodads.None, raw.Groups[0].Vertices);
    }

    /// <summary>The M2's bounding hull as one group, or null when the file is absent or has no bounding triangles.</summary>
    // ref: vmangos@0e3ff01 contrib/vmap_extractor/vmapextract/model.cpp:33-74 (open) and :76-140 (ConvertToVMAPModel)
    private VmapRawModel? ReadM2(string path)
    {
        byte[]? bytes = _files.Read(path);
        if (bytes is null)
        {
            return null;
        }

        M2ModelFile model = M2ModelFile.Parse(bytes);
        if (model.BoundingIndices.Length == 0)
        {
            return null;
        }

        if (model.BoundingIndices.Length % 3 != 0)
        {
            throw new InvalidDataException($"the bounding hull has {model.BoundingIndices.Length} indices, not a whole number of triangles.");
        }

        // The reference swaps the second and third corner of every triangle. Its two axis changes of the vertices cancel
        // out, so the vertices are stored as the M2 holds them.
        ushort[] indices = (ushort[])model.BoundingIndices.Clone();
        for (int i = 1; i < indices.Length; i += 3)
        {
            (indices[i], indices[i + 1]) = (indices[i + 1], indices[i]);
        }

        foreach (ushort index in indices)
        {
            if (index >= model.BoundingVertices.Length)
            {
                throw new InvalidDataException($"a bounding triangle index ({index}) points past the {model.BoundingVertices.Length} bounding vertices.");
            }
        }

        return new VmapRawModel(0, [new VmapRawGroup(0, 0, Vector3.Zero, Vector3.Zero, model.BoundingVertices, indices, null)]);
    }

    /// <summary>
    /// The WMO's kept groups, or null when the root or one of its group files is absent. <paramref name="withDoodads"/>
    /// also resolves the doodad models (which fills the library, so only the placement pass asks for it).
    /// </summary>
    // ref: vmangos@0e3ff01 contrib/vmap_extractor/vmapextract/vmapexport.cpp:139-243 (ExtractSingleWmo)
    private VmapRawModel? ReadWmo(string path, bool withDoodads, out VmapWmoDoodads doodads, out int triangles)
    {
        doodads = VmapWmoDoodads.None;
        triangles = 0;
        byte[]? rootBytes = _files.Read(path);
        if (rootBytes is null)
        {
            return null;
        }

        WmoRootFile root = WmoRootFile.Parse(rootBytes);

        // Doodad names whose model was extracted (now or earlier); only those can be referenced.
        // ref: vmangos@0e3ff01 contrib/vmap_extractor/vmapextract/wmo.cpp:89-106 (MODN: ExtractSingleModel per name)
        var validNames = new HashSet<uint>();
        foreach (WmoDoodad doodad in withDoodads ? root.Doodads : [])
        {
            if (doodad.ModelPath.Length > 0 && !validNames.Contains(doodad.NameOffset) && Doodad(doodad.ModelPath).Model is not null)
            {
                validNames.Add(doodad.NameOffset);
            }
        }

        var groups = new List<VmapRawGroup>();
        var references = new SortedSet<ushort>();
        for (int g = 0; g < root.Header.GroupCount; g++)
        {
            byte[]? groupBytes = _files.Read(WmoRootFile.GroupFilePath(path, g));
            if (groupBytes is null)
            {
                // The reference deletes the raw file of a WMO with a missing group, so the WMO is never placed.
                return null;
            }

            WmoGroupFile group = WmoGroupFile.Parse(groupBytes);
            if ((group.Flags & WmoGroupFlags.Unreachable) != 0
                || (group.Flags & WmoGroupFlags.Antiportal) != 0
                || string.Equals(root.GroupNameAt(group.NameOffset), AntiportalName, StringComparison.Ordinal))
            {
                continue;
            }

            triangles += group.TriangleCount;
            groups.Add(new VmapRawGroup(group.Flags, group.GroupId, group.BoundsMin, group.BoundsMax, group.Vertices, group.Indices, Liquid(root, group, groupBytes)));

            // ref: vmangos@0e3ff01 contrib/vmap_extractor/vmapextract/vmapexport.cpp:219-229
            foreach (ushort reference in group.DoodadReferences)
            {
                if (reference < root.Doodads.Count && validNames.Contains(root.Doodads[reference].NameOffset))
                {
                    references.Add(reference);
                }
            }
        }

        doodads = new VmapWmoDoodads(root.DoodadSets, root.Doodads, [.. references]);
        return new VmapRawModel(root.Header.WmoId, groups);
    }

    /// <summary>
    /// A group's liquid with the reference's LiquidType.dbc entry (1 water, 2 ocean, 3 magma, 4 slime, 21 Naxxramas
    /// slime), or null when the group has none or the reference drops it.
    /// </summary>
    // ref: vmangos@0e3ff01 contrib/vmap_extractor/vmapextract/wmo.cpp:433-506
    private static WmoLiquid? Liquid(WmoRootFile root, WmoGroupFile group, ReadOnlySpan<byte> groupBytes)
    {
        if (group.Liquid is not { } grid || !LiquidIsLastChunk(groupBytes))
        {
            return null;
        }

        if (grid.VerticesX != grid.TilesX + 1 || grid.VerticesY != grid.TilesY + 1)
        {
            throw new InvalidDataException($"the liquid grid has {grid.VerticesX} x {grid.VerticesY} vertices for {grid.TilesX} x {grid.TilesY} tiles.");
        }

        uint entry;
        if ((root.Header.Flags & RootUsesLiquidTypeIds) != 0)
        {
            entry = group.LiquidType;
        }
        else if (group.LiquidType == 15)
        {
            entry = 0;
        }
        else
        {
            entry = group.LiquidType + 1;
        }

        if (entry == 0)
        {
            // The first tile that holds liquid names the type.
            foreach (byte tile in grid.TileFlags)
            {
                if ((tile & 0xF) != 15)
                {
                    entry = (uint)(tile & 0xF) + 1;
                    break;
                }
            }
        }

        if (entry != 0 && entry < 21)
        {
            entry = (((byte)entry - 1) & 3) switch
            {
                0 => (group.Flags & GroupIsOcean) != 0 ? 2u : 1u,
                1 => 2,
                2 => 3,
                _ => root.Header.WmoId == NaxxramasRootId ? 21u : 4u,
            };
        }

        // The header's type field is 16 bits wide.
        return new WmoLiquid((uint)grid.TilesX, (uint)grid.TilesY, grid.Corner, (ushort)entry, grid.Heights, grid.TileFlags);
    }

    /// <summary>
    /// The reference clears its "has liquid" state before every chunk it reads, so a group's liquid survives only when
    /// MLIQ is the last chunk of the file (as it is in the 1.12.1 layout).
    /// </summary>
    // ref: vmangos@0e3ff01 contrib/vmap_extractor/vmapextract/wmo.cpp:194-195 (LiquEx_size = 0; liquflags = 0; inside the chunk loop)
    private static bool LiquidIsLastChunk(ReadOnlySpan<byte> groupBytes)
    {
        foreach (WowChunk chunk in WowChunks.Scan(groupBytes, 0, groupBytes.Length))
        {
            if (chunk.Tag == "MOGP")
            {
                List<WowChunk> subs = WowChunks.Scan(groupBytes, chunk.Offset + WmoGroupFile.MogpHeaderSize, groupBytes.Length);
                return subs.Count > 0 && subs[^1].Tag == "MLIQ";
            }
        }

        return false;
    }
}

using System.Numerics;
using System.Text;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Client.Models;
using ArcaneCore.Atlas.Reference.Client.Terrain;
using ArcaneCore.Game.Maps.Collision.VMaps;

namespace ArcaneCore.Atlas.Extract;

/// <summary>What happened to one map during a <see cref="VmapExtractor"/> run.</summary>
/// <param name="Id">Map.dbc id.</param>
/// <param name="Directory">The map's folder under <c>World\Maps</c>.</param>
/// <param name="HasWdt">False when the client has no WDT for the map; nothing is written for it.</param>
/// <param name="Tiled">True when the map's models come from terrain tiles, false when from one global WMO placement.</param>
/// <param name="Spawns">Distinct model placements in the map's tree.</param>
/// <param name="TileFiles">Tile files written.</param>
/// <param name="Failed">Client files of the map that were refused.</param>
public sealed record VmapExtractMapReport(uint Id, string Directory, bool HasWdt, bool Tiled, int Spawns, int TileFiles, int Failed);

/// <summary>The outcome of a <see cref="VmapExtractor"/> run.</summary>
/// <param name="VmapsDirectory">The folder the files are in.</param>
/// <param name="Maps">Per map, in map id order.</param>
/// <param name="ModelFiles">Model files written.</param>
/// <param name="Bytes">Bytes written.</param>
/// <param name="Problems">Client files that were refused, name collisions and dropped placements, with the reason.</param>
public sealed record VmapExtractReport(string VmapsDirectory, IReadOnlyList<VmapExtractMapReport> Maps, int ModelFiles, long Bytes, IReadOnlyList<string> Problems);

/// <summary>
/// Writes the server's static collision data (<c>vmaps/</c>: one <c>NNN.vmtree</c> per map, <c>NNN_YY_XX.vmtile</c> per
/// terrain tile with models, and a model file per placed model) from a 1.12.1 client, doing in one pass what the
/// reference tools do in two (the vmap extractor's raw files, then the assembler). The files are "VMAP_7.0" and are read
/// by the world server's <see cref="VMapManager"/>.
/// </summary>
/// <remarks>
/// <para>
/// What is the same as the reference: which models and WMO groups are kept, every triangle and vertex of a model file and
/// its trees (so a model file is meant to come out equal to the reference's), which placements exist, their positions,
/// rotations, scales and names, and the tree and tile layout.
/// </para>
/// <para>
/// What differs: placement ids are handed out in map id order (the reference follows Map.dbc's record order), so ids,
/// and with them the order of spawns inside a tree, need not match; the bounds of M2 placements and the placement of
/// WMO doodads go through sines and cosines, whose last bit depends on the math library; when two folders hold a WMO of
/// the same plain name the reference keeps the first in its archive listing, this the first in path order (each case
/// is reported); the game object model list (<c>temp_gameobject_models</c>) is not written, because nothing in ArcaneCore
/// reads it; placements whose model name is not a plain file name are dropped and reported, where the reference would
/// write them through the path; and a client file that fails the readers' checks is reported and left out.
/// </para>
/// </remarks>
public sealed class VmapExtractor
{
    /// <summary>The folder under the output root that holds the files, as the server expects it.</summary>
    public const string VmapsFolder = "vmaps";

    // ref: vmangos@0e3ff01 contrib/vmap_extractor/vmapextract/wdtfile.cpp:107 (the WDT's own placements are filed under tile 65, 65)
    private const uint GlobalTile = 65;

    // ref: vmangos@0e3ff01 contrib/vmap_extractor/vmapextract/wmo.cpp:586-590 and src/game/vmap/TileAssembler.cpp:99-100 (a WMO stored at x = z = 0 is moved to the map centre)
    private const float MapCentre = 533.33333f * 32;

    // ref: vmangos@0e3ff01 contrib/vmap_extractor/vmapextract/wmo.cpp:560-563 (MODF flag 1: destructible, not placed)
    private const ushort DestructibleWmo = 0x1;

    private readonly IClientFileSource _files;
    private readonly IMapDirectory _maps;

    /// <param name="files">The client files.</param>
    /// <param name="maps">The client's Map.dbc.</param>
    public VmapExtractor(IClientFileSource files, IMapDirectory maps)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _maps = maps ?? throw new ArgumentNullException(nameof(maps));
    }

    /// <summary>Extracts into <c>&lt;outputRoot&gt;/vmaps</c>, replacing the files of the maps it writes.</summary>
    /// <param name="outputRoot">The server data root.</param>
    /// <param name="onlyMaps">Map ids to extract; null or empty extracts every map.</param>
    /// <param name="threads">Model files built at once (at least 1).</param>
    /// <exception cref="ArgumentException">A requested map id is not in Map.dbc.</exception>
    public VmapExtractReport Extract(string outputRoot, IReadOnlyCollection<uint>? onlyMaps, int threads, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        ArgumentOutOfRangeException.ThrowIfLessThan(threads, 1);

        var selected = new List<ClientMap>();
        if (onlyMaps is { Count: > 0 })
        {
            foreach (uint id in onlyMaps.Distinct().Order())
            {
                selected.Add(_maps.TryGetMap(id, out ClientMap map) ? map : throw new ArgumentException($"Map {id} is not in the client's Map.dbc.", nameof(onlyMaps)));
            }
        }
        else
        {
            selected.AddRange(_maps.Maps.OrderBy(m => m.Id));
        }

        string directory = Path.Combine(outputRoot, VmapsFolder);
        Directory.CreateDirectory(directory);

        var library = new VmapModelLibrary(_files);
        var ids = new Dictionary<(uint ClientId, ushort DoodadId), uint>();
        var problems = new List<string>();
        var droppedNames = new HashSet<string>(StringComparer.Ordinal);
        var reports = new List<MapResult>(selected.Count);
        var modelFiles = new SortedDictionary<string, VmapModelInfo>(StringComparer.Ordinal);
        long bytes = 0;
        foreach (ClientMap map in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MapResult result = ExtractMap(map, directory, library, ids, problems, droppedNames, cancellationToken);
            reports.Add(result);
            bytes += result.Bytes;
            foreach ((string file, VmapModelInfo model) in result.ModelFiles)
            {
                modelFiles.TryAdd(file, model);
            }
        }

        // ref: vmangos@0e3ff01 src/game/vmap/TileAssembler.cpp:195-206 (every placed model name becomes a model file)
        long modelBytes = 0;
        var failed = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.ForEach(modelFiles, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = cancellationToken }, pair =>
        {
            try
            {
                byte[] file = library.Load(pair.Value).ToWorldModel().ToBytes();
                ExtractOutput.WriteWhole(ExtractOutput.FileIn(directory, pair.Key), file);
                Interlocked.Add(ref modelBytes, file.Length);
            }
            catch (Exception e) when (e is InvalidDataException or IOException or NotSupportedException or UnauthorizedAccessException)
            {
                failed.Add($"model file '{pair.Key}' (from '{pair.Value.ClientPath}') was not written: {e.Message}");
            }
        });

        problems.AddRange(library.Problems);
        problems.AddRange(failed.Order(StringComparer.Ordinal));
        return new VmapExtractReport(directory, [.. reports.Select(r => r.Report)], modelFiles.Count - failed.Count, bytes + modelBytes, problems);
    }

    private sealed record MapResult(VmapExtractMapReport Report, long Bytes, IReadOnlyList<(string File, VmapModelInfo Model)> ModelFiles);

    /// <summary>One placement as the reference's placement list holds it; positions and bounds are vmap internal coordinates.</summary>
    private sealed record Placement(uint Tile, uint Flags, ushort AdtId, uint Id, Vector3 Position, Vector3 Rotation, float Scale, Vector3 BoundLow, Vector3 BoundHigh, string Name, VmapModelInfo Model);

    private MapResult ExtractMap(ClientMap map, string directory, VmapModelLibrary library, Dictionary<(uint, ushort), uint> ids, List<string> problems, HashSet<string> droppedNames, CancellationToken cancellationToken)
    {
        // ref: vmangos@0e3ff01 contrib/vmap_extractor/vmapextract/vmapexport.cpp:245-283 (ParsMapFiles: the WDT, then every <Map>_<x>_<y>.adt for x, y in 0..63)
        string wdtPath = ClientMapPaths.WdtPath(map.Directory);
        WdtFile wdt;
        try
        {
            byte[]? wdtBytes = _files.Read(wdtPath);
            if (wdtBytes is null)
            {
                return new MapResult(new VmapExtractMapReport(map.Id, map.Directory, HasWdt: false, Tiled: false, 0, 0, 0), 0, []);
            }

            wdt = WdtFile.Parse(wdtBytes);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or NotSupportedException)
        {
            problems.Add($"'{wdtPath}' was refused: {e.Message}");
            return new MapResult(new VmapExtractMapReport(map.Id, map.Directory, HasWdt: true, Tiled: false, 0, 0, Failed: 1), 0, []);
        }

        var placements = new List<Placement>();
        int failed = 0;

        // The WDT's own WMO placements. Their names get their case fixed but keep their spaces, so a name with a space
        // finds no extracted model and is not placed.
        // ref: vmangos@0e3ff01 contrib/vmap_extractor/vmapextract/wdtfile.cpp:71-111
        foreach (AdtWmoObject wmo in wdt.WmoObjects)
        {
            string name = ClientModelNames.FixNameCase(ClientModelNames.PlainName(wmo.ModelPath.Replace('/', '\\')));
            AddWmo(placements, wmo, name, GlobalTile, GlobalTile, library, ids);
        }

        for (uint x = 0; x < 64; x++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (uint y = 0; y < 64; y++)
            {
                string adtPath = $"World\\Maps\\{map.Directory}\\{map.Directory}_{x}_{y}.adt";
                try
                {
                    byte[]? adtBytes = _files.Read(adtPath);
                    if (adtBytes is null)
                    {
                        continue;
                    }

                    AddTile(placements, AdtFile.Parse(adtBytes), x, y, library, ids);
                }
                catch (Exception e) when (e is InvalidDataException or IOException or NotSupportedException)
                {
                    failed++;
                    problems.Add($"'{adtPath}' was refused: {e.Message}");
                }
            }
        }

        // Model names come from client data and become file names, so a placement whose name is not a plain file name is
        // dropped before anything (tree, tile files, model files, spawn count) is built from it. One problem per distinct name per run.
        placements.RemoveAll(p =>
        {
            if (VMapFormat.IsSafeModelName(p.Name) && ExtractOutput.IsPlainFileName(VMapFormat.ModelFileName(p.Name)))
            {
                return false;
            }

            // Shown and de-duplicated without the stored terminating NUL, so one model reached through an ADT and through a WMO doodad is reported once.
            int nul = p.Name.IndexOf('\0', StringComparison.Ordinal);
            string shown = nul >= 0 ? p.Name[..nul] : p.Name;
            if (droppedNames.Add(shown))
            {
                problems.Add($"model name '{shown}' is not a plain file name; its placements were dropped");
            }

            return true;
        });

        if (placements.Count == 0)
        {
            // The reference writes nothing for a map without a single placement.
            return new MapResult(new VmapExtractMapReport(map.Id, map.Directory, HasWdt: true, Tiled: true, 0, 0, failed), 0, []);
        }

        return Assemble(map, directory, placements, failed);
    }

    // ref: vmangos@0e3ff01 contrib/vmap_extractor/vmapextract/adtfile.cpp:94-245 (ADTFile::init: MMDX, MWMO, then MDDF and MODF entries in file order)
    private static void AddTile(List<Placement> placements, AdtFile adt, uint tileX, uint tileY, VmapModelLibrary library, Dictionary<(uint, ushort), uint> ids)
    {
        // Every doodad name is looked up (and its model extracted) as the name list is read, placed or not.
        var doodadModels = new (string Name, VmapModelInfo? Model)[adt.DoodadNames.Count];
        for (int i = 0; i < doodadModels.Length; i++)
        {
            doodadModels[i] = library.Doodad(adt.DoodadNames[i]);
        }

        // ref: vmangos@0e3ff01 contrib/vmap_extractor/vmapextract/model.cpp:152-221 (ModelInstance: an MDDF entry)
        foreach (AdtDoodad doodad in adt.Doodads)
        {
            if (doodad.NameIndex >= doodadModels.Length)
            {
                throw new InvalidDataException($"an MDDF entry names model {doodad.NameIndex} of {doodadModels.Length}.");
            }

            (string name, VmapModelInfo? model) = doodadModels[doodad.NameIndex];
            if (model is null || model.HeaderCount == 0)
            {
                continue;
            }

            uint flags = VMapFormat.ModM2 | (tileX == GlobalTile && tileY == GlobalTile ? VMapFormat.ModWorldSpawn : 0);
            placements.Add(new Placement(
                PackTile(tileX, tileY),
                flags,
                AdtId: 0,
                UniqueId(ids, doodad.UniqueId, 0),
                FixCoords(ToVector(doodad.Position)),
                ToVector(doodad.Rotation),
                doodad.ScaleRaw / 1024.0f,
                Vector3.Zero,
                Vector3.Zero,
                name,
                model));
        }

        foreach (AdtWmoObject wmo in adt.WmoObjects)
        {
            if (wmo.NameIndex >= adt.WmoNames.Count)
            {
                throw new InvalidDataException($"a MODF entry names WMO {wmo.NameIndex} of {adt.WmoNames.Count}.");
            }

            // ref: vmangos@0e3ff01 contrib/vmap_extractor/vmapextract/adtfile.cpp:183-201 (MWMO: plain name, fixed case, spaces to underscores)
            string name = ClientModelNames.FixNameSpaces(ClientModelNames.FixNameCase(ClientModelNames.PlainName(adt.WmoNames[(int)wmo.NameIndex].Replace('/', '\\'))));
            AddWmo(placements, wmo, name, tileX, tileY, library, ids);
        }
    }

    /// <summary>One MODF entry: the WMO itself, then the doodads of its doodad set.</summary>
    // ref: vmangos@0e3ff01 contrib/vmap_extractor/vmapextract/wmo.cpp:539-626 (WMOInstance)
    private static void AddWmo(List<Placement> placements, AdtWmoObject wmo, string name, uint tileX, uint tileY, VmapModelLibrary library, Dictionary<(uint, ushort), uint> ids)
    {
        VmapModelInfo? model = library.Wmo(name);
        bool worldSpawn = tileX == GlobalTile && tileY == GlobalTile;

        // The position stays as stored unless the WMO itself is placed; the doodad pass below uses whichever it is.
        Vector3 position = ToVector(wmo.Position);
        if ((wmo.Flags & DestructibleWmo) == 0 && model is not null && model.HeaderCount != 0)
        {
            if (position.X == 0 && position.Z == 0)
            {
                position.X = MapCentre;
                position.Z = MapCentre;
            }

            position = FixCoords(position);
            placements.Add(new Placement(
                PackTile(tileX, tileY),
                VMapFormat.ModHasBound | (worldSpawn ? VMapFormat.ModWorldSpawn : 0),
                wmo.NameSet,
                UniqueId(ids, wmo.UniqueId, 0),
                position,
                ToVector(wmo.Rotation),
                1.0f,
                FixCoords(ToVector(wmo.BoundsMin)),
                FixCoords(ToVector(wmo.BoundsMax)),
                name,
                model));
        }

        // ref: vmangos@0e3ff01 contrib/vmap_extractor/vmapextract/model.cpp:223-301 (Doodad::ExtractSet: only the placement's own doodad set)
        VmapWmoDoodads doodads = model?.Doodads ?? VmapWmoDoodads.None;
        if (wmo.DoodadSet >= doodads.Sets.Count)
        {
            return;
        }

        Vector3 wmoRotation = ToVector(wmo.Rotation);
        VmapRotation rotation = VmapRotation.FromEulerAnglesZyx(VmapRotation.ToRadians(wmoRotation.Y), VmapRotation.ToRadians(wmoRotation.X), VmapRotation.ToRadians(wmoRotation.Z));
        WmoDoodadSet set = doodads.Sets[wmo.DoodadSet];
        ushort doodadId = 0;
        foreach (ushort index in doodads.References)
        {
            if (index < set.FirstDoodad || index >= set.FirstDoodad + set.DoodadCount)
            {
                continue;
            }

            WmoDoodad doodad = doodads.Doodads[index];
            (string doodadName, VmapModelInfo? doodadModel) = library.Doodad(doodad.ModelPath);
            if (doodadModel is null || doodadModel.HeaderCount == 0)
            {
                continue;
            }

            doodadId++;
            Vector3 doodadPosition = position + rotation.Transform(doodad.Position);
            (float angleX, float angleY, float angleZ) = (VmapRotation.FromQuaternion(doodad.Rotation) * rotation).ToEulerAnglesXyz();
            var doodadRotation = new Vector3(VmapRotation.ToDegrees(angleY), VmapRotation.ToDegrees(angleZ), VmapRotation.ToDegrees(angleX));
            placements.Add(new Placement(
                PackTile(tileX, tileY),
                VMapFormat.ModM2 | (worldSpawn ? VMapFormat.ModWorldSpawn : 0),
                AdtId: 0,
                UniqueId(ids, wmo.UniqueId, doodadId),
                doodadPosition,
                doodadRotation,
                doodad.Scale,
                Vector3.Zero,
                Vector3.Zero,
                StoredDoodadName(doodad.ModelPath, doodadName),
                doodadModel));
        }
    }

    /// <summary>
    /// The name a WMO doodad's spawn carries. The reference turns <c>.mdx</c> into <c>.m2</c> in place and still writes the
    /// old length, so such a name is stored with its terminating NUL (<c>"Bed.m2\0"</c>), and the model file it names has no
    /// <c>.vmo</c> suffix (<see cref="VMapFormat.ModelFileName"/>).
    /// </summary>
    // ref: vmangos@0e3ff01 contrib/vmap_extractor/vmapextract/model.cpp:242-253 and :298-299
    private static string StoredDoodadName(string storedPath, string name)
        => storedPath.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase) || storedPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) ? name + "\0" : name;

    /// <summary>Builds and writes one map's tree and tile files.</summary>
    // ref: vmangos@0e3ff01 src/game/vmap/TileAssembler.cpp:69-212 (convertWorld2)
    private static MapResult Assemble(ClientMap map, string directory, List<Placement> placements, int failed)
    {
        // The first placement seen under an id is the one kept for that id.
        // ref: vmangos@0e3ff01 src/game/vmap/TileAssembler.cpp:244-246 (UniqueEntries.insert does not replace; TileEntries keeps every entry)
        var unique = new SortedDictionary<uint, ModelSpawn>();
        var models = new Dictionary<uint, VmapModelInfo>();
        foreach (Placement p in placements)
        {
            if (unique.ContainsKey(p.Id))
            {
                continue;
            }

            Vector3 low = p.BoundLow;
            Vector3 high = p.BoundHigh;
            uint flags = p.Flags;
            if ((flags & VMapFormat.ModM2) != 0)
            {
                (low, high) = TransformedBound(p);
                flags |= VMapFormat.ModHasBound;
            }
            else if ((flags & VMapFormat.ModWorldSpawn) != 0)
            {
                // ref: vmangos@0e3ff01 src/game/vmap/TileAssembler.cpp:90-101 (a global WMO's bound gets the map-centre offset its position already has)
                var offset = new Vector3(MapCentre, MapCentre, 0f);
                low += offset;
                high += offset;
            }

            unique.Add(p.Id, new ModelSpawn(flags, p.AdtId, p.Id, p.Position, p.Rotation, p.Scale, low, high, p.Name));
            models.Add(p.Id, p.Model);
        }

        // The tree is built over the spawns in id order; a spawn's slot is its place in that order.
        ModelSpawn[] spawns = [.. unique.Values];
        var slots = new Dictionary<uint, uint>(spawns.Length);
        var bounds = new (Vector3 Low, Vector3 High)[spawns.Length];
        for (int i = 0; i < spawns.Length; i++)
        {
            slots.Add(spawns[i].Id, (uint)i);
            bounds[i] = (spawns[i].BoundLow, spawns[i].BoundHigh);
        }

        BihTree tree = VmapBih.Build(bounds);

        // Entries by tile, each tile's in the order they were found.
        (uint Tile, uint Id)[] entries = [.. placements.Select((p, order) => (p.Tile, p.Id, order)).OrderBy(e => e.Tile).ThenBy(e => e.order).Select(e => (e.Tile, e.Id))];
        uint globalTile = PackTile(GlobalTile, GlobalTile);
        bool tiled = !entries.Any(e => e.Tile == globalTile);

        foreach (string stale in Directory.EnumerateFiles(directory, $"{map.Id:D3}_??_??.vmtile"))
        {
            File.Delete(stale);
        }

        long bytes = 0;
        using (var stream = new MemoryStream())
        {
            using (var w = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
            {
                // ref: vmangos@0e3ff01 src/game/vmap/TileAssembler.cpp:126-148 (magic, tiled byte, "NODE" tree, "GOBJ" global spawns with their slots)
                w.Write(Encoding.ASCII.GetBytes(VMapFormat.Magic));
                w.Write((byte)(tiled ? 1 : 0));
                w.Write("NODE"u8);
                tree.Write(w);
                w.Write("GOBJ"u8);
                foreach ((uint tile, uint id) in entries)
                {
                    if (tile == globalTile)
                    {
                        unique[id].Write(w);
                        w.Write(slots[id]);
                    }
                }
            }

            bytes += ExtractOutput.WriteWhole(Path.Combine(directory, VMapFormat.TreeFileName(map.Id)), stream.ToArray());
        }

        // ref: vmangos@0e3ff01 src/game/vmap/TileAssembler.cpp:154-188 (one file per tile: magic, count, spawns with their slots)
        var perTile = new Dictionary<uint, int>();
        foreach ((uint tile, uint _) in entries)
        {
            perTile[tile] = perTile.GetValueOrDefault(tile) + 1;
        }

        int tileFiles = 0;
        for (int t = 0; t < entries.Length; t++)
        {
            if ((unique[entries[t].Id].Flags & VMapFormat.ModWorldSpawn) != 0)
            {
                continue;
            }

            uint tile = entries[t].Tile;
            int count = perTile[tile];
            using var stream = new MemoryStream();
            using (var w = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
            {
                w.Write(Encoding.ASCII.GetBytes(VMapFormat.Magic));
                w.Write((uint)count);
                for (int s = 0; s < count; s++)
                {
                    if (s != 0)
                    {
                        t++;
                    }

                    if (t == entries.Length)
                    {
                        break;
                    }

                    unique[entries[t].Id].Write(w);
                    w.Write(slots[entries[t].Id]);
                }
            }

            // The file is named from the two numbers of the ADT's own name, in that order.
            string name = $"{map.Id:D3}_{tile >> 16:D2}_{tile & 0xFFFF:D2}.vmtile";
            bytes += ExtractOutput.WriteWhole(Path.Combine(directory, name), stream.ToArray());
            tileFiles++;
        }

        var modelFiles = new List<(string, VmapModelInfo)>();
        foreach (ModelSpawn spawn in spawns)
        {
            modelFiles.Add((VMapFormat.ModelFileName(spawn.Name), models[spawn.Id]));
        }

        return new MapResult(new VmapExtractMapReport(map.Id, map.Directory, HasWdt: true, tiled, spawns.Length, tileFiles, failed), bytes, modelFiles);
    }

    /// <summary>The internal-space box around an M2 placement: every hull vertex scaled, rotated and moved.</summary>
    // ref: vmangos@0e3ff01 src/game/vmap/TileAssembler.cpp:253-295 (calculateTransformedBound) and :51-56 (transform: scale, then rotate)
    private static (Vector3 Low, Vector3 High) TransformedBound(Placement p)
    {
        VmapRotation rotation = VmapRotation.FromEulerAnglesZyx(VmapRotation.SpawnRadians(p.Rotation.Y), VmapRotation.SpawnRadians(p.Rotation.X), VmapRotation.SpawnRadians(p.Rotation.Z));
        Vector3 low = default;
        Vector3 high = default;
        bool empty = true;
        foreach (Vector3 vertex in p.Model.Hull)
        {
            Vector3 v = rotation.Transform(vertex * p.Scale);
            if (empty)
            {
                low = v;
                high = v;
                empty = false;
            }
            else
            {
                low = new Vector3(low.X < v.X ? low.X : v.X, low.Y < v.Y ? low.Y : v.Y, low.Z < v.Z ? low.Z : v.Z);
                high = new Vector3(v.X < high.X ? high.X : v.X, v.Y < high.Y ? high.Y : v.Y, v.Z < high.Z ? high.Z : v.Z);
            }
        }

        return (low + p.Position, high + p.Position);
    }

    // ref: vmangos@0e3ff01 contrib/vmap_extractor/vmapextract/vmapexport.cpp:87-92 (ids count up from 1 in the order (client id, doodad number) pairs are first seen)
    private static uint UniqueId(Dictionary<(uint, ushort), uint> ids, uint clientId, ushort doodadId)
    {
        if (!ids.TryGetValue((clientId, doodadId), out uint id))
        {
            id = (uint)ids.Count + 1;
            ids.Add((clientId, doodadId), id);
        }

        return id;
    }

    // ref: vmangos@0e3ff01 src/game/vmap/MapTree.h:71-72
    private static uint PackTile(uint tileX, uint tileY) => (tileX << 16) | tileY;

    // ref: vmangos@0e3ff01 contrib/vmap_extractor/vmapextract/wmo.h:69 (fixCoords: (z, x, y))
    private static Vector3 FixCoords(Vector3 v) => new(v.Z, v.X, v.Y);

    // Placement fields are stored as 32-bit floats; the readers widen them without loss.
    private static Vector3 ToVector(WorldPoint p) => new((float)p.X, (float)p.Y, (float)p.Z);
}

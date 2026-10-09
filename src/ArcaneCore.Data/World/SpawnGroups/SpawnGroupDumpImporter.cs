using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Data.World.GameObjects;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.World.SpawnGroups;

/// <summary>What a spawn group import read (rows that would be, or were, written).</summary>
public sealed record SpawnGroupImportReport(
    int GameObjectSpawnEntries, int Groups, int Spawns, int Entries, int Formations, int LinkedGroups, IReadOnlyList<string> Warnings);

/// <summary>
/// What a refresh wrote into an empty <c>gameobject_spawn_entry</c> and empty spawn group tables; a null count means the world already
/// had rows there and that part was left alone.
/// </summary>
public sealed record SpawnGroupFillReport(int? GameObjectSpawnEntries, int? Groups, int Spawns, int Entries, int Formations, int LinkedGroups, int SkippedMembers);

/// <summary>
/// Reads cmangos classic-db's <c>gameobject_spawn_entry</c> and spawn group tables (<c>spawn_group</c>, <c>spawn_group_spawn</c>,
/// <c>spawn_group_entry</c>, <c>spawn_group_formation</c>, <c>spawn_group_linked_group</c>) BY COLUMN NAME onto
/// <see cref="SpawnGroupDataModule"/>'s rows, and the <c>guid</c> and <c>id</c> of every <c>creature</c> and <c>gameobject</c> row (the
/// refresh compares them with the world's spawns). vmangos has none of these tables. The rows are written as the dump has them: the world
/// skips what cmangos ObjectMgr::LoadSpawnGroups would skip (a spawn that does not exist, a second group for one spawn, an entry without
/// a template) when it loads. Nothing is bundled; the operator points the importer at their own dump.
/// </summary>
public sealed class SpawnGroupDumpImporter
{
    private static readonly RowMapper<GameObjectSpawnEntryRow> s_spawnEntries = new(new Dictionary<string, string> { ["guid"] = nameof(GameObjectSpawnEntryRow.SpawnGuid) });
    private static readonly RowMapper<SpawnGroupRow> s_groups = new();
    private static readonly RowMapper<SpawnGroupSpawnRow> s_spawns = new();
    private static readonly RowMapper<SpawnGroupEntryRow> s_entries = new();
    private static readonly RowMapper<SpawnGroupFormationRow> s_formations = new();
    private static readonly RowMapper<SpawnGroupLinkedGroupRow> s_links = new();

    private readonly Dictionary<(uint, uint), GameObjectSpawnEntryRow> _spawnEntries = [];
    private readonly Dictionary<uint, SpawnGroupRow> _groups = [];
    private readonly Dictionary<(uint, uint), SpawnGroupSpawnRow> _spawns = [];
    private readonly Dictionary<(uint, uint), SpawnGroupEntryRow> _entries = [];
    private readonly Dictionary<uint, SpawnGroupFormationRow> _formations = [];
    private readonly Dictionary<(uint, uint), SpawnGroupLinkedGroupRow> _links = [];
    private readonly Dictionary<uint, uint> _dumpCreatures = [];
    private readonly Dictionary<uint, uint> _dumpGameObjects = [];
    private readonly MapDiagnostics _diagnostics = new();

    /// <summary>Read one dump (call again for further files; later rows replace earlier ones with the same key).</summary>
    public void Read(TextReader dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        foreach (object item in new MySqlDumpReader(dump).Read())
        {
            if (item is not DumpRow row)
            {
                continue;
            }

            switch (row.Table.ToLowerInvariant())
            {
                case "creature":
                    if (row.Has("guid") && row.Has("id"))
                    {
                        _dumpCreatures[UInt(row, "guid")] = UInt(row, "id");
                    }

                    break;
                case "gameobject":
                    if (row.Has("guid") && row.Has("id"))
                    {
                        _dumpGameObjects[UInt(row, "guid")] = UInt(row, "id");
                    }

                    break;
                case SpawnGroupDataModule.GameObjectSpawnEntryTable:
                    Require(row, "guid", "entry");
                    GameObjectSpawnEntryRow spawnEntry = s_spawnEntries.Map(row, _diagnostics);
                    _spawnEntries[(spawnEntry.SpawnGuid, spawnEntry.Entry)] = spawnEntry;
                    break;
                case SpawnGroupDataModule.GroupTable:
                    Require(row, "Id", "Type");
                    SpawnGroupRow group = s_groups.Map(row, _diagnostics);
                    _groups[group.Id] = group;
                    break;
                case SpawnGroupDataModule.SpawnTable:
                    Require(row, "Id", "Guid");
                    SpawnGroupSpawnRow spawn = s_spawns.Map(row, _diagnostics);
                    if (!row.Has("SlotId"))
                    {
                        spawn.SlotId = -1;
                    }

                    _spawns[(spawn.Id, spawn.Guid)] = spawn;
                    break;
                case SpawnGroupDataModule.EntryTable:
                    Require(row, "Id", "Entry");
                    SpawnGroupEntryRow entry = s_entries.Map(row, _diagnostics);
                    _entries[(entry.Id, entry.Entry)] = entry;
                    break;
                case SpawnGroupDataModule.FormationTable:
                    Require(row, "Id");
                    SpawnGroupFormationRow formation = s_formations.Map(row, _diagnostics);
                    _formations[formation.Id] = formation;
                    break;
                case SpawnGroupDataModule.LinkedGroupTable:
                    Require(row, "Id", "LinkedId");
                    SpawnGroupLinkedGroupRow link = s_links.Map(row, _diagnostics);
                    _links[(link.Id, link.LinkedId)] = link;
                    break;
            }
        }
    }

    /// <summary>Whether the dumps carried any of the six tables.</summary>
    public bool HasRows => _spawnEntries.Count + _groups.Count + _spawns.Count + _entries.Count + _formations.Count + _links.Count > 0;

    public SpawnGroupImportReport BuildReport() => new(
        _spawnEntries.Count, _groups.Count, _spawns.Count, _entries.Count, _formations.Count, _links.Count, _diagnostics.Samples);

    /// <summary>The rows as read (tests; the refresh's sources).</summary>
    public IReadOnlyCollection<GameObjectSpawnEntryRow> SpawnEntrySnapshot() => _spawnEntries.Values;

    public IReadOnlyCollection<SpawnGroupRow> GroupSnapshot() => _groups.Values;

    public IReadOnlyCollection<SpawnGroupSpawnRow> SpawnSnapshot() => _spawns.Values;

    public IReadOnlyCollection<SpawnGroupEntryRow> EntrySnapshot() => _entries.Values;

    public IReadOnlyCollection<SpawnGroupFormationRow> FormationSnapshot() => _formations.Values;

    public IReadOnlyCollection<SpawnGroupLinkedGroupRow> LinkSnapshot() => _links.Values;

    /// <summary>The dump's <c>creature.guid</c> to <c>creature.id</c> (the refresh's member check).</summary>
    public IReadOnlyDictionary<uint, uint> DumpCreatureEntries => _dumpCreatures;

    /// <summary>The dump's <c>gameobject.guid</c> to <c>gameobject.id</c>.</summary>
    public IReadOnlyDictionary<uint, uint> DumpGameObjectEntries => _dumpGameObjects;

    /// <summary>
    /// Write the six tables atomically with the contract of <see cref="ImportTransaction"/>: with <paramref name="replace"/> they are emptied
    /// first, without it an existing key fails the write and nothing changes.
    /// </summary>
    public async Task<SpawnGroupImportReport> WriteAsync(WorldDbContext db, bool replace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        await ImportTransaction.RunAsync(db, async token =>
        {
            bool detect = db.ChangeTracker.AutoDetectChangesEnabled;
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            try
            {
                if (replace)
                {
                    await DeleteAllAsync(db, token).ConfigureAwait(false);
                }

                await InsertAsync(db, _spawnEntries.Values, _groups.Values, _spawns.Values, _entries.Values, _formations.Values, _links.Values, token)
                    .ConfigureAwait(false);
            }
            finally
            {
                db.ChangeTracker.AutoDetectChangesEnabled = detect;
            }
        }, cancellationToken).ConfigureAwait(false);
        return BuildReport();
    }

    /// <summary>
    /// The refresh rule (as for <c>creature_spawn_entry</c>): fill each part only when the world has none of its rows, and only for the
    /// world's own spawns. <c>gameobject_spawn_entry</c> rows are written for a world spawn whose entry is 0 or one of the dump's entries for
    /// that guid. A spawn group member is written when the world has that spawn with the entry the dump's <c>creature</c> or
    /// <c>gameobject</c> row gives it (0 included), so a world built from other data, whose guids mean other spawns, gets no group; a group is
    /// written when one of its members is, with its entry and formation rows, and a link when both of its groups are written. The caller
    /// owns the transaction.
    /// </summary>
    public async Task<SpawnGroupFillReport> FillAsync(WorldDbContext db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        int? spawnEntries = null;
        if (_spawnEntries.Count > 0 && !await db.Set<GameObjectSpawnEntryRow>().AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            Dictionary<uint, uint> worldObjects = await db.Set<GameObjectSpawnRow>().AsNoTracking()
                .ToDictionaryAsync(r => r.Guid, r => r.Entry, cancellationToken).ConfigureAwait(false);
            GameObjectSpawnEntryRow[] rows = [.. _spawnEntries.Values
                .GroupBy(r => r.SpawnGuid)
                .Where(g => worldObjects.TryGetValue(g.Key, out uint entry) && (entry == 0 || g.Any(r => r.Entry == entry)))
                .SelectMany(g => g)
                .OrderBy(r => r.SpawnGuid).ThenBy(r => r.Entry)
                .Select(r => new GameObjectSpawnEntryRow { SpawnGuid = r.SpawnGuid, Entry = r.Entry })];
            await ImportBatch.InsertAsync(db, rows, cancellationToken).ConfigureAwait(false);
            spawnEntries = rows.Length;
        }

        if (_groups.Count == 0 || await db.Set<SpawnGroupRow>().AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return new SpawnGroupFillReport(spawnEntries, null, 0, 0, 0, 0, 0);
        }

        Dictionary<uint, uint> worldCreatures = await db.Set<CreatureSpawnRow>().AsNoTracking()
            .ToDictionaryAsync(r => r.Guid, r => r.Entry, cancellationToken).ConfigureAwait(false);
        Dictionary<uint, uint> worldGameObjects = await db.Set<GameObjectSpawnRow>().AsNoTracking()
            .ToDictionaryAsync(r => r.Guid, r => r.Entry, cancellationToken).ConfigureAwait(false);
        int skipped = 0;
        var spawns = new List<SpawnGroupSpawnRow>();
        foreach (SpawnGroupSpawnRow spawn in _spawns.Values.OrderBy(s => s.Id).ThenBy(s => s.Guid))
        {
            if (!_groups.TryGetValue(spawn.Id, out SpawnGroupRow? group))
            {
                skipped++;
                continue;
            }

            (Dictionary<uint, uint> world, Dictionary<uint, uint> dump) = group.Type == 0
                ? (worldCreatures, _dumpCreatures)
                : (worldGameObjects, _dumpGameObjects);
            if (world.TryGetValue(spawn.Guid, out uint worldEntry) && dump.TryGetValue(spawn.Guid, out uint dumpEntry) && worldEntry == dumpEntry)
            {
                spawns.Add(Copy(spawn));
            }
            else
            {
                skipped++;
            }
        }

        HashSet<uint> kept = [.. spawns.Select(s => s.Id)];
        SpawnGroupRow[] groups = [.. _groups.Values.Where(g => kept.Contains(g.Id)).OrderBy(g => g.Id).Select(Copy)];
        SpawnGroupEntryRow[] entries = [.. _entries.Values.Where(e => kept.Contains(e.Id)).OrderBy(e => e.Id).ThenBy(e => e.Entry).Select(Copy)];
        SpawnGroupFormationRow[] formations = [.. _formations.Values.Where(f => kept.Contains(f.Id)).OrderBy(f => f.Id).Select(Copy)];
        SpawnGroupLinkedGroupRow[] links = [.. _links.Values.Where(l => kept.Contains(l.Id) && kept.Contains(l.LinkedId))
            .OrderBy(l => l.Id).ThenBy(l => l.LinkedId).Select(l => new SpawnGroupLinkedGroupRow { Id = l.Id, LinkedId = l.LinkedId })];
        await InsertAsync(db, [], groups, spawns, entries, formations, links, cancellationToken).ConfigureAwait(false);
        return new SpawnGroupFillReport(spawnEntries, groups.Length, spawns.Count, entries.Length, formations.Length, links.Length, skipped);
    }

    private static async Task DeleteAllAsync(WorldDbContext db, CancellationToken token)
    {
        await db.Set<GameObjectSpawnEntryRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
        await db.Set<SpawnGroupRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
        await db.Set<SpawnGroupSpawnRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
        await db.Set<SpawnGroupEntryRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
        await db.Set<SpawnGroupFormationRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
        await db.Set<SpawnGroupLinkedGroupRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
    }

    private static async Task InsertAsync(
        WorldDbContext db, IEnumerable<GameObjectSpawnEntryRow> spawnEntries, IEnumerable<SpawnGroupRow> groups, IEnumerable<SpawnGroupSpawnRow> spawns,
        IEnumerable<SpawnGroupEntryRow> entries, IEnumerable<SpawnGroupFormationRow> formations, IEnumerable<SpawnGroupLinkedGroupRow> links,
        CancellationToken token)
    {
        await ImportBatch.InsertAsync(db, spawnEntries.OrderBy(r => r.SpawnGuid).ThenBy(r => r.Entry).ToArray(), token).ConfigureAwait(false);
        await ImportBatch.InsertAsync(db, groups.OrderBy(r => r.Id).ToArray(), token).ConfigureAwait(false);
        await ImportBatch.InsertAsync(db, spawns.OrderBy(r => r.Id).ThenBy(r => r.Guid).ToArray(), token).ConfigureAwait(false);
        await ImportBatch.InsertAsync(db, entries.OrderBy(r => r.Id).ThenBy(r => r.Entry).ToArray(), token).ConfigureAwait(false);
        await ImportBatch.InsertAsync(db, formations.OrderBy(r => r.Id).ToArray(), token).ConfigureAwait(false);
        await ImportBatch.InsertAsync(db, links.OrderBy(r => r.Id).ThenBy(r => r.LinkedId).ToArray(), token).ConfigureAwait(false);
    }

    private static SpawnGroupRow Copy(SpawnGroupRow r) => new()
    {
        Id = r.Id, Name = r.Name, Type = r.Type, MaxCount = r.MaxCount, WorldState = r.WorldState,
        WorldStateExpression = r.WorldStateExpression, Flags = r.Flags, StringId = r.StringId,
    };

    private static SpawnGroupSpawnRow Copy(SpawnGroupSpawnRow r) => new() { Id = r.Id, Guid = r.Guid, SlotId = r.SlotId, Chance = r.Chance };

    private static SpawnGroupEntryRow Copy(SpawnGroupEntryRow r) => new()
    {
        Id = r.Id, Entry = r.Entry, MinCount = r.MinCount, MaxCount = r.MaxCount, Chance = r.Chance,
    };

    private static SpawnGroupFormationRow Copy(SpawnGroupFormationRow r) => new()
    {
        Id = r.Id, FormationType = r.FormationType, FormationSpread = r.FormationSpread, FormationOptions = r.FormationOptions,
        PathId = r.PathId, MovementType = r.MovementType, Comment = r.Comment,
    };

    private static uint UInt(DumpRow row, string column)
        => row.TryGet(out string? raw, column) && uint.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out uint value)
            ? value : 0;

    private static void Require(DumpRow row, params ReadOnlySpan<string> columns)
    {
        foreach (string column in columns)
        {
            if (!row.Has(column))
            {
                throw new ImportSchemaException(row.Table, column, $"table `{row.Table}` has no column `{column}`");
            }
        }

        if (row.Values.Count != row.Columns.Count)
        {
            throw new ImportSchemaException(row.Table, null, $"table `{row.Table}`: a row has {row.Values.Count} values but {row.Columns.Count} columns");
        }
    }
}

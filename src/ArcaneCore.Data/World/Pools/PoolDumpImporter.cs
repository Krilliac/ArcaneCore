using System.Globalization;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Data.World.GameObjects;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.World.Pools;

/// <summary>What a pool import read (rows that would be, or were, written) and the vmangos rows outside patch 10 it dropped.</summary>
public sealed record PoolImportReport(
    int Templates, int Creatures, int CreatureTemplates, int GameObjects, int GameObjectTemplates, int PoolPools, int RowsOutsidePatch,
    IReadOnlyList<string> Warnings);

/// <summary>
/// What a refresh wrote into empty pool tables; <see cref="Templates"/> is null when the world already had pools and the six tables were left
/// alone. <see cref="SkippedMembers"/> counts the <c>pool_creature</c> / <c>pool_gameobject</c> rows left out because the world has no such
/// spawn, or one with another entry.
/// </summary>
public sealed record PoolFillReport(
    int? Templates, int Creatures, int CreatureTemplates, int GameObjects, int GameObjectTemplates, int PoolPools, int SkippedMembers);

/// <summary>
/// Reads the cmangos pool tables (<c>pool_template</c>, <c>pool_creature</c>, <c>pool_creature_template</c>, <c>pool_gameobject</c>,
/// <c>pool_gameobject_template</c>, <c>pool_pool</c>) BY COLUMN NAME onto <see cref="PoolDataModule"/>'s rows, and the <c>guid</c> and
/// <c>id</c> of every <c>creature</c> and <c>gameobject</c> row (the refresh compares them with the world's spawns). A vmangos dump's pool
/// tables carry <c>patch_min</c>/<c>patch_max</c>; only rows whose range holds patch 10 (1.12) are kept, as vmangos
/// PoolManager::LoadFromDB selects them (<c>WHERE 10 BETWEEN patch_min AND patch_max</c>). The rows are written as the dump has them: the
/// world drops at load what cmangos PoolManager::LoadFromDB would drop (a spawn that does not exist, a pool id above the largest template,
/// a chance outside 0..100, a member on another map than its pool, a circular pool link). Nothing is bundled; the operator points the
/// importer at their own dump.
/// </summary>
public sealed class PoolDumpImporter
{
    private const int SupportedPatch = 10;

    private static readonly RowMapper<PoolTemplateRow> s_templates = new();
    private static readonly RowMapper<PoolCreatureRow> s_creatures = new();
    private static readonly RowMapper<PoolCreatureTemplateRow> s_creatureTemplates = new();
    private static readonly RowMapper<PoolGameObjectRow> s_gameObjects = new();
    private static readonly RowMapper<PoolGameObjectTemplateRow> s_gameObjectTemplates = new();
    private static readonly RowMapper<PoolPoolRow> s_pools = new();

    private readonly Dictionary<uint, PoolTemplateRow> _templates = [];
    private readonly Dictionary<uint, PoolCreatureRow> _creatures = [];
    private readonly Dictionary<uint, PoolCreatureTemplateRow> _creatureTemplates = [];
    private readonly Dictionary<uint, PoolGameObjectRow> _gameObjects = [];
    private readonly Dictionary<uint, PoolGameObjectTemplateRow> _gameObjectTemplates = [];
    private readonly Dictionary<uint, PoolPoolRow> _pools = [];
    private readonly Dictionary<uint, uint> _dumpCreatures = [];
    private readonly Dictionary<uint, uint> _dumpGameObjects = [];
    private readonly MapDiagnostics _diagnostics = new();
    private int _outsidePatch;

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
                case PoolDataModule.TemplateTable:
                    Require(row, "entry", "max_limit");
                    if (InPatch(row))
                    {
                        PoolTemplateRow template = s_templates.Map(row, _diagnostics);
                        _templates[template.Entry] = template;
                    }

                    break;
                case PoolDataModule.CreatureTable:
                    Require(row, "guid", "pool_entry");
                    if (InPatch(row))
                    {
                        PoolCreatureRow creature = s_creatures.Map(row, _diagnostics);
                        _creatures[creature.Guid] = creature;
                    }

                    break;
                case PoolDataModule.CreatureTemplateTable:
                    Require(row, "id", "pool_entry");
                    if (InPatch(row))
                    {
                        PoolCreatureTemplateRow creatureTemplate = s_creatureTemplates.Map(row, _diagnostics);
                        _creatureTemplates[creatureTemplate.Id] = creatureTemplate;
                    }

                    break;
                case PoolDataModule.GameObjectTable:
                    Require(row, "guid", "pool_entry");
                    if (InPatch(row))
                    {
                        PoolGameObjectRow gameObject = s_gameObjects.Map(row, _diagnostics);
                        _gameObjects[gameObject.Guid] = gameObject;
                    }

                    break;
                case PoolDataModule.GameObjectTemplateTable:
                    Require(row, "id", "pool_entry");
                    if (InPatch(row))
                    {
                        PoolGameObjectTemplateRow gameObjectTemplate = s_gameObjectTemplates.Map(row, _diagnostics);
                        _gameObjectTemplates[gameObjectTemplate.Id] = gameObjectTemplate;
                    }

                    break;
                case PoolDataModule.PoolPoolTable:
                    Require(row, "pool_id", "mother_pool");
                    if (InPatch(row))
                    {
                        PoolPoolRow pool = s_pools.Map(row, _diagnostics);
                        _pools[pool.PoolId] = pool;
                    }

                    break;
            }
        }
    }

    /// <summary>Whether the dumps carried any of the six tables.</summary>
    public bool HasRows => _templates.Count + _creatures.Count + _creatureTemplates.Count + _gameObjects.Count + _gameObjectTemplates.Count + _pools.Count > 0;

    public PoolImportReport BuildReport() => new(
        _templates.Count, _creatures.Count, _creatureTemplates.Count, _gameObjects.Count, _gameObjectTemplates.Count, _pools.Count, _outsidePatch,
        _diagnostics.Samples);

    public IReadOnlyCollection<PoolTemplateRow> TemplateSnapshot() => _templates.Values;

    public IReadOnlyCollection<PoolCreatureRow> CreatureSnapshot() => _creatures.Values;

    public IReadOnlyCollection<PoolCreatureTemplateRow> CreatureTemplateSnapshot() => _creatureTemplates.Values;

    public IReadOnlyCollection<PoolGameObjectRow> GameObjectSnapshot() => _gameObjects.Values;

    public IReadOnlyCollection<PoolGameObjectTemplateRow> GameObjectTemplateSnapshot() => _gameObjectTemplates.Values;

    public IReadOnlyCollection<PoolPoolRow> PoolSnapshot() => _pools.Values;

    /// <summary>
    /// Write the six tables atomically with the contract of <see cref="ImportTransaction"/>: with <paramref name="replace"/> they are emptied
    /// first, without it an existing key fails the write and nothing changes.
    /// </summary>
    public async Task<PoolImportReport> WriteAsync(WorldDbContext db, bool replace, CancellationToken cancellationToken = default)
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

                await InsertAsync(db, _templates.Values, _creatures.Values, _creatureTemplates.Values, _gameObjects.Values, _gameObjectTemplates.Values,
                    _pools.Values, token).ConfigureAwait(false);
            }
            finally
            {
                db.ChangeTracker.AutoDetectChangesEnabled = detect;
            }
        }, cancellationToken).ConfigureAwait(false);
        return BuildReport();
    }

    /// <summary>
    /// The refresh rule (as for the spawn groups): the six tables are filled only when the world has no <c>pool_template</c> row at all, and
    /// a <c>pool_creature</c> / <c>pool_gameobject</c> row is written only when the world has that spawn with the entry the dump's
    /// <c>creature</c> / <c>gameobject</c> row gives it (0 included), so a world built from other data, whose guids mean other spawns, pools
    /// none of them. The templates, the pool links and the entry-keyed rows are written as the dump has them (an entry row pools every spawn
    /// of its entry the world has, which is what it means). The caller owns the transaction.
    /// </summary>
    public async Task<PoolFillReport> FillAsync(WorldDbContext db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (_templates.Count == 0 || await db.Set<PoolTemplateRow>().AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return new PoolFillReport(null, 0, 0, 0, 0, 0, 0);
        }

        Dictionary<uint, uint> worldCreatures = await db.Set<CreatureSpawnRow>().AsNoTracking()
            .ToDictionaryAsync(r => r.Guid, r => r.Entry, cancellationToken).ConfigureAwait(false);
        Dictionary<uint, uint> worldGameObjects = await db.Set<GameObjectSpawnRow>().AsNoTracking()
            .ToDictionaryAsync(r => r.Guid, r => r.Entry, cancellationToken).ConfigureAwait(false);
        int skipped = 0;
        PoolCreatureRow[] creatures = [.. Kept(_creatures.Values, worldCreatures, _dumpCreatures)];
        PoolGameObjectRow[] gameObjects = [.. Kept(_gameObjects.Values, worldGameObjects, _dumpGameObjects)];
        await InsertAsync(db, _templates.Values, creatures, _creatureTemplates.Values, gameObjects, _gameObjectTemplates.Values, _pools.Values,
            cancellationToken).ConfigureAwait(false);
        return new PoolFillReport(_templates.Count, creatures.Length, _creatureTemplates.Count, gameObjects.Length, _gameObjectTemplates.Count, _pools.Count, skipped);

        IEnumerable<T> Kept<T>(IEnumerable<T> rows, Dictionary<uint, uint> world, Dictionary<uint, uint> dump)
            where T : PoolSpawnRowBase
        {
            foreach (T row in rows)
            {
                if (world.TryGetValue(row.Guid, out uint worldEntry) && dump.TryGetValue(row.Guid, out uint dumpEntry) && worldEntry == dumpEntry)
                {
                    yield return row;
                }
                else
                {
                    skipped++;
                }
            }
        }
    }

    private static async Task DeleteAllAsync(WorldDbContext db, CancellationToken token)
    {
        await db.Set<PoolTemplateRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
        await db.Set<PoolCreatureRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
        await db.Set<PoolCreatureTemplateRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
        await db.Set<PoolGameObjectRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
        await db.Set<PoolGameObjectTemplateRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
        await db.Set<PoolPoolRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
    }

    private static async Task InsertAsync(
        WorldDbContext db, IEnumerable<PoolTemplateRow> templates, IEnumerable<PoolCreatureRow> creatures, IEnumerable<PoolCreatureTemplateRow> creatureTemplates,
        IEnumerable<PoolGameObjectRow> gameObjects, IEnumerable<PoolGameObjectTemplateRow> gameObjectTemplates, IEnumerable<PoolPoolRow> pools,
        CancellationToken token)
    {
        await ImportBatch.InsertAsync(db, templates.OrderBy(r => r.Entry).Select(Copy).ToArray(), token).ConfigureAwait(false);
        await ImportBatch.InsertAsync(db, creatures.OrderBy(r => r.Guid).Select(r => CopySpawn<PoolCreatureRow>(r)).ToArray(), token).ConfigureAwait(false);
        await ImportBatch.InsertAsync(db, creatureTemplates.OrderBy(r => r.Id).Select(r => CopyEntry<PoolCreatureTemplateRow>(r)).ToArray(), token).ConfigureAwait(false);
        await ImportBatch.InsertAsync(db, gameObjects.OrderBy(r => r.Guid).Select(r => CopySpawn<PoolGameObjectRow>(r)).ToArray(), token).ConfigureAwait(false);
        await ImportBatch.InsertAsync(db, gameObjectTemplates.OrderBy(r => r.Id).Select(r => CopyEntry<PoolGameObjectTemplateRow>(r)).ToArray(), token).ConfigureAwait(false);
        await ImportBatch.InsertAsync(db, pools.OrderBy(r => r.PoolId).Select(Copy).ToArray(), token).ConfigureAwait(false);
    }

    private static PoolTemplateRow Copy(PoolTemplateRow r) => new() { Entry = r.Entry, MaxLimit = r.MaxLimit, Description = r.Description };

    private static PoolPoolRow Copy(PoolPoolRow r) => new() { PoolId = r.PoolId, MotherPool = r.MotherPool, Chance = r.Chance, Description = r.Description };

    private static T CopySpawn<T>(PoolSpawnRowBase r)
        where T : PoolSpawnRowBase, new()
        => new() { Guid = r.Guid, PoolEntry = r.PoolEntry, Chance = r.Chance, Description = r.Description };

    private static T CopyEntry<T>(PoolEntryRowBase r)
        where T : PoolEntryRowBase, new()
        => new() { Id = r.Id, PoolEntry = r.PoolEntry, Chance = r.Chance, Description = r.Description };

    /// <summary>A vmangos row is kept when its patch range holds patch 10; a classic-db row has no patch columns and is always kept.</summary>
    private bool InPatch(DumpRow row)
    {
        if (!row.Has("patch_min") && !row.Has("patch_max"))
        {
            return true;
        }

        uint min = row.Has("patch_min") ? UInt(row, "patch_min") : 0;
        uint max = row.Has("patch_max") ? UInt(row, "patch_max") : uint.MaxValue;
        if (min <= SupportedPatch && SupportedPatch <= max)
        {
            return true;
        }

        _outsidePatch++;
        return false;
    }

    private static uint UInt(DumpRow row, string column)
        => row.TryGet(out string? raw, column) && uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint value) ? value : 0;

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

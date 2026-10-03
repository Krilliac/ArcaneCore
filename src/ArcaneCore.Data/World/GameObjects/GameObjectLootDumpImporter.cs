using System.Globalization;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.World.Creatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ArcaneCore.Data.World.GameObjects;

/// <summary>What a game object / loot import read and wrote.</summary>
public sealed record GameObjectLootImportReport(
    int Templates,
    int Spawns,
    int QuestStarters,
    int QuestEnders,
    int Locks,
    int LootRows,
    int CreatureLootInfos,
    int SkippedRows,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Maps the game object and loot tables of a cmangos classic-db or vmangos world dump into the
/// world v7 schema by column <b>name</b> (as <see cref="CreatureDumpImporter"/>), plus Lock.dbc
/// through <see cref="ReadLocks"/>. The dumps and DBCs are GPL / proprietary data and are never
/// committed; tests use hand-written rows in each layout.
/// <para>
/// cmangos columns: <c>gameobject_template.entry,type,displayId,name,faction,flags,size,data0..23</c>,
/// <c>gameobject.guid,id,map,position_*,orientation,rotation0..3,spawntimesecs[min],animprogress,state</c>,
/// <c>*_loot_template.entry,item,ChanceOrQuestChance,groupid,mincountOrRef,maxcount,condition_id</c> and the
/// <c>creature_template.LootId,SkinningLootId,MinLootGold,MaxLootGold</c> columns. vmangos uses
/// <c>loot_id,skinning_loot_id,gold_min,gold_max</c> and patch-versioned rows: the template with the
/// highest <c>patch</c> ≤ 10 and the rows whose <c>patch_min..patch_max</c> contains 10 are used.
/// </para>
/// </summary>
public sealed class GameObjectLootDumpImporter
{
    /// <summary>vmangos WowPatch for 1.12.1.</summary>
    public const int MaxPatch = CreatureDumpImporter.MaxPatch;

    /// <summary>Lock.dbc 1.12.1: ID, Type[8], Index[8], Skill[8], Action[8] (vmangos LockEntryfmt).</summary>
    public const int LockFieldCount = 33;

    private static readonly Dictionary<string, Func<LootTemplateRowBase>> s_lootTables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["creature_loot_template"] = () => new CreatureLootTemplateRow(),
        ["gameobject_loot_template"] = () => new GameObjectLootTemplateRow(),
        ["item_loot_template"] = () => new ItemLootTemplateRow(),
        ["skinning_loot_template"] = () => new SkinningLootTemplateRow(),
        ["reference_loot_template"] = () => new ReferenceLootTemplateRow(),
    };

    private readonly Dictionary<uint, (int Patch, GameObjectTemplateRow Row)> _templates = [];
    private readonly Dictionary<uint, GameObjectSpawnRow> _spawns = [];
    private readonly HashSet<(uint, uint)> _starters = [];
    private readonly HashSet<(uint, uint)> _enders = [];
    private readonly Dictionary<uint, LockTemplateRow> _locks = [];
    private readonly Dictionary<(Type, uint, uint), LootTemplateRowBase> _loot = [];
    private readonly Dictionary<uint, (int Patch, CreatureLootInfoRow Row)> _creatureLoot = [];
    private readonly List<string> _warnings = [];
    private int _skipped;

    /// <summary>Read one dump (call again for further files; later rows replace earlier ones with the same key).</summary>
    public void Read(TextReader dump)
    {
        var reader = new MySqlDumpReader(dump);
        foreach (object item in reader.Read())
        {
            if (item is not DumpRow row)
            {
                continue;
            }

            string table = row.Table.ToLowerInvariant();
            switch (table)
            {
                case "gameobject_template":
                    ReadTemplate(row);
                    break;
                case "gameobject":
                    ReadSpawn(row);
                    break;
                case "gameobject_questrelation":
                    ReadRelation(row, _starters);
                    break;
                case "gameobject_involvedrelation":
                    ReadRelation(row, _enders);
                    break;
                case "creature_template":
                    ReadCreatureLoot(row);
                    break;
                default:
                    if (s_lootTables.TryGetValue(table, out Func<LootTemplateRowBase>? create))
                    {
                        ReadLoot(row, create());
                    }

                    break;
            }
        }
    }

    /// <summary>Read Lock.dbc (vmangos sLockStore). Throws on a file that does not have the 1.12.1 layout.</summary>
    public void ReadLocks(DbcFile dbc)
    {
        ArgumentNullException.ThrowIfNull(dbc);
        if (dbc.FieldCount != LockFieldCount)
        {
            throw new InvalidDataException($"Lock.dbc has {dbc.FieldCount} fields; build 5875 has {LockFieldCount}");
        }

        for (int r = 0; r < dbc.RecordCount; r++)
        {
            var row = new LockTemplateRow { Id = dbc.GetUInt32(r, 0) };
            var types = new uint[8];
            var indexes = new uint[8];
            var skills = new uint[8];
            for (int i = 0; i < 8; i++)
            {
                types[i] = dbc.GetUInt32(r, 1 + i);
                indexes[i] = dbc.GetUInt32(r, 9 + i);
                skills[i] = dbc.GetUInt32(r, 17 + i);
            }

            row.Set(types, indexes, skills);
            _locks[row.Id] = row;
        }
    }

    /// <summary>
    /// Write everything read so far atomically, with the same transaction contract as
    /// <see cref="CreatureDumpImporter.WriteAsync"/>: an empty change tracker is required, a
    /// caller's EF transaction is protected by a savepoint and stays the caller's, otherwise
    /// this method owns the transaction; a failure or cancellation restores the previous rows.
    /// With <paramref name="replace"/> the eleven tables are emptied first.
    /// </summary>
    public async Task<GameObjectLootImportReport> WriteAsync(WorldDbContext db, bool replace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (db.ChangeTracker.Entries().Any())
        {
            throw new InvalidOperationException("Game object/loot imports require an empty change tracker; save caller changes and clear tracking, or use a dedicated context.");
        }

        IDbContextTransaction? callerTransaction = db.Database.CurrentTransaction;
        if (callerTransaction is null && System.Transactions.Transaction.Current is not null)
        {
            throw new InvalidOperationException("Game object/loot imports require an explicit EF transaction when a caller owns the transaction; ambient transactions are not supported.");
        }

        if (callerTransaction is { SupportsSavepoints: false })
        {
            throw new InvalidOperationException("The caller's transaction does not support savepoints; the import cannot protect its existing work.");
        }

        await using IDbContextTransaction? ownedTransaction = callerTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        IDbContextTransaction transaction = callerTransaction ?? ownedTransaction!;
        string? savepoint = callerTransaction is not null ? "ArcaneGoLootImport_" + Guid.NewGuid().ToString("N") : null;
        if (savepoint is not null)
        {
            await transaction.CreateSavepointAsync(savepoint, cancellationToken).ConfigureAwait(false);
        }

        bool detect = db.ChangeTracker.AutoDetectChangesEnabled;
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            if (replace)
            {
                await db.Set<GameObjectSpawnRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await db.Set<GameObjectTemplateRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await db.Set<GameObjectQuestStarterRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await db.Set<GameObjectQuestEnderRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await db.Set<LockTemplateRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await db.Set<CreatureLootTemplateRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await db.Set<GameObjectLootTemplateRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await db.Set<ItemLootTemplateRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await db.Set<SkinningLootTemplateRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await db.Set<ReferenceLootTemplateRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await db.Set<CreatureLootInfoRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            }

            await InsertBatchedAsync(db, _templates.Values.Select(t => t.Row), cancellationToken).ConfigureAwait(false);
            await InsertBatchedAsync(db, _spawns.Values, cancellationToken).ConfigureAwait(false);
            await InsertBatchedAsync(db, _starters.Select(r => new GameObjectQuestStarterRow { Id = r.Item1, Quest = r.Item2 }), cancellationToken).ConfigureAwait(false);
            await InsertBatchedAsync(db, _enders.Select(r => new GameObjectQuestEnderRow { Id = r.Item1, Quest = r.Item2 }), cancellationToken).ConfigureAwait(false);
            await InsertBatchedAsync(db, _locks.Values, cancellationToken).ConfigureAwait(false);
            await InsertBatchedAsync<LootTemplateRowBase>(db, _loot.Values, cancellationToken).ConfigureAwait(false);
            await InsertBatchedAsync(db, _creatureLoot.Values.Select(c => c.Row), cancellationToken).ConfigureAwait(false);

            if (savepoint is not null)
            {
                await transaction.ReleaseSavepointAsync(savepoint, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception importError)
        {
            try
            {
                if (savepoint is not null)
                {
                    await transaction.RollbackToSavepointAsync(savepoint, CancellationToken.None).ConfigureAwait(false);
                    await transaction.ReleaseSavepointAsync(savepoint, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException("Game object/loot import and rollback failed; discard the context and transaction.", importError, rollbackError);
            }

            throw;
        }
        finally
        {
            db.ChangeTracker.Clear();
            db.ChangeTracker.AutoDetectChangesEnabled = detect;
        }

        return BuildReport();
    }

    public GameObjectLootImportReport BuildReport() => new(
        _templates.Count, _spawns.Count, _starters.Count, _enders.Count, _locks.Count, _loot.Count, _creatureLoot.Count, _skipped, [.. _warnings]);

    /// <summary>The loot rows that would be written (for inspection and tests).</summary>
    public IReadOnlyCollection<LootTemplateRowBase> LootRows => _loot.Values;

    /// <summary>The spawn rows that would be written.</summary>
    public IReadOnlyCollection<GameObjectSpawnRow> SpawnRows => _spawns.Values;

    private static async Task InsertBatchedAsync<T>(WorldDbContext db, IEnumerable<T> rows, CancellationToken ct)
        where T : class
    {
        const int BatchSize = 2000;
        int pending = 0;
        foreach (T row in rows)
        {
            db.Add((object)row);
            if (++pending == BatchSize)
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                db.ChangeTracker.Clear();
                pending = 0;
            }
        }

        if (pending > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            db.ChangeTracker.Clear();
        }
    }

    // --- rows -----------------------------------------------------------------------------

    private void ReadTemplate(DumpRow row)
    {
        int patch = row.Has("patch") ? (int)U32(row, "patch") : 0;
        if (patch > MaxPatch)
        {
            return;
        }

        uint entry = U32(row, "entry");
        if (_templates.TryGetValue(entry, out var existing) && existing.Patch > patch)
        {
            return;
        }

        var data = new uint[24];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = U32(row, "data" + i.ToString(CultureInfo.InvariantCulture));
        }

        var t = new GameObjectTemplateRow
        {
            Entry = entry,
            Type = U32(row, "type"),
            DisplayId = U32(row, "displayId", "display_id"),
            Name = Truncate(Str(row, "name"), 100),
            Faction = U32(row, "faction"),
            Flags = U32(row, "flags"),
            Size = F32(row, 1.0f, "size"),
        };
        t.SetData(data);
        _templates[entry] = (patch, t);
    }

    private void ReadSpawn(DumpRow row)
    {
        if (!InPatch(row))
        {
            _skipped++;
            return;
        }

        uint guid = U32(row, "guid");
        if (guid > 0x00FFFFFF)
        {
            _warnings.Add($"gameobject guid {guid} exceeds the 24-bit GUID counter; skipped");
            _skipped++;
            return;
        }

        _spawns[guid] = new GameObjectSpawnRow
        {
            Guid = guid,
            Entry = U32(row, "id"),
            MapId = U32(row, "map"),
            X = F32(row, 0f, "position_x"),
            Y = F32(row, 0f, "position_y"),
            Z = F32(row, 0f, "position_z"),
            Orientation = F32(row, 0f, "orientation"),
            Rotation0 = F32(row, 0f, "rotation0"),
            Rotation1 = F32(row, 0f, "rotation1"),
            Rotation2 = F32(row, 0f, "rotation2"),
            Rotation3 = F32(row, 0f, "rotation3"),
            SpawnTimeSeconds = I32(row, 300, "spawntimesecs", "spawntimesecsmin"),
            AnimProgress = U32Or(row, 100, "animprogress"),
            State = (byte)Math.Min(U32Or(row, 1, "state"), 2u),
        };
    }

    private void ReadRelation(DumpRow row, HashSet<(uint, uint)> into)
    {
        if (!InPatch(row))
        {
            _skipped++;
            return;
        }

        into.Add((U32(row, "id"), U32(row, "quest")));
    }

    private void ReadCreatureLoot(DumpRow row)
    {
        bool vmangos = row.Has("loot_id") || row.Has("level_min");
        int patch = vmangos && row.Has("patch") ? (int)U32(row, "patch") : 0;
        if (patch > MaxPatch)
        {
            return;
        }

        uint entry = U32(row, "Entry");
        if (_creatureLoot.TryGetValue(entry, out var existing) && existing.Patch > patch)
        {
            return;
        }

        var info = new CreatureLootInfoRow
        {
            Entry = entry,
            LootId = U32(row, "LootId", "loot_id"),
            SkinningLootId = U32(row, "SkinningLootId", "skinning_loot_id"),
            MinGold = U32(row, "MinLootGold", "gold_min"),
            MaxGold = U32(row, "MaxLootGold", "gold_max"),
        };

        if (info is { LootId: 0, SkinningLootId: 0, MinGold: 0, MaxGold: 0 })
        {
            _creatureLoot.Remove(entry);
            return;
        }

        _creatureLoot[entry] = (patch, info);
    }

    private void ReadLoot(DumpRow row, LootTemplateRowBase target)
    {
        if (!InPatch(row))
        {
            _skipped++;
            return;
        }

        target.Entry = U32(row, "entry");
        target.Item = U32(row, "item");
        target.ChanceOrQuestChance = F32(row, 100f, "ChanceOrQuestChance", "chance");
        target.GroupId = (byte)Math.Min(U32(row, "groupid"), byte.MaxValue);
        target.MinCountOrRef = I32(row, 1, "mincountOrRef", "mincount");
        target.MaxCount = U32Or(row, 1, "maxcount");
        target.ConditionId = U32(row, "condition_id", "conditionId");
        _loot[(target.GetType(), target.Entry, target.Item)] = target;
    }

    /// <summary>vmangos progressive rows: patch_min ≤ 10 ≤ patch_max (rows without the columns always apply).</summary>
    private static bool InPatch(DumpRow row)
    {
        if (!row.Has("patch_min"))
        {
            return true;
        }

        uint min = U32(row, "patch_min");
        uint max = row.Has("patch_max") ? U32Or(row, MaxPatch, "patch_max") : MaxPatch;
        return min <= MaxPatch && MaxPatch <= max;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static string? Get(DumpRow row, params ReadOnlySpan<string> names) => row.TryGet(out string? value, names) ? value : null;

    private static string Str(DumpRow row, params ReadOnlySpan<string> names) => Get(row, names) ?? string.Empty;

    private static uint U32(DumpRow row, params ReadOnlySpan<string> names) => U32Or(row, 0, names);

    private static uint U32Or(DumpRow row, uint fallback, params ReadOnlySpan<string> names)
    {
        string? raw = Get(row, names);
        if (string.IsNullOrEmpty(raw))
        {
            return fallback;
        }

        double value = double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
        return value <= 0 ? 0 : value >= uint.MaxValue ? uint.MaxValue : (uint)value;
    }

    private static int I32(DumpRow row, int fallback, params ReadOnlySpan<string> names)
    {
        string? raw = Get(row, names);
        if (string.IsNullOrEmpty(raw))
        {
            return fallback;
        }

        double value = double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
        return value <= int.MinValue ? int.MinValue : value >= int.MaxValue ? int.MaxValue : (int)value;
    }

    private static float F32(DumpRow row, float fallback, params ReadOnlySpan<string> names)
    {
        string? raw = Get(row, names);
        return string.IsNullOrEmpty(raw) ? fallback : float.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}

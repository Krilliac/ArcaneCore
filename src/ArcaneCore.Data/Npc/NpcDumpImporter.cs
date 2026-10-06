using System.Globalization;
using System.Reflection;
using System.Collections.Concurrent;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Kernel.Npc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ArcaneCore.Data.Npc;

public sealed record NpcImportReport(
    int NpcGossips, int GossipMenus, int GossipOptions, int NpcTexts, int Vendors, int Trainers,
    int Replaced, int Skipped, IReadOnlyList<string> Diagnostics);

/// <summary>
/// Maps the existing CMaNGOS NPC service tables into the already-owned EF entities. Reflection is
/// limited to matching model properties by normalized column name; it avoids a second hand-written
/// parser for the 80-column inline npc_text layout. Conditions are delegated to ConditionsDumpImporter.
/// </summary>
public sealed class NpcDumpImporter
{
    public static IReadOnlyCollection<string> TrackedTables => Required.Keys.ToArray();

    public static bool ReadsColumn(string table, string column)
    {
        return ModelTypes.TryGetValue(table, out Type? type)
            && PropertyCache.GetOrAdd(type, BuildPropertyMap).ContainsKey(Normalize(column));
    }

    private readonly Dictionary<uint, NpcGossip> _gossip = [];
    private readonly Dictionary<(uint, uint, uint), GossipMenu> _menus = [];
    private readonly Dictionary<(uint, uint), GossipMenuOption> _options = [];
    private readonly Dictionary<uint, NpcTextRow> _texts = [];
    private readonly Dictionary<(uint, uint), VendorItem> _vendors = [];
    private readonly Dictionary<(uint, uint), TrainerSpell> _trainers = [];
    private readonly List<string> _diagnostics = [];
    private readonly HashSet<(string Table, IReadOnlyList<string> Columns)> _validatedLayouts = [];
    private static readonly ConcurrentDictionary<Type, Dictionary<string, PropertyInfo>> PropertyCache = new();
    private int _replaced;
    private int _skipped;

    public void Read(TextReader dump)
    {
        foreach (object item in new MySqlDumpReader(dump).Read())
        {
            if (item is not DumpRow row)
                continue;
            string table = row.Table.ToLowerInvariant();
            // Conditions are owned by ConditionsDumpImporter and are intentionally not consumed here.

            if (!Required.TryGetValue(table, out string[]? required))
                continue;
            if (!_validatedLayouts.Contains((table, row.Columns)))
            {
                if (required.Any(c => !row.Has(c)))
                    throw new InvalidDataException($"{table} is missing one or more required columns: {string.Join(", ", required.Where(c => !row.Has(c)))}");
                _validatedLayouts.Add((table, row.Columns));
            }

            try
            {
                switch (table)
                {
                    case "npc_gossip": { NpcGossip x = ConvertModel<NpcGossip>(row); RequirePositive(x.NpcGuid, table, "npc_guid"); Put(_gossip, x, v => v.NpcGuid); break; }
                    case "gossip_menu": { GossipMenu x = ConvertModel<GossipMenu>(row); RequirePositive(x.Entry, table, "entry"); RequirePositive(x.TextId, table, "text_id"); Put(_menus, x, v => (v.Entry, v.TextId, v.ConditionId)); break; }
                    // CMaNGOS ObjectMgr::LoadGossipMenuItems treats menu_id 0 as the default menu.
                    case "gossip_menu_option":
                    {
                        ValidateGossipOptionExtensions(row);
                        GossipMenuOption x = ConvertModel<GossipMenuOption>(row);
                        Put(_options, x, v => (v.MenuId, v.Id));
                        break;
                    }
                    case "npc_text": { NpcTextRow x = ConvertModel<NpcTextRow>(row); RequirePositive(x.Id, table, "ID"); Put(_texts, x, v => v.Id); break; }
                    case "npc_vendor": { VendorItem x = ConvertModel<VendorItem>(row); RequirePositive(x.Entry, table, "entry"); RequirePositive(x.Item, table, "item"); Put(_vendors, x, v => (v.Entry, v.Item)); break; }
                    case "npc_trainer":
                    {
                        ValidateTrainerExtensions(row);
                        TrainerSpell x = ConvertModel<TrainerSpell>(row);
                        RequirePositive(x.Entry, table, "entry");
                        RequirePositive(x.Spell, table, "spell");
                        Put(_trainers, x, v => (v.Entry, v.Spell));
                        break;
                    }
                }
            }
            catch (Exception error) when (error is InvalidDataException or OverflowException or FormatException)
            {
                _skipped++;
                if (_diagnostics.Count < 32) _diagnostics.Add($"{table}: {error.Message}");
            }
        }
    }

    public NpcImportReport BuildReport() => new(_gossip.Count, _menus.Count, _options.Count, _texts.Count,
        _vendors.Count, _trainers.Count, _replaced, _skipped, [.. _diagnostics]);

    public async Task<NpcImportReport> WriteAsync(WorldDbContext db, bool replace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (db.ChangeTracker.Entries().Any()) throw new InvalidOperationException("NPC imports require an empty change tracker.");
        IDbContextTransaction? caller = db.Database.CurrentTransaction;
        if (caller is null && System.Transactions.Transaction.Current is not null) throw new InvalidOperationException("NPC imports require an explicit transaction.");
        if (caller is { SupportsSavepoints: false }) throw new InvalidOperationException("The caller's transaction does not support savepoints; the import cannot protect its existing work.");
        await using IDbContextTransaction? owned = caller is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        IDbContextTransaction tx = caller ?? owned!;
        string? savepoint = caller is not null ? "ArcaneNpcImport_" + Guid.NewGuid().ToString("N") : null;
        if (savepoint is not null) await tx.CreateSavepointAsync(savepoint, cancellationToken);
        bool detect = db.ChangeTracker.AutoDetectChangesEnabled;
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            if (replace)
            {
                await db.Set<NpcGossip>().ExecuteDeleteAsync(cancellationToken);
                await db.Set<GossipMenu>().ExecuteDeleteAsync(cancellationToken);
                await db.Set<GossipMenuOption>().ExecuteDeleteAsync(cancellationToken);
                await db.Set<NpcTextRow>().ExecuteDeleteAsync(cancellationToken);
                await db.Set<VendorItem>().ExecuteDeleteAsync(cancellationToken);
                await db.Set<TrainerSpell>().ExecuteDeleteAsync(cancellationToken);
            }
            const int batchSize = 2000;
            await InsertBatchedAsync(db, _gossip.Values, batchSize, cancellationToken);
            await InsertBatchedAsync(db, _menus.Values, batchSize, cancellationToken);
            await InsertBatchedAsync(db, _options.Values, batchSize, cancellationToken);
            await InsertBatchedAsync(db, _texts.Values, batchSize, cancellationToken);
            await InsertBatchedAsync(db, _vendors.Values, batchSize, cancellationToken);
            await InsertBatchedAsync(db, _trainers.Values, batchSize, cancellationToken);
            if (savepoint is not null) await tx.ReleaseSavepointAsync(savepoint, cancellationToken); else await tx.CommitAsync(cancellationToken);
        }
        catch (Exception importError)
        {
            try
            {
                if (savepoint is not null) { await tx.RollbackToSavepointAsync(savepoint, CancellationToken.None); await tx.ReleaseSavepointAsync(savepoint, CancellationToken.None); }
                else await tx.RollbackAsync(CancellationToken.None);
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException("NPC import and rollback failed; discard the context and transaction.", importError, rollbackError);
            }
            throw;
        }
        finally { db.ChangeTracker.Clear(); db.ChangeTracker.AutoDetectChangesEnabled = detect; }
        return BuildReport();
    }

    private static async Task InsertBatchedAsync<T>(WorldDbContext db, IEnumerable<T> rows, int batchSize, CancellationToken cancellationToken) where T : class
    {
        int pending = 0;
        foreach (T row in rows)
        {
            db.Add(row);
            if (++pending != batchSize) continue;
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
            pending = 0;
        }
        if (pending > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }
    }

    private void Put<TKey, TValue>(Dictionary<TKey, TValue> map, TValue value, Func<TValue, TKey> key) where TKey : notnull
    {
        if (map.ContainsKey(key(value))) _replaced++;
        map[key(value)] = value;
    }

    private static T ConvertModel<T>(DumpRow row) where T : new()
    {
        T result = new();
        Dictionary<string, PropertyInfo> properties = PropertyCache.GetOrAdd(typeof(T), BuildPropertyMap);
        for (int i = 0; i < row.Columns.Count && i < row.Values.Count; i++)
        {
            if (row.Values[i] is null || !properties.TryGetValue(Normalize(row.Columns[i]), out PropertyInfo? property)) continue;
            try { property.SetValue(result, Parse(row.Values[i]!, property.PropertyType)); }
            catch (Exception error) when (error is FormatException or OverflowException or InvalidDataException)
            { throw new InvalidDataException($"{row.Table}.{row.Columns[i]}: {error.Message}", error); }
        }
        return result;
    }

    private static object Parse(string raw, Type type)
    {
        if (type == typeof(string)) return raw;
        if (type == typeof(uint)) return uint.Parse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture);
        if (type == typeof(int)) return int.Parse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture);
        if (type == typeof(byte)) return byte.Parse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture);
        if (type == typeof(float))
        {
            float value = float.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (!float.IsFinite(value) || value < 0) throw new InvalidDataException("probability must be finite and nonnegative");
            return value;
        }
        throw new InvalidDataException($"unsupported property type {type.Name}");
    }

    private static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).ToArray());

    private static void ValidateTrainerExtensions(DumpRow row)
    {
        foreach (string column in new[] { "ReqAbility1", "ReqAbility2", "ReqAbility3", "condition_id" })
        {
            if (ReadUnsignedExtension(row, column) != 0)
                throw new InvalidDataException($"npc_trainer.{column} is unsupported when nonzero");
        }
    }

    private static void ValidateGossipOptionExtensions(DumpRow row)
    {
        if (ReadUnsignedExtension(row, "action_script_id") != 0)
            throw new InvalidDataException("gossip_menu_option.action_script_id is unsupported when nonzero");
        if (ReadUnsignedExtension(row, "box_money") != 0)
            throw new InvalidDataException("gossip_menu_option.box_money is unsupported when nonzero");

        int optionBroadcast = ReadSignedExtension(row, "option_broadcast_text");
        if (optionBroadcast != 0 && string.IsNullOrWhiteSpace(ReadTextExtension(row, "option_text")))
            throw new InvalidDataException("gossip_menu_option.option_broadcast_text requires inline option_text");
        int boxBroadcast = ReadSignedExtension(row, "box_broadcast_text");
        if (boxBroadcast != 0 && string.IsNullOrWhiteSpace(ReadTextExtension(row, "box_text")))
            throw new InvalidDataException("gossip_menu_option.box_broadcast_text requires inline box_text");
    }

    private static uint ReadUnsignedExtension(DumpRow row, string column)
    {
        string? raw = ReadExtension(row, column);
        if (raw is null or "") return 0;
        if (!uint.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out uint value))
            throw new InvalidDataException($"{row.Table}.{column} must be a nonnegative unsigned integer");
        return value;
    }

    private static int ReadSignedExtension(DumpRow row, string column)
    {
        string? raw = ReadExtension(row, column);
        if (raw is null or "") return 0;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < 0)
            throw new InvalidDataException($"{row.Table}.{column} must be a nonnegative signed integer");
        return value;
    }

    private static string? ReadTextExtension(DumpRow row, string column) => ReadExtension(row, column);

    private static string? ReadExtension(DumpRow row, string column)
    {
        int index = -1;
        for (int i = 0; i < row.Columns.Count; i++)
        {
            if (string.Equals(Normalize(row.Columns[i]), Normalize(column), StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }
        return index >= 0 && index < row.Values.Count ? row.Values[index] : null;
    }

    private static Dictionary<string, PropertyInfo> BuildPropertyMap(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite)
            .ToDictionary(p => Normalize(p.Name), StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, Type> ModelTypes = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
    {
        ["npc_gossip"] = typeof(NpcGossip), ["gossip_menu"] = typeof(GossipMenu),
        ["gossip_menu_option"] = typeof(GossipMenuOption), ["npc_text"] = typeof(NpcTextRow),
        ["npc_vendor"] = typeof(VendorItem), ["npc_trainer"] = typeof(TrainerSpell),
    };

    private static void RequirePositive(uint value, string table, string column)
    {
        if (value == 0) throw new InvalidDataException($"{table}.{column} must be greater than zero");
    }

    private static readonly IReadOnlyDictionary<string, string[]> Required = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["npc_gossip"] = ["npc_guid", "textid"],
        ["gossip_menu"] = ["entry", "text_id", "condition_id"],
        ["gossip_menu_option"] = ["menu_id", "id", "option_icon", "option_text", "option_id", "npc_option_npcflag", "action_menu_id", "action_poi_id", "box_coded", "box_text", "condition_id"],
        ["npc_text"] = ["ID", "text0_0", "text0_1", "lang0", "prob0", "em0_0", "em0_1", "em0_2", "em0_3", "em0_4", "em0_5"],
        ["npc_vendor"] = ["entry", "item", "maxcount", "incrtime", "slot", "condition_id"],
        ["npc_trainer"] = ["entry", "spell", "spellcost", "reqskill", "reqskillvalue", "reqlevel"],
    };

}

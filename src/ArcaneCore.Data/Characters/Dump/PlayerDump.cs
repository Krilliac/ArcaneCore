using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace ArcaneCore.Data.Characters.Dump;

/// <summary>What a characters table holds for a dump (vmangos <c>DumpTableType</c>, PlayerDump.h).</summary>
public enum DumpTableType
{
    /// <summary><c>characters</c>: the character id, account and name are replaced.</summary>
    Character,

    /// <summary>A per-character table keyed by <c>CharacterId</c>; only that column changes.</summary>
    CharTable,

    /// <summary><c>character_inventory</c>: owner, item and bag guids change (-> item guids).</summary>
    Inventory,

    /// <summary><c>character_pet</c>: owner and pet number change (-> pet numbers).</summary>
    Pet,

    /// <summary><c>character_pet_cooldown</c>: owner and pet number (&lt;- pet numbers).</summary>
    PetTable,

    /// <summary><c>mail</c>: id, receiver, text and item change (-> mail ids, item guids, text ids).</summary>
    Mail,

    /// <summary><c>item_instance</c>: guid, owner and text change (&lt;- item guids). Gift data are columns of it (vmangos character_gifts).</summary>
    Item,

    /// <summary><c>item_loot</c>, <c>item_loot_state</c>: item guid changes (&lt;- item guids).</summary>
    ItemLoot,

    /// <summary><c>item_text</c>: id changes (&lt;- text ids).</summary>
    ItemText,
}

/// <summary>vmangos <c>DumpReturn</c>, with one result this schema needs.</summary>
public enum DumpReturn
{
    Success,
    FileOpenError,
    TooManyChars,
    UnexpectedEnd,
    FileBroken,

    /// <summary>
    /// The name to load under is taken. vmangos then stores the duplicate with the rename-at-login flag (PlayerDump.cpp, DTT_CHARACTER);
    /// <c>characters.Name</c> is unique here, so the load is refused instead and the GM names the character.
    /// </summary>
    NameInUse,
}

/// <summary>Fresh realm-unique ids for a loaded dump (vmangos ObjectMgr m_ItemGuids, m_MailIds, m_ItemTextIds, GeneratePetNumber).</summary>
public interface IPlayerDumpIds
{
    uint NextItemGuid();

    uint NextMailId();

    uint NextItemTextId();

    uint NextPetNumber();
}

/// <summary>Ids above the largest stored ones, for a load while no world is running (tools, tests).</summary>
public sealed class StoredPlayerDumpIds : IPlayerDumpIds
{
    private uint _item, _mail, _text, _pet;

    public static async Task<StoredPlayerDumpIds> CreateAsync(CharacterDbContext db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return new StoredPlayerDumpIds
        {
            _item = await MaxAsync(db, "item_instance", "Guid", cancellationToken).ConfigureAwait(false),
            _mail = await MaxAsync(db, "mail", "Id", cancellationToken).ConfigureAwait(false),
            _text = await MaxAsync(db, "item_text", "Id", cancellationToken).ConfigureAwait(false),
            _pet = await MaxAsync(db, "character_pet", "PetNumber", cancellationToken).ConfigureAwait(false),
        };
    }

    public uint NextItemGuid() => ++_item;

    public uint NextMailId() => ++_mail;

    public uint NextItemTextId() => ++_text;

    public uint NextPetNumber() => ++_pet;

    private static async Task<uint> MaxAsync(CharacterDbContext db, string table, string column, CancellationToken ct)
    {
        uint max = 0;
        foreach (object row in await PlayerDumpTables.ReadAsync(db, table, null, ct).ConfigureAwait(false))
        {
            max = Math.Max(max, Convert.ToUInt32(PlayerDumpTables.Get(db, table, row, column)));
        }

        return max;
    }
}

/// <summary>The dumped tables (vmangos <c>dumpTables</c>, PlayerDump.cpp:42-66), in load order: <c>characters</c> first.</summary>
public static class PlayerDumpTables
{
    /// <summary>
    /// vmangos dumps the character, its per-character tables, inventory, pet, mail and their items, loot and texts; never social lists,
    /// guild or group membership, instance binds or battleground data. character_homebind is <c>characters</c> columns here, and
    /// character_tutorial is per account (account_tutorial), as in vmangos where it is not keyed by the character either.
    /// </summary>
    public static IReadOnlyList<(string Table, DumpTableType Type)> All { get; } =
    [
        ("characters", DumpTableType.Character),
        ("character_action", DumpTableType.CharTable),
        ("character_aura", DumpTableType.CharTable),
        ("character_explored_zones", DumpTableType.CharTable),
        ("character_forgotten_skills", DumpTableType.CharTable),
        ("character_honor", DumpTableType.CharTable),
        ("character_honor_cp", DumpTableType.CharTable),
        ("character_item_cooldown_owner", DumpTableType.CharTable),
        ("character_item_state", DumpTableType.CharTable),
        ("character_queststatus", DumpTableType.CharTable),
        ("character_reputation", DumpTableType.CharTable),
        ("character_reputation_watch", DumpTableType.CharTable),
        ("character_rest", DumpTableType.CharTable),
        ("character_skills", DumpTableType.CharTable),
        ("character_spell", DumpTableType.CharTable),
        ("character_spell_cooldown", DumpTableType.CharTable),
        ("character_spell_disabled", DumpTableType.CharTable),
        ("character_talent", DumpTableType.CharTable),
        ("character_taxi", DumpTableType.CharTable),
        ("character_vitals", DumpTableType.CharTable),
        ("character_inventory", DumpTableType.Inventory),
        ("character_pet", DumpTableType.Pet),
        ("character_pet_cooldown", DumpTableType.PetTable),
        ("mail", DumpTableType.Mail),
        ("item_instance", DumpTableType.Item),
        ("item_loot", DumpTableType.ItemLoot),
        ("item_loot_state", DumpTableType.ItemLoot),
        ("item_text", DumpTableType.ItemText),
    ];

    private static readonly Dictionary<string, DumpTableType> Types = All.ToDictionary(t => t.Table, t => t.Type, StringComparer.Ordinal);

    public static bool TryGetType(string table, out DumpTableType type) => Types.TryGetValue(table, out type);

    internal static IEntityType Entity(DbContext db, string table)
        => db.Model.GetEntityTypes().FirstOrDefault(e => e.GetTableName() == table && !e.IsOwned())
           ?? throw new InvalidOperationException($"the characters model has no table {table}");

    internal static object? Get(DbContext db, string table, object row, string property)
        => Entity(db, table).FindProperty(property)!.PropertyInfo!.GetValue(row);

    internal static void Set(DbContext db, string table, object row, string property, object value)
    {
        PropertyInfo info = Entity(db, table).FindProperty(property)!.PropertyInfo!;
        info.SetValue(row, Convert.ChangeType(value, info.PropertyType, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Rows of <paramref name="table"/>, all of them or those whose <paramref name="filter"/> column is in the set.</summary>
    internal static async Task<List<object>> ReadAsync(DbContext db, string table, (string Column, IReadOnlyCollection<long> Values)? filter, CancellationToken ct)
    {
        IEntityType entity = Entity(db, table);
        MethodInfo read = typeof(PlayerDumpTables).GetMethod(nameof(ReadTyped), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(entity.ClrType);
        return await ((Task<List<object>>)read.Invoke(null, [db, filter, ct])!).ConfigureAwait(false);
    }

    private static async Task<List<object>> ReadTyped<T>(DbContext db, (string Column, IReadOnlyCollection<long> Values)? filter, CancellationToken ct)
        where T : class
    {
        List<T> rows = await db.Set<T>().AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        if (filter is not { } f)
        {
            return [.. rows];
        }

        // Filtered in memory: the key columns are int or uint by table, and a dump is an admin's rare action.
        PropertyInfo info = typeof(T).GetProperty(f.Column)!;
        var wanted = f.Values.ToHashSet();
        return [.. rows.Where(r => wanted.Contains(Convert.ToInt64(info.GetValue(r), System.Globalization.CultureInfo.InvariantCulture)))];
    }
}

/// <summary>
/// vmangos <c>PlayerDumpWriter::GetDump</c>: every row the character owns in <see cref="PlayerDumpTables.All"/>, plus the items in its
/// inventory and mail, their loot and the texts they and the mail carry. One line per row: the table, a tab, the row as JSON (vmangos
/// writes INSERT statements; rows keyed by property name survive later columns, which load with their defaults).
/// </summary>
public sealed class PlayerDumpWriter(CharacterDbContext db)
{
    /// <summary>The first line (vmangos dumps begin with "IMPORTANT NOTE:" and the loader skips it).</summary>
    public const string Note = "IMPORTANT NOTE: this is not SQL to apply directly; load it with the '.pdump load' command.";

    private static readonly JsonSerializerOptions Json = new() { IncludeFields = false };

    /// <summary>The dump of character <paramref name="characterId"/>, or null when it does not exist.</summary>
    public async Task<string?> GetDumpAsync(int characterId, CancellationToken cancellationToken = default)
    {
        List<object> character = await PlayerDumpTables.ReadAsync(db, "characters", ("Id", [characterId]), cancellationToken).ConfigureAwait(false);
        if (character.Count == 0)
        {
            return null;
        }

        var dump = new StringBuilder(Note).Append('\n');
        void Append(string table, IEnumerable<object> rows)
        {
            foreach (object row in rows)
            {
                dump.Append(table).Append('\t').Append(JsonSerializer.Serialize(row, row.GetType(), Json)).Append('\n');
            }
        }

        Append("characters", character);
        long[] self = [characterId];
        var items = new SortedSet<long>();
        var texts = new SortedSet<long>();
        foreach ((string table, DumpTableType type) in PlayerDumpTables.All)
        {
            List<object> rows = type switch
            {
                DumpTableType.CharTable or DumpTableType.Pet or DumpTableType.PetTable
                    => await PlayerDumpTables.ReadAsync(db, table, ("CharacterId", self), cancellationToken).ConfigureAwait(false),
                DumpTableType.Inventory => await PlayerDumpTables.ReadAsync(db, table, ("Guid", self), cancellationToken).ConfigureAwait(false),
                DumpTableType.Mail => await PlayerDumpTables.ReadAsync(db, table, ("ReceiverId", self), cancellationToken).ConfigureAwait(false),
                DumpTableType.Item or DumpTableType.ItemLoot => items.Count == 0 ? []
                    : await PlayerDumpTables.ReadAsync(db, table, (type == DumpTableType.Item ? "Guid" : "ItemGuid", items), cancellationToken).ConfigureAwait(false),
                DumpTableType.ItemText => texts.Count == 0 ? []
                    : await PlayerDumpTables.ReadAsync(db, table, ("Id", texts), cancellationToken).ConfigureAwait(false),
                _ => [],
            };

            foreach (object row in rows)
            {
                switch (type)
                {
                    case DumpTableType.Inventory:
                        items.Add(Convert.ToInt64(PlayerDumpTables.Get(db, table, row, "ItemGuid")));
                        break;
                    case DumpTableType.Mail:
                        AddNonZero(items, PlayerDumpTables.Get(db, table, row, "ItemGuid"));
                        AddNonZero(texts, PlayerDumpTables.Get(db, table, row, "ItemTextId"));
                        break;
                    case DumpTableType.Item:
                        AddNonZero(texts, PlayerDumpTables.Get(db, table, row, "Text"));
                        break;
                }
            }

            Append(table, rows);
        }

        return dump.ToString();
    }

    private static void AddNonZero(SortedSet<long> set, object? value)
    {
        long v = Convert.ToInt64(value);
        if (v != 0)
        {
            set.Add(v);
        }
    }
}

/// <summary>The outcome of <see cref="PlayerDumpReader.LoadDumpAsync"/>: the result and, on success, the new character.</summary>
public readonly record struct PlayerDumpLoad(DumpReturn Result, int CharacterId = 0, string Name = "");

/// <summary>
/// vmangos <c>PlayerDumpReader::LoadDump</c> (PlayerDump.cpp:409-697): checks the account's character count, picks the character id and
/// name, gives every item, mail, text and pet a fresh id (old ids map consistently across tables, vmangos changeGuid) and stores the
/// rows in one transaction; any broken line rolls the whole load back.
/// </summary>
public sealed class PlayerDumpReader(CharacterDbContext db)
{
    /// <summary>vmangos LoadDump: <c>GetCharactersCount(account) &gt;= 10</c> refuses.</summary>
    public const int MaxCharactersPerAccount = 10;

    private static readonly JsonSerializerOptions Json = new();

    /// <summary>
    /// Load <paramref name="dump"/> into <paramref name="accountId"/>. <paramref name="name"/> is an already normalized and checked
    /// name, or empty for the dump's own; <paramref name="characterId"/> is the id to use when free, or 0 for a new one.
    /// </summary>
    public async Task<PlayerDumpLoad> LoadDumpAsync(string dump, int accountId, string name, int characterId, IPlayerDumpIds ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dump);
        ArgumentNullException.ThrowIfNull(ids);
        if (await db.Characters.CountAsync(c => c.AccountId == accountId, cancellationToken).ConfigureAwait(false) >= MaxCharactersPerAccount)
        {
            return new(DumpReturn.TooManyChars);
        }

        if (!TryParse(dump, out List<(string Table, DumpTableType Type, object Row)> rows))
        {
            return new(DumpReturn.FileBroken);
        }

        if (rows.Count == 0)
        {
            return new(DumpReturn.UnexpectedEnd);
        }

        // vmangos: a given guid already in use falls back to a new one.
        if (characterId != 0 && await db.Characters.AnyAsync(c => c.Id == characterId, cancellationToken).ConfigureAwait(false))
        {
            characterId = 0;
        }

        // vmangos: a name already taken falls back to the dump's own.
        if (name.Length != 0 && await db.Characters.AnyAsync(c => c.Name == name, cancellationToken).ConfigureAwait(false))
        {
            name = string.Empty;
        }

        var character = (Kernel.Characters.CharacterRecord)rows[0].Row;
        if (name.Length == 0)
        {
            name = character.Name;
            if (await db.Characters.AnyAsync(c => c.Name == name, cancellationToken).ConfigureAwait(false))
            {
                return new(DumpReturn.NameInUse);
            }
        }

        character.Id = characterId;
        character.AccountId = accountId;
        character.Name = name;

        var items = new Dictionary<long, uint>();
        var mails = new Dictionary<long, uint>();
        var texts = new Dictionary<long, uint>();
        var pets = new Dictionary<long, uint>();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        db.Characters.Add(character);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        int newId = character.Id;
        foreach ((string table, DumpTableType type, object row) in rows.Skip(1))
        {
            void Remap(string column, Dictionary<long, uint> map, Func<uint> next, bool allowZero)
            {
                long old = Convert.ToInt64(PlayerDumpTables.Get(db, table, row, column));
                if (old == 0 && allowZero)
                {
                    return;
                }

                if (!map.TryGetValue(old, out uint fresh))
                {
                    map[old] = fresh = next();
                }

                PlayerDumpTables.Set(db, table, row, column, fresh);
            }

            switch (type)
            {
                case DumpTableType.CharTable:
                    PlayerDumpTables.Set(db, table, row, "CharacterId", newId);
                    break;
                case DumpTableType.Inventory:
                    PlayerDumpTables.Set(db, table, row, "Guid", newId);
                    Remap("Bag", items, ids.NextItemGuid, allowZero: true);
                    Remap("ItemGuid", items, ids.NextItemGuid, allowZero: false);
                    break;
                case DumpTableType.Pet:
                    PlayerDumpTables.Set(db, table, row, "CharacterId", newId);
                    Remap("PetNumber", pets, ids.NextPetNumber, allowZero: false);
                    break;
                case DumpTableType.PetTable:
                    // vmangos DTT_PET_TABLE: a pet number the dump's pets did not define breaks the dump.
                    if (!pets.TryGetValue(Convert.ToInt64(PlayerDumpTables.Get(db, table, row, "PetNumber")), out uint pet))
                    {
                        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                        db.ChangeTracker.Clear();
                        return new(DumpReturn.FileBroken);
                    }

                    PlayerDumpTables.Set(db, table, row, "CharacterId", newId);
                    PlayerDumpTables.Set(db, table, row, "PetNumber", pet);
                    break;
                case DumpTableType.Mail:
                    Remap("Id", mails, ids.NextMailId, allowZero: false);
                    PlayerDumpTables.Set(db, table, row, "ReceiverId", newId);
                    // vmangos remaps a zero text id too, to a text that does not exist; zero (no body) stays zero here.
                    Remap("ItemTextId", texts, ids.NextItemTextId, allowZero: true);
                    Remap("ItemGuid", items, ids.NextItemGuid, allowZero: true);
                    break;
                case DumpTableType.Item:
                    Remap("Guid", items, ids.NextItemGuid, allowZero: false);
                    PlayerDumpTables.Set(db, table, row, "OwnerGuid", newId);
                    Remap("Text", texts, ids.NextItemTextId, allowZero: true);
                    break;
                case DumpTableType.ItemLoot:
                    Remap("ItemGuid", items, ids.NextItemGuid, allowZero: false);
                    break;
                case DumpTableType.ItemText:
                    Remap("Id", texts, ids.NextItemTextId, allowZero: false);
                    break;
            }

            // Store-generated keys (character_honor_cp.Id) are generated again.
            IEntityType entity = PlayerDumpTables.Entity(db, table);
            foreach (IProperty key in entity.FindPrimaryKey()!.Properties.Where(p => p.ValueGenerated == ValueGenerated.OnAdd && p.PropertyInfo is not null))
            {
                key.PropertyInfo!.SetValue(row, Activator.CreateInstance(key.ClrType));
            }

            db.Add(row);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Duplicate rows in a hand-edited dump: the whole load goes, as vmangos ROLLBACK(DUMP_FILE_BROKEN).
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            db.ChangeTracker.Clear();
            return new(DumpReturn.FileBroken);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
        return new(DumpReturn.Success, newId, name);
    }

    /// <summary>Lines of "table TAB json"; blank lines and the note skipped; <c>characters</c> must come first and only once.</summary>
    private bool TryParse(string dump, out List<(string Table, DumpTableType Type, object Row)> rows)
    {
        rows = [];
        foreach (string raw in dump.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("IMPORTANT NOTE:", StringComparison.Ordinal))
            {
                continue;
            }

            int tab = line.IndexOf('\t', StringComparison.Ordinal);
            if (tab <= 0 || !PlayerDumpTables.TryGetType(line[..tab], out DumpTableType type))
            {
                return false;
            }

            if ((type == DumpTableType.Character) != (rows.Count == 0))
            {
                return false;
            }

            string table = line[..tab];
            object? row;
            try
            {
                row = JsonSerializer.Deserialize(line[(tab + 1)..], PlayerDumpTables.Entity(db, table).ClrType, Json);
            }
            catch (JsonException)
            {
                return false;
            }

            if (row is null)
            {
                return false;
            }

            rows.Add((table, type, row));
        }

        return true;
    }
}

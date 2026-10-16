using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using Microsoft.Data.Sqlite;

namespace ArcaneCore.Benchmarks;

/// <summary>
/// The real data the benchmarks run on: an imported SQLite world.db (<c>ARCANECORE_TEST_WORLD_DB</c>, opened read-only and immutable) and the
/// build-5875 DBC directory (<c>ARCANECORE_TEST_DBC_DIR</c>). A benchmark that needs one throws when it is missing, never runs on stand-in data.
/// </summary>
internal static class BenchData
{
    public const string WorldVariable = "ARCANECORE_TEST_WORLD_DB";
    public const string DbcVariable = "ARCANECORE_TEST_DBC_DIR";

    private static readonly Lazy<LootContent> s_loot = new(LoadLoot);
    private static readonly Lazy<ItemTemplate[]> s_items = new(LoadItems);
    private static readonly Lazy<SpellStore> s_spells = new(LoadSpells);

    public static LootContent Loot => s_loot.Value;

    public static ItemTemplate[] Items => s_items.Value;

    public static SpellStore Spells => s_spells.Value;

    private static string Required(string variable)
        => Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value
            : throw new InvalidOperationException($"{variable} is not set; this benchmark runs on real data only.");

    private static SqliteConnection OpenWorld()
    {
        string uri = new Uri(Path.GetFullPath(Required(WorldVariable))).AbsoluteUri + "?mode=ro&immutable=1";
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = uri, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static readonly (string Table, LootTableKind Kind)[] LootTables =
    [
        ("creature_loot_template", LootTableKind.Creature), ("gameobject_loot_template", LootTableKind.GameObject),
        ("item_loot_template", LootTableKind.Item), ("skinning_loot_template", LootTableKind.Skinning),
        ("reference_loot_template", LootTableKind.Reference), ("fishing_loot_template", LootTableKind.Fishing),
        ("pickpocketing_loot_template", LootTableKind.Pickpocketing), ("disenchant_loot_template", LootTableKind.Disenchant),
    ];

    private static LootContent LoadLoot()
    {
        using SqliteConnection connection = OpenWorld();
        var rows = new List<(LootTableKind, LootStoreRow)>();
        foreach ((string table, LootTableKind kind) in LootTables)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT Entry, Item, ChanceOrQuestChance, GroupId, MinCountOrRef, MaxCount, ConditionId FROM {table}";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((kind, new LootStoreRow((uint)reader.GetInt64(0), (uint)reader.GetInt64(1), (float)reader.GetDouble(2),
                    (byte)reader.GetInt64(3), (int)reader.GetInt64(4), (uint)reader.GetInt64(5), (uint)reader.GetInt64(6))));
            }
        }

        return new LootContent(rows, []);
    }

    /// <summary>Every item_template row with the columns the consumable classification reads (entry, class, level, spells).</summary>
    private static ItemTemplate[] LoadItems()
    {
        using SqliteConnection connection = OpenWorld();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT entry, class, subclass, item_level, required_level, "
            + string.Join(", ", Enumerable.Range(1, 5).Select(i => $"spellid_{i}, spelltrigger_{i}")) + " FROM item_template ORDER BY entry";
        using SqliteDataReader reader = command.ExecuteReader();
        var items = new List<ItemTemplate>();
        while (reader.Read())
        {
            var spells = new ItemSpell[ItemTemplate.MaxSpells];
            for (int i = 0; i < spells.Length; i++)
            {
                spells[i] = new ItemSpell((uint)reader.GetInt64(5 + (i * 2)), (uint)reader.GetInt64(6 + (i * 2)), 0, 0, 0, 0, 0);
            }

            items.Add(new ItemTemplate
            {
                Entry = (uint)reader.GetInt64(0),
                Class = (uint)reader.GetInt64(1),
                SubClass = (uint)reader.GetInt64(2),
                ItemLevel = (uint)reader.GetInt64(3),
                RequiredLevel = (uint)reader.GetInt64(4),
                Spells = spells,
            });
        }

        return [.. items];
    }

    private static SpellStore LoadSpells()
    {
        SpellDbcContent rows = SpellDbcImporter.ReadDirectory(Required(DbcVariable));
        return SpellStoreFactory.Build(new SpellContent(rows.Spells, rows.CastTimes, rows.Durations, rows.Ranges, rows.Radii, [], []));
    }

    /// <summary>A player outside any map, for the benchmarks that only need its guid and state.</summary>
    public static Player Player(uint id)
    {
        var character = new CharacterRecord
        {
            Id = (int)id, AccountId = 1, Name = $"Bench{id}", Race = 1, Class = 1, Gender = 0, Level = 60, MapId = 0, ZoneId = 12,
        };
        var appearance = new PlayerAppearance(DisplayId: 49, FactionTemplate: 1, PowerType.Rage, BaseHealth: 60, BaseMana: 0,
            MaxHealth: 60, MaxPower: 1000, StartPower: 0, NextLevelXp: 400);
        return new Player(character, appearance, new NullSession((int)id));
    }

    private sealed class NullSession(int accountId) : IPlayerSession
    {
        public int AccountId { get; } = accountId;

        public AccountSecurity Security => AccountSecurity.Player;

        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload)
        {
        }

        public void ProcessWorldPackets(Player player)
        {
        }

        public void Kick()
        {
        }

        public void OnLoggedOut()
        {
        }
    }
}

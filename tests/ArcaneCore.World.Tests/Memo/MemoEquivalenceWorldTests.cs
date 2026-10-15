using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Memo;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Spells;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Memo;

/// <summary>Runs only when <c>ARCANECORE_TEST_WORLD_DB</c> names an imported SQLite world.db; its loot and item tables are read raw (any schema).</summary>
internal sealed class RealWorldDbFactAttribute : FactAttribute
{
    public const string Variable = "ARCANECORE_TEST_WORLD_DB";

    public RealWorldDbFactAttribute(bool needsDbc = false)
    {
        if (Environment.GetEnvironmentVariable(Variable) is not { Length: > 0 } path || !File.Exists(path) || new FileInfo(path).Length == 0)
        {
            Skip = $"{Variable} (an imported SQLite world.db) is required; the real-data equivalence check did NOT run.";
        }
        else if (needsDbc && (Environment.GetEnvironmentVariable(ClientData.RealClientDbcFactAttribute.Variable) is not { Length: > 0 } dbc
            || !File.Exists(Path.Combine(dbc, "Spell.dbc"))))
        {
            Skip = $"{ClientData.RealClientDbcFactAttribute.Variable} (the build-5875 DBC directory) is required; the real-data equivalence check did NOT run.";
        }
    }

    public static SqliteConnection Open()
    {
        string uri = new Uri(Path.GetFullPath(Environment.GetEnvironmentVariable(Variable)!)).AbsoluteUri + "?mode=ro&immutable=1";
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = uri, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
}

/// <summary>
/// The World-side memo tables give exactly what they replace (docs/integration/memo-tabulation-20261015.md): the spellbook snapshot against a
/// model book, the playerbot consumable classification against its computation, and the loot plans against the original generator on the real
/// world.db and the real 5875 Spell.dbc.
/// </summary>
public sealed class MemoEquivalenceWorldTests(ITestOutputHelper output)
{
    // ---- Spellbook: the per-character sorted snapshot ----

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

    private static Player NewPlayer(int id) => new(
        new CharacterRecord { Id = id, AccountId = 1, Name = $"P{id}", Race = 1, Class = 1, Level = 1 },
        new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400),
        new NullSession(id));

    [Fact]
    public void SpellbookSnapshots_AreTheSortedBook_AfterEveryKindOfChange_AndEarlierSnapshotsNeverChange()
    {
        var cache = new SpellbookCache(null, NullLogger.Instance);
        Player[] players = [NewPlayer(1), NewPlayer(2), NewPlayer(3)];
        var model = new Dictionary<int, HashSet<uint>>();
        var held = new List<(IReadOnlyList<uint> Snapshot, uint[] Contents)>();
        var random = new Random(5);
        int changes = 0;

        void Check()
        {
            foreach (Player player in players)
            {
                int id = SpellbookCache.CharacterId(player);
                uint[] expected = model.TryGetValue(id, out HashSet<uint>? book) ? [.. book.Order()] : [];
                IReadOnlyList<uint> actual = cache.GetSpells(player);
                Assert.Equal(expected, actual);
                Assert.Same(actual, cache.GetSpells(player)); // unchanged book: the same snapshot
                held.Add((actual, [.. actual]));
            }

            foreach ((IReadOnlyList<uint> snapshot, uint[] contents) in held)
            {
                Assert.Equal(contents, snapshot); // a snapshot handed out earlier is never altered by later changes
            }
        }

        Check();
        for (int step = 0; step < 600; step++)
        {
            Player player = players[random.Next(players.Length)];
            int id = SpellbookCache.CharacterId(player);
            uint spell = (uint)random.Next(1, 40);
            switch (random.Next(8))
            {
                case 0 or 1 or 2:
                    Assert.Equal(model.GetValueOrDefault(id)?.Contains(spell) != true, cache.LearnSpell(player, spell));
                    (model.TryGetValue(id, out HashSet<uint>? learned) ? learned : model[id] = []).Add(spell);
                    break;
                case 3:
                    Assert.Equal(model.GetValueOrDefault(id)?.Remove(spell) == true, cache.ForgetSpell(player, spell));
                    break;
                case 4:
                    uint[] adopted = [spell, spell + 50];
                    Assert.Equal(model.TryGetValue(id, out HashSet<uint>? cached), cache.AdoptCommitted(id, adopted));
                    cached?.UnionWith(adopted);
                    break;
                case 5:
                    uint[] defaults = [spell, 99];
                    Assert.Equal(!model.ContainsKey(id), cache.EnsureDefaults(player, defaults));
                    if (!model.ContainsKey(id))
                    {
                        model[id] = [.. defaults];
                    }

                    break;
                case 6:
                    uint[] loaded = [.. Enumerable.Range(0, random.Next(0, 6)).Select(_ => (uint)random.Next(1, 40))];
                    cache.LoadCharacter(id, loaded);
                    model[id] = [.. loaded];
                    break;
                default:
                    cache.DeleteCharacter(id);
                    model.Remove(id);
                    break;
            }

            changes++;
            Check();
        }

        cache.Load([new Data.Characters.Spells.CharacterSpellRow { CharacterId = 1, Spell = 7 }, new Data.Characters.Spells.CharacterSpellRow { CharacterId = 1, Spell = 3 }]);
        model.Clear();
        model[1] = [3, 7];
        Check();
        Assert.Equal(600, changes);
        Assert.Throws<NotSupportedException>(() => ((IList<uint>)cache.GetSpells(players[0]))[0] = 1); // read-only, as the copies were
    }

    // ---- Playerbot consumables: the classification table ----

    private static SpellInfo Spell(uint id, uint family, SpellAuraInterruptFlags interrupt, params SpellEffectInfo[] effects)
        => new() { Id = id, Name = $"s{id}", SpellFamilyName = family, AuraInterruptFlags = interrupt, Effects = [.. effects, .. Enumerable.Repeat(new SpellEffectInfo(), Math.Max(0, 3 - effects.Length))] };

    private static SpellEffectInfo Effect(SpellEffectName effect, AuraType aura = AuraType.None, int misc = 0, int basePoints = 0)
        => new() { Effect = effect, AuraType = aura, MiscValue = misc, BasePoints = basePoints };

    /// <summary>Every spell shape the classification distinguishes: food, drink, both, neither, cooked food, wrong family, no standing cancel.</summary>
    private static List<SpellInfo> SpellShapes()
    {
        var spells = new List<SpellInfo>();
        uint id = 1000;
        SpellEffectInfo[] effects =
        [
            Effect(SpellEffectName.ApplyAura, AuraType.ModRegen), Effect(SpellEffectName.ApplyAura, AuraType.ModPowerRegen, (int)PowerType.Mana),
            Effect(SpellEffectName.ApplyAura, AuraType.ModPowerRegen, (int)PowerType.Rage), Effect(SpellEffectName.ApplyAura, AuraType.ModRegen, basePoints: -5),
            Effect(SpellEffectName.TriggerSpell), Effect(SpellEffectName.Heal), Effect(SpellEffectName.None),
        ];
        foreach (uint family in new uint[] { 0, 3 })
        {
            foreach (SpellAuraInterruptFlags interrupt in new[] { SpellAuraInterruptFlags.StandingCancels, SpellAuraInterruptFlags.None })
            {
                foreach (SpellEffectInfo first in effects)
                {
                    foreach (SpellEffectInfo second in effects)
                    {
                        spells.Add(Spell(id++, family, interrupt, first, second));
                    }
                }
            }
        }

        return spells;
    }

    [Fact]
    public void ConsumableClassification_EqualsTheComputation_ForEverySpellAndSlotShape_AndFollowsTheStoreAndTemplate()
    {
        List<SpellInfo> shapes = SpellShapes();
        var store = new SpellStore(shapes, [], []);
        var templates = new List<ItemTemplate>();
        uint entry = 1;
        foreach (SpellInfo spell in shapes)
        {
            templates.Add(new ItemTemplate { Entry = entry++, Spells = [new ItemSpell(spell.Id, 0, -1, 0, 0, 0, 0)] });
            templates.Add(new ItemTemplate { Entry = entry++, Spells = [new ItemSpell(spell.Id, 1, -1, 0, 0, 0, 0)] });
            templates.Add(new ItemTemplate { Entry = entry++, Spells = [new ItemSpell(0, 0, 0, 0, 0, 0, 0), new ItemSpell(spell.Id, 0, -1, 0, 0, 0, 0)] });
            templates.Add(new ItemTemplate { Entry = entry++, Spells = [new ItemSpell(spell.Id, 0, -1, 0, 0, 0, 0), new ItemSpell(spell.Id, 0, -1, 0, 0, 0, 0)] });
        }

        templates.Add(new ItemTemplate { Entry = entry++, Spells = [new ItemSpell(99_999, 0, -1, 0, 0, 0, 0)] }); // a spell the store lacks
        int food = 0;
        int drink = 0;
        foreach (ItemTemplate template in templates)
        {
            for (int read = 0; read < 2; read++) // the first read fills the table, the second is served from it
            {
                bool expected = PlayerbotConsumables.ComputeClassify(template, store, out PlayerbotConsumableKind expectedKind);
                bool actual = PlayerbotConsumables.TryClassify(template, store, out PlayerbotConsumableKind actualKind);
                Assert.Equal((expected, expectedKind), (actual, actualKind));
                food += read == 0 && actual && actualKind == PlayerbotConsumableKind.Food ? 1 : 0;
                drink += read == 0 && actual && actualKind == PlayerbotConsumableKind.Drink ? 1 : 0;
            }
        }

        Assert.True(food > 0 && drink > 0, $"food {food}, drink {drink}: the shapes do not reach both answers");

        // A reloaded store (a new instance) where the same spell id is no longer food: the template is classified again.
        ItemTemplate bread = templates.First(t => PlayerbotConsumables.TryClassify(t, store, out PlayerbotConsumableKind k) && k == PlayerbotConsumableKind.Food);
        uint breadSpell = bread.Spells[0].SpellId;
        var reloaded = new SpellStore(shapes.Select(s => s.Id == breadSpell ? s with { AuraInterruptFlags = SpellAuraInterruptFlags.None } : s), [], []);
        Assert.False(PlayerbotConsumables.TryClassify(bread, reloaded, out _));
        Assert.True(PlayerbotConsumables.TryClassify(bread, store, out _));

        // A reloaded template (a new instance, equal by value) is classified on its own, by reference.
        ItemTemplate copy = bread with { };
        Assert.Equal(PlayerbotConsumables.ComputeClassify(copy, reloaded, out _), PlayerbotConsumables.TryClassify(copy, reloaded, out _));
    }

    [RealWorldDbFact(needsDbc: true)]
    public void ConsumableClassification_EqualsTheComputation_ForEveryRealItem_AgainstTheReal5875SpellDbc()
    {
        SpellDbcContent rows = SpellDbcImporter.ReadDirectory(ClientData.RealClientDbcFactAttribute.DbcDirectory);
        SpellStore store = SpellStoreFactory.Build(new SpellContent(rows.Spells, rows.CastTimes, rows.Durations, rows.Ranges, rows.Radii, [], []));
        using SqliteConnection connection = RealWorldDbFactAttribute.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT entry, class, " + string.Join(", ", Enumerable.Range(1, 5).Select(i => $"spellid_{i}, spelltrigger_{i}")) + " FROM item_template";
        using SqliteDataReader reader = command.ExecuteReader();
        int items = 0;
        var kinds = new Dictionary<(bool, PlayerbotConsumableKind), int>();
        while (reader.Read())
        {
            var template = new ItemTemplate
            {
                Entry = (uint)reader.GetInt64(0), Class = (uint)reader.GetInt64(1),
                Spells = [.. Enumerable.Range(0, 5).Select(i => new ItemSpell((uint)reader.GetInt64(2 + (i * 2)), (uint)reader.GetInt64(3 + (i * 2)), 0, 0, 0, 0, 0))],
            };
            bool expected = PlayerbotConsumables.ComputeClassify(template, store, out PlayerbotConsumableKind expectedKind);
            for (int read = 0; read < 2; read++)
            {
                bool actual = PlayerbotConsumables.TryClassify(template, store, out PlayerbotConsumableKind actualKind);
                Assert.True((expected, expectedKind) == (actual, actualKind), $"item {template.Entry}: classification changed");
            }

            kinds[(expected, expectedKind)] = kinds.GetValueOrDefault((expected, expectedKind)) + 1;
            items++;
        }

        output.WriteLine($"{items} items, {store.Count} spells: " + string.Join(", ", kinds.Select(p => $"{p.Key}={p.Value}")));
        Assert.True(items > 10_000, $"only {items} item rows were read");
        Assert.True(kinds.GetValueOrDefault((true, PlayerbotConsumableKind.Food)) > 50 && kinds.GetValueOrDefault((true, PlayerbotConsumableKind.Drink)) > 10,
            "the real items should include food and drink");
    }

    // ---- Loot: per-template plans on the real world.db ----

    private static readonly (string Table, LootTableKind Kind)[] LootTables =
    [
        ("creature_loot_template", LootTableKind.Creature), ("gameobject_loot_template", LootTableKind.GameObject),
        ("item_loot_template", LootTableKind.Item), ("skinning_loot_template", LootTableKind.Skinning),
        ("reference_loot_template", LootTableKind.Reference), ("fishing_loot_template", LootTableKind.Fishing),
        ("pickpocketing_loot_template", LootTableKind.Pickpocketing), ("disenchant_loot_template", LootTableKind.Disenchant),
    ];

    [RealWorldDbFact]
    public void LootPlans_RollExactlyAsTheOriginalGenerator_ForEveryTemplateOfTheRealWorldDb()
    {
        var rows = new List<(LootTableKind, LootStoreRow)>();
        using (SqliteConnection connection = RealWorldDbFactAttribute.Open())
        {
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
        }

        var content = new LootContent(rows, []);
        (LootTableKind Kind, uint Entry)[] templates = [.. rows.Select(r => (r.Item1, r.Item2.Entry)).Distinct().OrderBy(t => t.Item1).ThenBy(t => t.Entry)];
        foreach (bool rated in new[] { false, true })
        {
            Func<LootStoreRow, float>? rate = rated ? row => row.MinCountOrRef < 0 ? 1.5f : 2f : null;
            var oracle = new ReferenceLootGenerator(content, new Random(2026)) { ChanceRate = rate };
            var generator = new LootGenerator(content, new Random(2026)) { ChanceRate = rate };
            long items = 0;
            for (int round = 0; round < 5; round++)
            {
                foreach ((LootTableKind kind, uint entry) in templates)
                {
                    List<RolledLoot> expected = oracle.Roll(kind, entry);
                    Assert.True(ReferenceLootGenerator.Same(expected, generator.Roll(kind, entry)), $"{kind} {entry} round {round} rated={rated}: rolls differ");
                    items += expected.Count;
                }
            }

            output.WriteLine($"rated={rated}: {templates.Length} templates x 5 rounds from {rows.Count} rows, {items} items rolled identically");
            Assert.True(items > 20_000, $"only {items} items were rolled");
        }

        Assert.True(templates.Length > 7000, $"only {templates.Length} templates were read");
    }
}

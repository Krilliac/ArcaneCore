using System.Globalization;
using System.Text;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Items;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.Crafting;
using ArcaneCore.Data.Items;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Gm.Character.Boost;
using ArcaneCore.World.Playerbots.Progression;
using ArcaneCore.World.Spells;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Gm.Character;

/// <summary>
/// Runs only when <c>ARCANECORE_TEST_WORLD_DB</c> names an imported SQLite world.db (opened read-only), <c>ARCANECORE_TEST_DBC_DIR</c> the
/// build-5875 DBC directory (ItemRandomProperties.dbc, SpellItemEnchantment.dbc and the Spell DBC set) and <c>ARCANECORE_CLASSICDB_DUMP</c> the classic-db dump
/// (item_enchantment_template): the random suffixes a live realm has. Reports Skipped naming the variables, never a silent pass.
/// </summary>
internal sealed class BoostRealContentFactAttribute : FactAttribute
{
    public const string Variable = "ARCANECORE_TEST_WORLD_DB";
    public const string DbcVariable = "ARCANECORE_TEST_DBC_DIR";
    public const string DumpVariable = "ARCANECORE_CLASSICDB_DUMP";

    public BoostRealContentFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) is not { Length: > 0 } path || !File.Exists(path)
            || Environment.GetEnvironmentVariable(DbcVariable) is not { Length: > 0 } dbc || !File.Exists(Path.Combine(dbc, "ItemRandomProperties.dbc"))
            || !File.Exists(Path.Combine(dbc, "SpellItemEnchantment.dbc"))
            || !File.Exists(Path.Combine(dbc, "Spell.dbc")) || !File.Exists(Path.Combine(dbc, "SpellCastTimes.dbc"))
            || !File.Exists(Path.Combine(dbc, "SpellDuration.dbc")) || !File.Exists(Path.Combine(dbc, "SpellRange.dbc"))
            || !File.Exists(Path.Combine(dbc, "SpellRadius.dbc"))
            || Environment.GetEnvironmentVariable(DumpVariable) is not { Length: > 0 } dump || !File.Exists(dump))
        {
            Skip = $"{Variable} (an imported SQLite world.db), {DbcVariable} (build-5875 DBCs) and {DumpVariable} (the classic-db dump) are required; the real-content boost kit checks did NOT run.";
        }
        Skip ??= RealContent.WorldFixtureSchema.SkipReasonForEnvironment();
    }
}

/// <summary>
/// The <c>.character boost</c> gear kit against the imported ClassicDB world (<c>ARCANECORE_TEST_WORLD_DB</c>, read-only): the pool the
/// planner sees at each filter stage, and the kit it chooses per class. <c>ARCANECORE_BOOST_KIT_DUMP</c> (optional) names a file the kits are
/// written to as markdown for the analysis.
/// </summary>
public sealed class CharacterBoostRealContentTests(ITestOutputHelper output)
{
    /// <summary>Loaded once per test run; the world.db is only read.</summary>
    private static readonly Lazy<RealBoostContent> s_content = new(() => RealBoostContent.Load(
        Environment.GetEnvironmentVariable(BoostRealContentFactAttribute.Variable)!,
        Environment.GetEnvironmentVariable(BoostRealContentFactAttribute.DbcVariable)!,
        Environment.GetEnvironmentVariable(BoostRealContentFactAttribute.DumpVariable)!));

    private static (Dictionary<ItemStatType, int> Stats, BoostGearPlan Plan) Kit(Class cls, byte level)
    {
        BoostGearPlan plan = s_content.Value.Plan(cls, level);
        return (s_content.Value.Totals(plan), plan);
    }

    [BoostRealContentFact]
    public void Sources_ExcludeAnEventOnlyVendorItem()
    {
        // ClassicDB Tree Frog Box (11026) is sold only by an event-spawned vendor; it has no permanent vendor,
        // quest reward or loot source. The real-content probe must use the production event-source rule.
        Assert.DoesNotContain(11026u, s_content.Value.Sources);
    }

    [BoostRealContentFact]
    public void PoolStageCount_MatchesThePlannerFilter()
    {
        const byte level = 20;
        RealBoostContent content = s_content.Value;
        string line = content.PoolStages(level).Split('\n').Single(s => s.StartsWith("- final IsPoolItem: ", StringComparison.Ordinal));
        int reported = int.Parse(line["- final IsPoolItem: ".Length..].Split(' ')[0], CultureInfo.InvariantCulture);
        int actual = content.Items.Count(item => BoostGearPlanner.IsPoolItem(item, level, content.Sources));
        Assert.Equal(actual, reported);
    }

    /// <summary>
    /// A level-60 warrior (the shield-tank weights a non-bot warrior gets) wears a kit a fresh 60 out of the level-55-60 dungeons would have:
    /// strength and stamina it can tank with, a real weapon and a shield. The thresholds sit between the kit before this change (green, fixed
    /// stats only: strength 109, stamina 173, main hand 35.8 DPS; docs/areas/character-boost.md) and what blues and suffixed greens give.
    /// </summary>
    [BoostRealContentFact]
    public void Warrior60_GetsStrengthStaminaAndARealWeapon()
    {
        (Dictionary<ItemStatType, int> stats, BoostGearPlan plan) = Kit(Class.Warrior, 60);
        output.WriteLine(s_content.Value.Describe(Class.Warrior, 60));

        Assert.True(stats.GetValueOrDefault(ItemStatType.Strength) + stats.GetValueOrDefault(ItemStatType.Stamina) >= 350,
            $"warrior 60 strength + stamina {stats.GetValueOrDefault(ItemStatType.Strength) + stats.GetValueOrDefault(ItemStatType.Stamina)}");
        Assert.True(stats.GetValueOrDefault(ItemStatType.Strength) >= 120, $"warrior 60 strength {stats.GetValueOrDefault(ItemStatType.Strength)}");
        ItemTemplate main = plan.Picks.Single(p => p.Key == InventorySlots.MainHand).Value;
        Assert.Equal((uint)ItemClass.Weapon, main.Class);
        Assert.True(PlayerbotItemScore.WeaponDps(main) >= 38f, $"warrior 60 main hand {main.Name} {PlayerbotItemScore.WeaponDps(main)} DPS");
        Assert.Equal(InventoryType.Shield, plan.Picks.Single(p => p.Key == InventorySlots.OffHand).Value.GetInventoryType());
        Assert.Empty(plan.EmptySlots);
        Assert.All(plan.Picks, p => Assert.True(p.Value.Quality >= 2, $"{BoostGearPlanner.SlotName(p.Key)} {p.Value.Name} is quality {p.Value.Quality}"));
        Assert.All(plan.Picks, p => Assert.True(BoostGearPlanner.GivesAnything(p.Value, plan.Suffixes.GetValueOrDefault(p.Key)), p.Value.Name));
        // ClassicDB Rainbow Girdle and Gauntlets of Valor are genuine plate with useful Strength/Stamina plus secondary
        // Intellect/Spirit. A blanket zero-caster-stat assertion incorrectly rejects them; primary stats must dominate.
        Assert.All(plan.Picks.Where(p => p.Value.Class == (uint)ItemClass.Armor && p.Value.SubClass == ArmorPlate), p =>
        {
            BoostSuffix? suffix = plan.Suffixes.GetValueOrDefault(p.Key);
            int primary = Stat(p.Value, suffix, ItemStatType.Strength) + Stat(p.Value, suffix, ItemStatType.Stamina);
            int secondary = Stat(p.Value, suffix, ItemStatType.Intellect) + Stat(p.Value, suffix, ItemStatType.Spirit);
            Assert.True(primary > 0 && primary >= secondary, $"{p.Value.Name}: primary {primary}, caster stats {secondary}");
        });
    }

    /// <summary>A level-60 mage: intellect and stamina, a caster weapon and a wand, and no strength or agility gear.</summary>
    [BoostRealContentFact]
    public void Mage60_GetsIntellectAndStamina_AndNoMeleeStats()
    {
        (Dictionary<ItemStatType, int> stats, BoostGearPlan plan) = Kit(Class.Mage, 60);
        output.WriteLine(s_content.Value.Describe(Class.Mage, 60));

        Assert.True(stats.GetValueOrDefault(ItemStatType.Intellect) >= 200, $"mage 60 intellect {stats.GetValueOrDefault(ItemStatType.Intellect)}");
        Assert.True(stats.GetValueOrDefault(ItemStatType.Stamina) >= 90, $"mage 60 stamina {stats.GetValueOrDefault(ItemStatType.Stamina)}");
        Assert.Equal(0, stats.GetValueOrDefault(ItemStatType.Strength) + stats.GetValueOrDefault(ItemStatType.Agility));
        Assert.All(plan.Picks.Where(p => p.Value.Class == (uint)ItemClass.Armor),
            p => Assert.True(p.Value.SubClass is ItemSubClasses.ArmorMisc or ArmorCloth, $"{p.Value.Name} is armor sub-class {p.Value.SubClass}"));
        Assert.Equal(19u, plan.Picks.Single(p => p.Key == InventorySlots.Ranged).Value.SubClass);   // a wand
        Assert.Empty(plan.EmptySlots);
    }

    /// <summary>A level-60 hunter: agility, and a bow, gun or crossbow at the DPS of the level's dungeons.</summary>
    [BoostRealContentFact]
    public void Hunter60_GetsAgilityAndARealRangedWeapon()
    {
        (Dictionary<ItemStatType, int> stats, BoostGearPlan plan) = Kit(Class.Hunter, 60);
        output.WriteLine(s_content.Value.Describe(Class.Hunter, 60));

        Assert.True(stats.GetValueOrDefault(ItemStatType.Agility) >= 200, $"hunter 60 agility {stats.GetValueOrDefault(ItemStatType.Agility)}");
        ItemTemplate ranged = plan.Picks.Single(p => p.Key == InventorySlots.Ranged).Value;
        Assert.True(ranged.SubClass is 2 or 3 or 18, $"{ranged.Name} is weapon sub-class {ranged.SubClass}");
        Assert.True(PlayerbotItemScore.WeaponDps(ranged) >= 32f, $"hunter 60 ranged {ranged.Name} {PlayerbotItemScore.WeaponDps(ranged)} DPS");
        Assert.Empty(plan.EmptySlots);
    }

    /// <summary>Every class at six leveling milestones: no slot holds a piece that gives nothing, and no grey item.</summary>
    [BoostRealContentFact]
    public void EveryClassAndLevel_NoStatlessOrGreyPieces()
    {
        foreach (byte level in new byte[] { 10, 20, 30, 40, 50, 60 })
        {
            foreach (Class cls in RealBoostContent.Classes)
            {
                BoostGearPlan plan = s_content.Value.Plan(cls, level);
                Assert.All(plan.Picks, p => Assert.True(p.Value.Quality >= 1 && BoostGearPlanner.GivesAnything(p.Value, plan.Suffixes.GetValueOrDefault(p.Key)),
                    $"{cls} {level} {BoostGearPlanner.SlotName(p.Key)}: {p.Value.Name} ({p.Value.Entry})"));
            }
        }
    }

    [BoostRealContentFact]
    public void EveryClassAtEveryLevel10Through60_WearsNearLevelLegalArmor()
    {
        foreach (byte level in Enumerable.Range(10, 51).Select(n => (byte)n))
        {
            foreach (Class cls in RealBoostContent.Classes)
            {
                uint armor = cls switch
                {
                    Class.Warrior or Class.Paladin => level >= 40 ? 4u : 3u,
                    Class.Hunter or Class.Shaman => level >= 40 ? 3u : 2u,
                    Class.Rogue or Class.Druid => 2u,
                    _ => 1u,
                };
                BoostGearPlan plan = s_content.Value.Plan(cls, level);
                Assert.All(plan.Picks, pick =>
                {
                    Assert.InRange(pick.Value.Quality, 1u, BoostGearPlanner.DefaultMaxQuality);
                    Assert.True(pick.Value.RequiredLevel <= level && pick.Value.ItemLevel <= (uint)level + BoostGearPlanner.ItemLevelHeadroom,
                        $"{cls} {level} {BoostGearPlanner.SlotName(pick.Key)}: {pick.Value.Name} requires {pick.Value.RequiredLevel}, item level {pick.Value.ItemLevel}");
                    if (pick.Key is InventorySlots.Head or InventorySlots.Shoulders or InventorySlots.Chest or InventorySlots.Waist
                        or InventorySlots.Legs or InventorySlots.Feet or InventorySlots.Wrists or InventorySlots.Hands)
                    {
                        // A level between the table milestones can have no eligible piece of the highest trained type in
                        // one slot (e.g. Rugged Spaulders before a mail shoulder becomes available), so lower armor is valid.
                        Assert.InRange(pick.Value.SubClass, 1u, armor);
                    }
                });
            }
        }
    }

    private const uint ArmorCloth = 1;   // vmangos ItemPrototype.h ITEM_SUBCLASS_ARMOR_CLOTH
    private const uint ArmorPlate = 4;   // ITEM_SUBCLASS_ARMOR_PLATE

    private static int Stat(ItemTemplate t, BoostSuffix? suffix, ItemStatType type)
        => t.Stats.Where(s => s.Type == (uint)type).Sum(s => s.Value) + (suffix?.Stats.Where(s => s.Type == (uint)type).Sum(s => s.Value) ?? 0);

    [BoostRealContentFact]
    public void DumpKits_ForEveryClass_At10_20_30_40_50_60()
    {
        RealBoostContent content = s_content.Value;
        var text = new StringBuilder();
        foreach (byte level in new byte[] { 10, 20, 30, 40, 50, 60 })
        {
            text.AppendLine(content.PoolStages(level));
        }

        foreach (byte level in new byte[] { 10, 20, 30, 40, 50, 60 })
        {
            foreach (Class cls in RealBoostContent.Classes)
            {
                text.AppendLine(content.Describe(cls, level));
            }
        }

        output.WriteLine(text.ToString());
        if (Environment.GetEnvironmentVariable("ARCANECORE_BOOST_KIT_DUMP") is { Length: > 0 } dump)
        {
            File.WriteAllText(dump, text.ToString());
        }
    }
}

/// <summary>Templates and the obtainable set of a real world.db, and a test wear rule per class.</summary>
internal sealed class RealBoostContent
{
    public static readonly Class[] Classes =
        [Class.Warrior, Class.Paladin, Class.Hunter, Class.Rogue, Class.Priest, Class.Shaman, Class.Mage, Class.Warlock, Class.Druid];

    private RealBoostContent(IReadOnlyList<ItemTemplate> items, HashSet<uint> sources, HashSet<uint> questOnly, BoostSuffixes suffixes)
    {
        Items = items;
        Sources = sources;
        QuestOnly = questOnly;
        Suffixes = suffixes;
    }

    public BoostSuffixes Suffixes { get; }

    public IReadOnlyList<ItemTemplate> Items { get; }

    public HashSet<uint> Sources { get; }

    public HashSet<uint> QuestOnly { get; }

    public static RealBoostContent Load(string path, string dbcDirectory, string dump)
    {
        var properties = new ItemRandomPropertyCatalog(
            ItemRandomPropertiesDbcReader.Load(Path.Combine(dbcDirectory, "ItemRandomProperties.dbc")), ItemEnchantmentTemplateDumpReader.Load(dump));
        SpellDbcContent spellRows = SpellDbcImporter.ReadDirectory(dbcDirectory);
        var spellStore = SpellStoreFactory.Build(new SpellContent(spellRows.Spells, spellRows.CastTimes, spellRows.Durations,
            spellRows.Ranges, spellRows.Radii, [], []));
        BoostSuffixes suffixes = BoostSuffixes.TryCreate(properties, EnchantDbcReader.Load(Path.Combine(dbcDirectory, "SpellItemEnchantment.dbc")), spellStore.Get)
            ?? throw new InvalidOperationException("the random property data loaded empty");
        // SQLite otherwise tries to open a sidecar beside the live DB, which a read-only profile correctly denies. This test never
        // writes to the DB and deliberately observes the on-disk snapshot, so immutable=1 is appropriate for this acceptance probe.
        string immutableUri = new Uri(Path.GetFullPath(path)).AbsoluteUri + "?mode=ro&immutable=1";
        string connection = new SqliteConnectionStringBuilder { DataSource = immutableUri, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        ItemTemplate[] items;
        using (var db = new WorldDbContext(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite(connection).Options))
        {
            items = [.. db.Set<ItemTemplateRow>().AsNoTracking().AsEnumerable().Select(r => r.ToTemplate())];
        }

        // BoostItemSources.Build over the same tables: permanent vendors, non-event quest rewards, loot rows that name an
        // item (not a reference). A vendor is event-only only when it has event spawns and no permanent spawn.
        using var sql = new SqliteConnection(connection);
        sql.Open();
        HashSet<uint> eventQuests = Ids(sql, "SELECT quest FROM game_event_quest");
        HashSet<uint> eventOnlyVendors = Ids(sql,
            "SELECT DISTINCT c.Entry FROM creature_spawn c JOIN game_event_creature e ON e.guid = c.Guid WHERE e.event > 0");
        eventOnlyVendors.ExceptWith(Ids(sql,
            "SELECT DISTINCT c.Entry FROM creature_spawn c WHERE NOT EXISTS " +
            "(SELECT 1 FROM game_event_creature e WHERE e.guid = c.Guid AND e.event > 0)"));
        var vendor = new HashSet<uint>();
        using (SqliteCommand command = sql.CreateCommand())
        {
            command.CommandText = "SELECT entry, item FROM npc_vendor";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!eventOnlyVendors.Contains((uint)reader.GetInt64(0)))
                {
                    vendor.Add((uint)reader.GetInt64(1));
                }
            }
        }
        var rewards = new HashSet<uint>();
        string rewardColumns = string.Join(", ", Enumerable.Range(1, 6).Select(i => $"RewChoiceItemId{i}").Concat(Enumerable.Range(1, 4).Select(i => $"RewItemId{i}")));
        using (SqliteCommand command = sql.CreateCommand())
        {
            command.CommandText = $"SELECT entry, {rewardColumns} FROM quest_template";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (eventQuests.Contains((uint)reader.GetInt64(0)))
                {
                    continue;
                }

                for (int i = 1; i < reader.FieldCount; i++)
                {
                    rewards.Add((uint)reader.GetInt64(i));
                }
            }
        }

        var loot = new HashSet<uint>();
        foreach (string table in new[] { "creature_loot_template", "gameobject_loot_template", "item_loot_template", "skinning_loot_template",
                     "reference_loot_template", "fishing_loot_template", "pickpocketing_loot_template", "disenchant_loot_template" })
        {
            loot.UnionWith(Ids(sql, $"SELECT Item FROM {table} WHERE MinCountOrRef >= 0"));
        }

        var sources = new HashSet<uint>(vendor);
        sources.UnionWith(rewards);
        sources.UnionWith(loot);
        sources.Remove(0);
        var questOnly = new HashSet<uint>(rewards);
        questOnly.ExceptWith(vendor);
        questOnly.ExceptWith(loot);
        questOnly.Remove(0);
        return new RealBoostContent(items, sources, questOnly, suffixes);
    }

    private static HashSet<uint> Ids(SqliteConnection sql, string query)
    {
        var ids = new HashSet<uint>();
        using SqliteCommand command = sql.CreateCommand();
        command.CommandText = query;
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            ids.Add((uint)reader.GetInt64(0));
        }

        return ids;
    }

    /// <summary>The race used for the class's wear rule (any race the class exists for).</summary>
    public static Race RaceOf(Class cls) => cls switch
    {
        Class.Hunter or Class.Druid => Race.NightElf,
        Class.Shaman => Race.Orc,
        _ => Race.Human,
    };

    /// <summary>The weights <see cref="CharacterBoost.WeightsFor"/> gives a player (not a managed bot) of the class.</summary>
    public static PlayerbotStatWeights WeightsOf(Class cls) => cls switch
    {
        Class.Warrior => PlayerbotStatWeights.ShieldTank,
        Class.Paladin => PlayerbotStatWeights.TwoHandStrength,
        Class.Hunter => PlayerbotStatWeights.Hunter,
        Class.Rogue => PlayerbotStatWeights.DualWieldAgility,
        Class.Priest => PlayerbotStatWeights.Healer,
        Class.Shaman => PlayerbotStatWeights.TwoHandShaman,
        Class.Mage or Class.Warlock => PlayerbotStatWeights.Caster,
        Class.Druid => PlayerbotStatWeights.Feral,
        _ => PlayerbotStatWeights.TwoHandStrength,
    };

    /// <summary>
    /// A class's 1.12 weapon and armor proficiencies once its trainer spells are learned (plate for warriors and paladins and mail for
    /// hunters and shamans from level 40), and dual wield (warriors and hunters from 20, rogues always).
    /// </summary>
    internal sealed class ClassRequirements(Class cls, byte level) : IItemRequirements
    {
        private readonly HashSet<uint> _skills = Skills(cls, level);

        public bool CanDualWield(PlayerInventory inventory)
            => cls == Class.Rogue || (cls is Class.Warrior or Class.Hunter && level >= 20);

        public uint SkillValue(PlayerInventory inventory, uint skill) => _skills.Contains(skill) ? 5u * level : 0u;

        public bool HasSpell(PlayerInventory inventory, uint spellId) => false;

        public byte HonorRank(PlayerInventory inventory) => 0;

        public uint ReputationRank(PlayerInventory inventory, uint faction) => 3;

        private static HashSet<uint> Skills(Class cls, byte level)
        {
            HashSet<uint> s = [ItemSkills.Cloth];
            void Add(params uint[] ids) => s.UnionWith(ids);
            switch (cls)
            {
                case Class.Warrior:
                    Add(ItemSkills.Leather, ItemSkills.Mail, ItemSkills.Shield, ItemSkills.Swords, ItemSkills.Axes, ItemSkills.Maces, ItemSkills.TwoHandedSwords,
                        ItemSkills.TwoHandedAxes, ItemSkills.TwoHandedMaces, ItemSkills.Daggers, ItemSkills.FistWeapons, ItemSkills.Polearms, ItemSkills.Staves,
                        ItemSkills.Bows, ItemSkills.Guns, ItemSkills.Crossbows, ItemSkills.Thrown);
                    if (level >= 40) Add(ItemSkills.PlateMail);
                    break;
                case Class.Paladin:
                    Add(ItemSkills.Leather, ItemSkills.Mail, ItemSkills.Shield, ItemSkills.Swords, ItemSkills.Axes, ItemSkills.Maces, ItemSkills.TwoHandedSwords,
                        ItemSkills.TwoHandedAxes, ItemSkills.TwoHandedMaces, ItemSkills.Polearms);
                    if (level >= 40) Add(ItemSkills.PlateMail);
                    break;
                case Class.Hunter:
                    Add(ItemSkills.Leather, ItemSkills.Swords, ItemSkills.Axes, ItemSkills.TwoHandedSwords, ItemSkills.TwoHandedAxes, ItemSkills.Daggers,
                        ItemSkills.FistWeapons, ItemSkills.Polearms, ItemSkills.Staves, ItemSkills.Bows, ItemSkills.Guns, ItemSkills.Crossbows, ItemSkills.Thrown);
                    if (level >= 40) Add(ItemSkills.Mail);
                    break;
                case Class.Rogue:
                    Add(ItemSkills.Leather, ItemSkills.Daggers, ItemSkills.Swords, ItemSkills.Maces, ItemSkills.FistWeapons, ItemSkills.Bows, ItemSkills.Guns,
                        ItemSkills.Crossbows, ItemSkills.Thrown);
                    break;
                case Class.Priest:
                    Add(ItemSkills.Maces, ItemSkills.Daggers, ItemSkills.Staves, ItemSkills.Wands);
                    break;
                case Class.Shaman:
                    Add(ItemSkills.Leather, ItemSkills.Shield, ItemSkills.Maces, ItemSkills.TwoHandedMaces, ItemSkills.Axes, ItemSkills.TwoHandedAxes,
                        ItemSkills.Daggers, ItemSkills.FistWeapons, ItemSkills.Staves);
                    if (level >= 40) Add(ItemSkills.Mail);
                    break;
                case Class.Mage:
                case Class.Warlock:
                    Add(ItemSkills.Swords, ItemSkills.Daggers, ItemSkills.Staves, ItemSkills.Wands);
                    break;
                case Class.Druid:
                    Add(ItemSkills.Leather, ItemSkills.Maces, ItemSkills.TwoHandedMaces, ItemSkills.Daggers, ItemSkills.FistWeapons, ItemSkills.Staves);
                    break;
            }

            return s;
        }
    }

    public static PlayerInventory InventoryOf(Class cls, byte level)
        => new(default, RaceOf(cls), cls, level) { Requirements = new ClassRequirements(cls, level) };

    public BoostGearPlan Plan(Class cls, byte level) => Plan(cls, level, WeightsOf(cls));

    public BoostGearPlan Plan(Class cls, byte level, PlayerbotStatWeights weights)
    {
        PlayerInventory inventory = InventoryOf(cls, level);
        ItemTemplate[] pool = [.. Items.Where(t => BoostGearPlanner.IsPoolItem(t, level, Sources))];
        return BoostGearPlanner.Plan(pool, weights, cls, inventory.Requirements.CanDualWield(inventory),
            t => inventory.CanUseItem(t) == InventoryResult.Ok, QuestOnly, Suffixes.For);
    }

    /// <summary>The kit's stat totals, template and chosen suffix stats together.</summary>
    public Dictionary<ItemStatType, int> Totals(BoostGearPlan plan)
    {
        var totals = new Dictionary<ItemStatType, int>();
        foreach ((byte slot, ItemTemplate t) in plan.Picks)
        {
            foreach (ItemStat stat in t.Stats.Concat(plan.Suffixes.GetValueOrDefault(slot)?.Stats ?? []).Where(s => s.Value != 0))
            {
                totals[(ItemStatType)stat.Type] = totals.GetValueOrDefault((ItemStatType)stat.Type) + stat.Value;
            }
        }

        return totals;
    }

    public string PoolStages(byte level)
    {
        IEnumerable<ItemTemplate> s = Items.Where(t => t.GetInventoryType() is not (InventoryType.NonEquip or InventoryType.Body or InventoryType.Tabard
            or InventoryType.Bag or InventoryType.Quiver or InventoryType.Ammo));
        var lines = new StringBuilder();
        lines.AppendLine(CultureInfo.InvariantCulture, $"### Pool at level {level}");
        void Stage(string name, IEnumerable<ItemTemplate> set)
        {
            ItemTemplate[] a = [.. set];
            lines.AppendLine(CultureInfo.InvariantCulture,
                $"- {name}: {a.Length} (grey {a.Count(t => t.Quality == 0)}, white {a.Count(t => t.Quality == 1)}, green {a.Count(t => t.Quality == 2)}, blue {a.Count(t => t.Quality == 3)}, epic {a.Count(t => t.Quality == 4)}; random-property {a.Count(t => t.RandomProperty != 0)})");
            s = a;
        }

        Stage("equippable (not shirt/tabard/bag/quiver/ammo)", s);
        Stage($"required level <= {level} and item level <= {(uint)level + BoostGearPlanner.ItemLevelHeadroom}",
            s.Where(t => t.RequiredLevel <= level && t.ItemLevel <= (uint)level + BoostGearPlanner.ItemLevelHeadroom));
        Stage("obtainable (vendor, quest, loot)", s.Where(t => Sources.Contains(t.Entry)));
        Stage("final IsPoolItem", s.Where(t => BoostGearPlanner.IsPoolItem(t, level, Sources)));
        Stage("of which a known suffix when random", s.Where(t => t.RandomProperty == 0 || Suffixes.For(t).Count > 0));
        return lines.ToString();
    }

    public string Describe(Class cls, byte level) => Describe(cls, level, Plan(cls, level), WeightsOf(cls));

    public static string Describe(Class cls, byte level, BoostGearPlan plan, PlayerbotStatWeights weights)
    {
        var lines = new StringBuilder();
        lines.AppendLine(CultureInfo.InvariantCulture, $"### {cls} {level} ({weights.Name})");
        lines.AppendLine("| Slot | Item | Q | ilvl | req | Stats | Armor | DPS |");
        lines.AppendLine("|---|---|---|---|---|---|---|---|");
        var totals = new Dictionary<string, int>();
        foreach ((byte slot, ItemTemplate t) in plan.Picks)
        {
            var stats = new List<string>();
            BoostSuffix? suffix = plan.Suffixes.GetValueOrDefault(slot);
            foreach (ItemStat stat in t.Stats.Concat(suffix?.Stats ?? []).Where(s => s.Value != 0))
            {
                string name = ((ItemStatType)stat.Type).ToString();
                stats.Add($"{stat.Value:+0;-0} {name}");
                totals[name] = totals.GetValueOrDefault(name) + stat.Value;
            }

            float dps = PlayerbotItemScore.WeaponDps(t);
            lines.AppendLine(CultureInfo.InvariantCulture,
                $"| {BoostGearPlanner.SlotName(slot)} | {t.Name}{(suffix is null ? "" : " " + suffix.Name)} ({t.Entry}{(suffix is null ? "" : "/" + suffix.PropertyId)}) | {t.Quality} | {t.ItemLevel} | {t.RequiredLevel} | {string.Join(", ", stats)} | {t.Armor} | {(dps > 0 ? dps.ToString("0.0", CultureInfo.InvariantCulture) : "")} |");
        }

        lines.AppendLine(CultureInfo.InvariantCulture,
            $"Totals: {string.Join(", ", totals.OrderBy(p => p.Key).Select(p => $"{p.Key} {p.Value}"))}; armor {plan.Picks.Sum(p => p.Value.Armor)}; empty {string.Join(", ", plan.EmptySlots.Select(BoostGearPlanner.SlotName))}");
        return lines.ToString();
    }
}

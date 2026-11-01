using ArcaneCore.Game.Loot;
using ArcaneCore.Kernel.Tuning;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;

namespace ArcaneCore.Game.Economy.AuctionBot;

/// <summary>One loot source of the seller: its tables and its "minTemplates, maxTemplates, minRolls, maxRolls" setting.</summary>
public sealed record AuctionBotLootSource(string Name, LootTableKind Kind, IReadOnlyList<uint> Tables, int[] Config);

/// <summary>
/// The loot sources of the seller (cMaNGOS AuctionHouseBot::Initialize :62-90 and AddLootToItemMap :582-612): the creature loot
/// tables split by creature rank, then the disenchant, fishing, gameobject and skinning tables. Each pass draws tables of each source
/// at random and processes each one the configured number of times; the items that drop (not quest items, nor rows behind a
/// condition, since no player is looting) feed the item map. The gameobject source only takes the tables of objects that are spawned and
/// respawn (cMaNGOS AuctionHouseBot::Initialize :85, spawntimesecsmax &gt; 0). The level caps and every other filter are the planner's.
/// </summary>
public sealed class AuctionBotLootSources
{
    private readonly AuctionBotLootSource[] _sources;
    private readonly LootGenerator _generator;
    private readonly Random _random;

    /// <summary>
    /// The loot ids (<c>data1</c>) of the game objects that have a spawn which respawns (cMaNGOS: <c>gameobject_template</c> entries whose
    /// <c>gameobject</c> row has <c>spawntimesecsmax</c> above 0; a spawn that can become other entries counts for each of them).
    /// </summary>
    public static HashSet<uint> RespawningLootIds(GameObjectContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var entries = new HashSet<uint>();
        foreach (GameObjectSpawn spawn in content.Spawns)
        {
            if ((spawn.SpawnTimeMaxSeconds ?? spawn.SpawnTimeSeconds) <= 0)
            {
                continue;
            }

            entries.Add(spawn.Entry);
            foreach (uint alternative in content.GetSpawnEntries(spawn.Guid))
            {
                entries.Add(alternative);
            }
        }

        var lootIds = new HashSet<uint>();
        foreach (uint entry in entries)
        {
            if (content.FindTemplate(entry) is { } template && template.GetData(1) != 0)
            {
                lootIds.Add(template.GetData(1));
            }
        }

        return lootIds;
    }

    /// <param name="rankOf">The creature_template rank of a creature entry (0 normal, 1 elite, 2 rare elite, 3 world boss, 4 rare), or null.</param>
    /// <param name="respawningGameObjectLootIds">The gameobject loot tables that may feed the pool (<see cref="RespawningLootIds"/>); null takes every table.</param>
    public AuctionBotLootSources(LootContent content, Func<uint, uint?> rankOf, AuctionBotOptions options, Random random, TuningCatalog? tuning = null,
        IReadOnlySet<uint>? respawningGameObjectLootIds = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(rankOf);
        ArgumentNullException.ThrowIfNull(options);
        _random = random ?? throw new ArgumentNullException(nameof(random));
        _generator = new LootGenerator(content, random);
        var byRank = new Dictionary<uint, SortedSet<uint>>();
        foreach (CreatureLootInfo creature in content.CreatureInfos)
        {
            if (creature.LootId != 0 && content.HasEntry(LootTableKind.Creature, creature.LootId) && rankOf(creature.Entry) is { } rank)
            {
                (byRank.TryGetValue(rank, out SortedSet<uint>? set) ? set : byRank[rank] = []).Add(creature.LootId);
            }
        }

        uint[] Tables(LootTableKind kind) => [.. content.Rows.Where(r => r.Kind == kind).Select(r => r.Row.Entry).Distinct().Order()];
        uint[] Rank(uint rank) => byRank.TryGetValue(rank, out SortedSet<uint>? set) ? [.. set] : [];
        uint[] gameObjectTables = [.. Tables(LootTableKind.GameObject).Where(t => respawningGameObjectLootIds is null || respawningGameObjectLootIds.Contains(t))];
        _sources =
        [
            new("creature normal", LootTableKind.Creature, Rank(0), options.ParseLoot("creature normal", tuning)),
            new("creature elite", LootTableKind.Creature, Rank(1), options.ParseLoot("creature elite", tuning)),
            new("creature rare elite", LootTableKind.Creature, Rank(2), options.ParseLoot("creature rare elite", tuning)),
            new("creature world boss", LootTableKind.Creature, Rank(3), options.ParseLoot("creature world boss", tuning)),
            new("creature rare", LootTableKind.Creature, Rank(4), options.ParseLoot("creature rare", tuning)),
            new("disenchant", LootTableKind.Disenchant, Tables(LootTableKind.Disenchant), options.ParseLoot("disenchant", tuning)),
            new("fishing", LootTableKind.Fishing, Tables(LootTableKind.Fishing), options.ParseLoot("fishing", tuning)),
            new("gameobject", LootTableKind.GameObject, gameObjectTables, options.ParseLoot("gameobject", tuning)),
            new("skinning", LootTableKind.Skinning, Tables(LootTableKind.Skinning), options.ParseLoot("skinning", tuning)),
        ];
    }

    public IReadOnlyList<AuctionBotLootSource> Sources => _sources;

    /// <summary>One pass over every source: the dropped items and counts, in drop order.</summary>
    public List<(uint Entry, uint Count)> Roll()
    {
        var items = new List<(uint, uint)>();
        foreach (AuctionBotLootSource source in _sources)
        {
            int[] c = source.Config;
            if (c[1] <= 0 || c[3] <= 0 || source.Tables.Count == 0)
            {
                continue;
            }

            int tables = c[0] < 0 ? _random.Next(0, c[1] - c[0] + 1) + c[0] : _random.Next(c[0], c[1] + 1);
            for (int i = 0; i < tables; i++)
            {
                uint table = source.Tables[_random.Next(source.Tables.Count)];
                for (int repeat = _random.Next(c[2], c[3] + 1); repeat > 0; repeat--)
                {
                    foreach (RolledLoot drop in _generator.Roll(source.Kind, table))
                    {
                        if (!drop.IsQuestItem && drop.ConditionId == 0 && drop.ReferenceConditions.Count == 0 && drop.Count > 0)
                        {
                            items.Add((drop.ItemId, drop.Count));
                        }
                    }
                }
            }
        }

        return items;
    }
}

using ArcaneCore.Game.Death.Visibility;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Spells;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Benchmarks;

/// <summary><see cref="SpellbookCache.GetSpells"/> for a level-60 sized book: the read the playerbot think repeats about 20 times.</summary>
[MemoryDiagnoser]
public class SpellbookBenchmarks
{
    private SpellbookCache _cache = null!;
    private Player _player = null!;

    [Params(250)]
    public int BookSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _cache = new SpellbookCache(null, NullLogger.Instance);
        _player = BenchData.Player(7);
        _cache.LoadCharacter(SpellbookCache.CharacterId(_player), [.. Enumerable.Range(0, BookSize).Select(i => (uint)((i * 7919) % 30000) + 1)]);
    }

    [Benchmark]
    public int GetSpellsCount() => _cache.GetSpells(_player).Count;

    [Benchmark]
    public long GetSpellsIterate()
    {
        long sum = 0;
        foreach (uint id in _cache.GetSpells(_player))
        {
            sum += id;
        }

        return sum;
    }
}

/// <summary><see cref="LootGenerator.Roll"/> over every creature loot template of the real world.db, once each per operation.</summary>
[MemoryDiagnoser]
public class LootRollBenchmarks
{
    private LootGenerator _generator = null!;
    private uint[] _creatures = [];

    [GlobalSetup]
    public void Setup()
    {
        LootContent content = BenchData.Loot;
        _creatures = [.. content.Rows.Where(r => r.Kind == LootTableKind.Creature).Select(r => r.Row.Entry).Distinct().Order()];
        _generator = new LootGenerator(content, new Random(1234));
    }

    [Benchmark(OperationsPerInvoke = 4296)]
    public int RollEveryCreatureTemplate()
    {
        int items = 0;
        foreach (uint entry in _creatures)
        {
            items += _generator.Roll(LootTableKind.Creature, entry).Count;
        }

        return items;
    }
}

/// <summary>The playerbot consumable classification over the 999 class-0 (consumable) items of the real world.db, against the real 5875 Spell.dbc.</summary>
[MemoryDiagnoser]
public class ConsumableClassifyBenchmarks
{
    private ItemTemplate[] _consumables = [];
    private ArcaneCore.Game.Spells.SpellStore _spells = null!;

    [GlobalSetup]
    public void Setup()
    {
        _consumables = [.. BenchData.Items.Where(i => i.Class == 0)];
        _spells = BenchData.Spells;
    }

    [Benchmark(OperationsPerInvoke = 999)]
    public int ClassifyConsumables()
    {
        int food = 0;
        foreach (ItemTemplate item in _consumables)
        {
            if (PlayerbotConsumables.TryClassify(item, _spells, out PlayerbotConsumableKind kind) && kind == PlayerbotConsumableKind.Food)
            {
                food++;
            }
        }

        return food;
    }
}

/// <summary>The ghost visibility rule for a living viewer and a living, ungrouped target: the pair every visibility pass checks most.</summary>
[MemoryDiagnoser]
public class GhostVisibilityBenchmarks
{
    private GhostVisibilityRule _rule = null!;
    private Player _viewer = null!;
    private Player _target = null!;

    [GlobalSetup]
    public void Setup()
    {
        var groups = new WorldSpellGroups(() => null);
        _rule = new GhostVisibilityRule(() => groups, () => 1f);
        _viewer = BenchData.Player(11);
        _target = BenchData.Player(12);
    }

    [Benchmark]
    public bool LivingSeesLiving() => _rule.CanSee(_viewer, _target, alreadyVisible: true, detect: false);
}

/// <summary>
/// One world tick of an empty world with <see cref="Handlers"/> <c>Updated</c> and as many <c>WorldTick</c> handlers that do nothing: the
/// dispatch cost itself (the invocation lists, their names and the per-handler timing).
/// </summary>
[MemoryDiagnoser]
public class WorldTickDispatchBenchmarks
{
    private WorldRuntime _world = null!;

    [Params(10, 100)]
    public int Handlers { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _world = new WorldRuntime(new WorldRuntimeOptions { UpdateCompressionThreshold = 0, AutosaveIntervalMs = 0, CommandTimeBudgetMs = 0 },
            new NoSaves(), NullLogger<WorldRuntime>.Instance);
        for (int i = 0; i < Handlers; i++)
        {
            _world.Updated += new Feature().Update;
        }

        for (int i = 0; i < Handlers; i++)
        {
            _world.WorldTick += new Feature().Update;
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark]
    public void RunTick() => _world.RunTick(50);

    private sealed class Feature
    {
        public uint Total { get; private set; }

        public void Update(uint diff) => Total += diff;
    }

    private sealed class NoSaves : ICharacterSaveQueue
    {
        public void Enqueue(CharacterState state)
        {
        }
    }
}

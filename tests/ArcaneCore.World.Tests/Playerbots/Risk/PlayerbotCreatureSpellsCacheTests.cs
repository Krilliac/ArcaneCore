using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Risk;

/// <summary>
/// The process-wide cache of <see cref="PlayerbotCreatureSpells.Of(Creature, SpellStore?)"/>. It is static, so every world in the
/// process shares it (test hosts run their world threads in parallel, all starting on <see cref="SpellStore.Empty"/>); and its
/// answer depends on the EventAI content as much as on the spell store, so a reload of either must not be answered from the old.
/// </summary>
public sealed class PlayerbotCreatureSpellsCacheTests
{
    private const uint BoltId = 9_917_001, StrikeId = 9_917_002;

    private static readonly SpellInfo Bolt = new()
    {
        Id = BoltId, Name = "Bolt",
        Effects = [new SpellEffectInfo { Effect = SpellEffectName.SchoolDamage, TargetA = SpellImplicitTarget.UnitEnemy, BasePoints = 39, DieSides = 11 }],
    };

    private static readonly SpellInfo Strike = new()
    {
        Id = StrikeId, Name = "Strike",
        Effects = [new SpellEffectInfo { Effect = SpellEffectName.Instakill, TargetA = SpellImplicitTarget.UnitEnemy }],
    };

    /// <summary>A reload of the EventAI rows (a new content) with the same spell store: the creature's new cast counts, not the old.</summary>
    [Fact]
    public void AnEventAiReload_IsNotAnsweredFromTheOldContentsCache()
    {
        const uint entry = 991_701;
        var store = new SpellStore([Bolt, Strike], [], []);
        var before = new CreatureAiContent([Cast(entry, 0, BoltId)], []);
        var after = new CreatureAiContent([Cast(entry, 0, StrikeId)], []);

        CreatureSpellThreat old = PlayerbotCreatureSpells.Of(EventAiCreature(entry, null, before), store);
        Assert.False(old.Instakill);
        Assert.Equal(50, old.MaxHit);

        CreatureSpellThreat reloaded = PlayerbotCreatureSpells.Of(EventAiCreature(entry, null, after), store);
        Assert.True(reloaded.Instakill, "the reloaded rows cast an instakill; the cache answered with the old content's bolt");
    }

    /// <summary>Rows keyed to one spawn guid belong to that spawn only: another creature of the entry is not judged by them, nor it by theirs.</summary>
    [Fact]
    public void ASpawnsOwnRows_AreCachedForThatSpawnOnly()
    {
        const uint entry = 991_702, guid = 991_702_77;
        var store = new SpellStore([Bolt, Strike], [], []);
        var content = new CreatureAiContent([Cast(entry, 0, BoltId), Cast(entry, guid, StrikeId)], []);

        Assert.False(PlayerbotCreatureSpells.Of(EventAiCreature(entry, null, content), store).Instakill);
        Assert.True(PlayerbotCreatureSpells.Of(EventAiCreature(entry, guid, content), store).Instakill,
            "the spawn's own row casts an instakill; the cache answered with its entry's");
        Assert.False(PlayerbotCreatureSpells.Of(EventAiCreature(entry, null, content), store).Instakill);
    }

    /// <summary>
    /// Many world threads at once on one spell store and content (every test host starts on <see cref="SpellStore.Empty"/>): no
    /// exception, and afterwards every entry is still answered with its own value. A plain <see cref="Dictionary{TKey, TValue}"/>
    /// written concurrently through its resizes throws, loses entries or corrupts its buckets.
    /// </summary>
    [Fact]
    public async Task ConcurrentWorldThreads_ShareTheCacheSafely()
    {
        const int threads = 8, perThread = 480; // 3840 entries: under the cache's 4096 bound, so every one is cached
        const uint firstEntry = 992_000;
        SpellInfo[] spells = [.. Enumerable.Range(0, threads * perThread).Select(i => new SpellInfo
        {
            Id = 9_920_000u + (uint)i, Name = "Bolt" + i,
            Effects = [new SpellEffectInfo { Effect = SpellEffectName.SchoolDamage, TargetA = SpellImplicitTarget.UnitEnemy, BasePoints = i, DieSides = 1 }],
        })];
        var store = new SpellStore(spells, [], []);
        var content = new CreatureAiContent([.. spells.Select((s, i) => Cast(firstEntry + (uint)i, 0, s.Id))], []);
        Creature[] creatures = [.. Enumerable.Range(0, threads * perThread).Select(i => EventAiCreature(firstEntry + (uint)i, null, content))];

        using var start = new Barrier(threads);
        Task[] workers = [.. Enumerable.Range(0, threads).Select(t => Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            for (int i = t; i < creatures.Length; i += threads) PlayerbotCreatureSpells.Of(creatures[i], store);
        }, TaskCreationOptions.LongRunning))];
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(30)); // a hung writer throws TimeoutException

        for (int i = 0; i < creatures.Length; i++)
            Assert.Equal(i + 1, PlayerbotCreatureSpells.Of(creatures[i], store).MaxHit);
    }

    private static Creature EventAiCreature(uint entry, uint? spawnGuid, CreatureAiContent ai)
    {
        var template = new CreatureTemplate { Entry = entry, Name = "Caster", MinLevelHealth = 20, MaxLevelHealth = 20, AIName = "EventAI" };
        CreatureSpawn? spawn = spawnGuid is { } guid ? new CreatureSpawn { Guid = guid, Entry = entry, MapId = 0, X = 0, Y = 0, Z = 0 } : null;
        var creature = new Creature(spawnGuid ?? entry, template, spawn,
            new CreatureContent([template], spawn is null ? [] : [spawn], [], [], []), new Random(1));
        creature.AI = new CreatureEventAI(creature, ai);
        return creature;
    }

    private static CreatureAiEvent Cast(uint entry, uint spawnGuid, uint spell) => new()
    {
        Id = spawnGuid != 0 ? spawnGuid : entry * 10 + 1, CreatureId = entry, CreatureGuid = spawnGuid, EventType = 0,
        Param3 = 5000, Param4 = 5000,
        Action1 = new CreatureAiAction(PlayerbotCreatureSpells.ActionCast, (int)spell, 1, 0),
    };
}

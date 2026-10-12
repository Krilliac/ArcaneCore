using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using Microsoft.Data.Sqlite;
using Xunit;
using static ArcaneCore.Game.Tests.Instances.InstanceFixture;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// A spell caster seam for script tests: every cast is recorded, a self-cast of Stoned (10255) lands as an aura (any other aura a script
/// asks about can be set by hand), and a cast with a target raises <see cref="SpellHit"/> the way the spell system does when a spell lands.
/// </summary>
internal sealed class ScriptSpellCaster : ICreatureSpellCaster
{
    public List<(Creature Caster, uint Spell, Unit? Target, bool Triggered, long AtMs)> Casts { get; } = [];

    public HashSet<(Unit Unit, uint Spell)> Auras { get; } = [];

    /// <summary>Milliseconds of world time the test has run, stamped on every cast.</summary>
    public long NowMs { get; set; }

    public event Action<Unit, Unit, SpellInfo>? SpellHit;

    public CreatureCastResult Cast(Creature caster, uint spellId, Unit? target, bool triggered)
    {
        Casts.Add((caster, spellId, target, triggered, NowMs));
        if (spellId == StonedDormancy.StonedAura && ReferenceEquals(target, caster))
        {
            Auras.Add((caster, spellId));
        }

        if (target is not null)
        {
            SpellHit?.Invoke(caster, target, new SpellInfo { Id = spellId });
        }

        return CreatureCastResult.Ok;
    }

    public bool IsCasting(Creature caster) => false;

    public bool HasAura(Unit unit, uint spellId) => Auras.Contains((unit, spellId));

    public void RemoveAuras(Unit unit, uint spellId) => Auras.Remove((unit, spellId));

    public void Interrupt(Creature caster)
    {
    }

    public void OnCreatureRemoved(Creature creature)
    {
    }

    /// <summary>A spell landing on <paramref name="target"/> that nobody cast through the seam (what the real spell system raises).</summary>
    public void Hit(Unit caster, Unit target, uint spellId) => SpellHit?.Invoke(caster, target, new SpellInfo { Id = spellId });

    public IEnumerable<(Creature Caster, uint Spell, Unit? Target, bool Triggered, long AtMs)> Of(Creature caster, uint spellId)
        => Casts.Where(c => ReferenceEquals(c.Caster, caster) && c.Spell == spellId);
}

/// <summary>
/// A dungeon instance (map 36) running a real instance script with real creatures, the real <see cref="CreatureAiFactory"/> registrations and
/// the EventAI content given, for the dungeon NPC script tests. Players enter at the dungeon entrance (-16.4, -383.07, 61.78).
/// </summary>
internal sealed class DungeonNpcRig : IDisposable
{
    public const float EntranceX = -16.4f, EntranceY = -383.07f, EntranceZ = 61.78f;
    private readonly InstanceFixture _fixture = new();

    public DungeonNpcRig(Func<Map, InstanceData> script, IEnumerable<CreatureTemplate> templates, IEnumerable<CreatureSpawn> spawns,
        CreatureAiContent? ai = null, ScriptSpellCaster? spells = null, bool alwaysHostile = true, IEnumerable<CreatureAiText>? texts = null,
        ICreatureHostility? hostility = null)
    {
        Spells = spells ?? new ScriptSpellCaster();
        _fixture.Manager.Scripts = new InstanceScriptRegistry().Register(Dungeon, script);
        CreatureAiContent content = ai ?? new CreatureAiContent([], texts ?? []);
        var creatures = new CreatureContent([.. templates], [.. spawns], [], [], [], content);
        var services = new CreatureAiServices { Spells = Spells, Factory = Factory };
        if (hostility is not null)
        {
            services = new CreatureAiServices { Spells = Spells, Factory = Factory, Hostility = hostility };
        }
        else if (alwaysHostile)
        {
            services = new CreatureAiServices { Spells = Spells, Factory = Factory, Hostility = new CreatureAi.AlwaysHostile() };
        }

        var emptyObjects = new GameObjectContent([], [], [], [], []);
        _fixture.World.MapCreated += map =>
        {
            if (map.MapId == Dungeon)
            {
                map.AddUpdater(new CreatureMapSystem(map, creatures, random: new Random(1), aiServices: services));
                map.AddUpdater(new GameObjectMapSystem(map, emptyObjects));
            }
        };

        Player = _fixture.AddPlayer(1);
        Assert.True(_fixture.EnterDungeon(Player));
        Tick();
    }

    public CreatureAiFactory Factory { get; } = new();

    public ScriptSpellCaster Spells { get; }

    public Player Player { get; }

    public Map Map => Player.Map!;

    public InstanceData Data => ArcaneCore.Game.Instances.InstanceManager.InstanceDataOf(Map)!;

    public CreatureMapSystem Creatures => Map.FindUpdater<CreatureMapSystem>()!;

    public InstanceFixture Fixture => _fixture;

    public Creature Creature(uint entry) => Assert.Single(Creatures.Creatures, c => c.Template.Entry == entry);

    public Creature BySpawn(uint spawnGuid) => Assert.Single(Creatures.Creatures, c => c.Spawn?.Guid == spawnGuid);

    /// <summary>A second player in the same instance, standing at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public Player AddPlayerAt(uint guid, float x, float y)
    {
        Player player = _fixture.AddPlayer(guid);
        _fixture.Party(Player, player);
        Assert.True(_fixture.EnterDungeon(player));
        _fixture.Tick();
        player.Relocate(x, y, EntranceZ, 0f, 0);
        return player;
    }

    public void Tick(uint diffMs = 50)
    {
        Spells.NowMs += diffMs;
        _fixture.Tick(diffMs);
    }

    /// <summary>Run <paramref name="ms"/> of world time in 50 ms ticks.</summary>
    public void Run(uint ms)
    {
        for (uint done = 0; done < ms; done += 50)
        {
            Tick();
        }
    }

    public void Dispose() => _fixture.Dispose();
}

/// <summary>Reads rows of the world fixture (ARCANECORE_TEST_WORLD_DB), opened read-only and immutable, never written.</summary>
internal static class WorldFixtureRows
{
    public static List<T> Query<T>(string sql, Func<SqliteDataReader, T> read)
    {
        string path = ScriptCoverageProbe.WorldDbPath()!;
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = new Uri(Path.GetFullPath(path)).AbsoluteUri + "?immutable=1",
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
        {
            rows.Add(read(reader));
        }

        return rows;
    }

    /// <summary>The creature_ai_scripts rows of <paramref name="creatureIds"/> as EventAI content rows (the first action only; the fixture rows have one).</summary>
    public static CreatureAiEvent[] AiEvents(params uint[] creatureIds)
        => [.. Query($"SELECT Id, CreatureId, EventType, EventInversePhaseMask, EventChance, EventFlags, EventFlags32, EventParam1, EventParam2, EventParam3, EventParam4, Action1Type, Action1Param1, Action1Param2, Action1Param3 FROM creature_ai_scripts WHERE CreatureId IN ({string.Join(',', creatureIds)}) ORDER BY Id",
            r => new CreatureAiEvent
            {
                Id = (uint)r.GetInt64(0),
                CreatureId = (uint)r.GetInt64(1),
                EventType = (byte)r.GetInt64(2),
                InversePhaseMask = (uint)r.GetInt64(3),
                Chance = (byte)r.GetInt64(4),
                Flags = r.GetInt64(6) != 0 ? (uint)r.GetInt64(6) : (uint)r.GetInt64(5),
                Param1 = (int)r.GetInt64(7),
                Param2 = (int)r.GetInt64(8),
                Param3 = (int)r.GetInt64(9),
                Param4 = (int)r.GetInt64(10),
                Action1 = new CreatureAiAction((byte)r.GetInt64(11), (int)r.GetInt64(12), (int)r.GetInt64(13), (int)r.GetInt64(14)),
            })];
}

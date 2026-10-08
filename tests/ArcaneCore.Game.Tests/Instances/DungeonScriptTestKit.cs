using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Instances;

internal sealed class DungeonScriptTestKit : IDisposable
{
    private const float X = -16.4f, Y = -383.07f, Z = 61.78f;
    private readonly InstanceFixture _fixture = new();

    public DungeonScriptTestKit(Func<Map, InstanceData> script, uint[] creatureEntries, uint[] spawnEntries,
        (uint Entry, GameObjectType Type)[] gameObjects, CreatureAiContent? ai = null,
        IEnumerable<(uint Entry, uint PathId, CreatureWaypoint Point)>? entryWaypoints = null,
        CreatureAiServices? aiServices = null, IEnumerable<CreatureSpawn>? extraSpawns = null,
        IReadOnlyDictionary<uint, int>? objectSpawnTimes = null, Func<Player, uint, bool>? questReady = null)
    {
        _fixture.Manager.Scripts = new InstanceScriptRegistry().Register(InstanceFixture.Dungeon, script);
        if (questReady is not null) _fixture.Manager.QuestCompleteUnrewarded = questReady;
        CreatureContent content = new(
            [.. creatureEntries.Select(e => Template(e, t => t.Civilian = e is 3678 or 3679 or 3849 or 3850 or 4444 or 10000 or 4627))],
            [.. spawnEntries.Select((e, i) => Spawn((uint)i + 1, e, X + i + 2, Y, Z, mapId: InstanceFixture.Dungeon)), .. extraSpawns ?? []],
            [], [], [], ai ?? new CreatureAiContent([], []), entryWaypoints: entryWaypoints);
        GameObjectContent objects = new(
            [.. gameObjects.Select(g => GameObjectTestKit.GoTemplate(g.Entry, g.Type))],
            [.. gameObjects.Select((g, i) => GameObjectTestKit.GoSpawn((uint)i + 100, g.Entry, X + i + 1, Y,
                    objectSpawnTimes?.GetValueOrDefault(g.Entry) ?? 60)
                with { MapId = InstanceFixture.Dungeon, Z = Z })],
            [], [], []);
        _fixture.World.MapCreated += map =>
        {
            if (map.MapId == InstanceFixture.Dungeon)
            {
                map.AddUpdater(new CreatureMapSystem(map, content, random: new Random(1), aiServices: aiServices));
                map.AddUpdater(new GameObjectMapSystem(map, objects));
            }
        };
        Player = _fixture.AddPlayer(1);
        Assert.True(_fixture.EnterDungeon(Player));
        Tick();
    }

    public Player Player { get; }
    public Map Map => Player.Map!;
    public InstanceData Script => Map.FindUpdater<InstanceData>()!;
    public CreatureMapSystem Creatures => Map.FindUpdater<CreatureMapSystem>()!;
    public GameObjectMapSystem Objects => Map.FindUpdater<GameObjectMapSystem>()!;
    public Creature Creature(uint entry) => Creatures.Creatures.Single(c => c.Entry == entry);
    public GameObject Object(uint entry) => Objects.GameObjects.Single(g => g.Entry == entry);
    public NpcInfo Npc(uint entry)
    {
        Creature c = Creature(entry);
        return new NpcInfo(c.Guid, c.Entry, c.Spawn?.Guid ?? 0, NpcFlags.Gossip, Map.MapId,
            c.X, c.Y, c.Z, 0.5f, c.IsAlive, false, c.Combat.IsInCombat, false, 0);
    }
    public void Tick(uint diffMs = 50) => _fixture.Tick(diffMs);
    public void Kill(uint entry) => Map.Combat.Kill(Player, Creature(entry));
    public IReadOnlyList<byte[]> Sent(WorldOpcode opcode) => _fixture.Sent(Player, opcode);
    public void Dispose() => _fixture.Dispose();
}

internal sealed class RecordingCreatureSpells : ICreatureSpellCaster
{
    public List<uint> Casts { get; } = [];
    public CreatureCastResult Cast(Creature caster, uint spellId, Unit? target, bool triggered)
    {
        Casts.Add(spellId);
        return CreatureCastResult.Ok;
    }

    public bool IsCasting(Creature caster) => false;
    public bool HasAura(Unit unit, uint spellId) => false;
    public void Interrupt(Creature caster) { }
    public void OnCreatureRemoved(Creature creature) { }
    public event Action<Unit, Unit, SpellInfo>? SpellHit { add { } remove { } }
}

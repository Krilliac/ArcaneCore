using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;
using static ArcaneCore.Game.Tests.Instances.InstanceFixture;

namespace ArcaneCore.Game.Tests.Instances;

internal sealed class DungeonScriptHarness : IDisposable
{
    private readonly InstanceFixture _fixture = new();

    public DungeonScriptHarness(Func<Map, InstanceData> script, uint[] templateEntries, uint[] spawnEntries, params (uint Entry, GameObjectType Type)[] gameObjects)
        : this(script, templateEntries, spawnEntries, null, null, gameObjects)
    {
    }

    public DungeonScriptHarness(Func<Map, InstanceData> script, uint[] templateEntries, uint[] spawnEntries,
        RelayScriptCatalog? relays, params (uint Entry, GameObjectType Type)[] gameObjects)
        : this(script, templateEntries, spawnEntries, relays, null, gameObjects)
    {
    }

    public DungeonScriptHarness(Func<Map, InstanceData> script, uint[] templateEntries, uint[] spawnEntries,
        RelayScriptCatalog? relays, CreatureAiServices? aiServices, params (uint Entry, GameObjectType Type)[] gameObjects)
    {
        _fixture.Manager.Scripts = new InstanceScriptRegistry().Register(Dungeon, script);
        var creatureContent = new CreatureContent(
            [.. templateEntries.Distinct().Select(e => Template(e))],
            [.. spawnEntries.Select((e, i) => Spawn(9000u + (uint)i, e, -16.4f + i * 2, -383.07f, 61.78f, mapId: Dungeon))],
            [], [], [], new CreatureAiContent([], []) { RelayScripts = relays ?? RelayScriptCatalog.Empty });
        var objectContent = new GameObjectContent(
            [.. gameObjects.Select(go => go.Type == GameObjectType.Chest
                ? GameObjectTestKit.GoTemplate(go.Entry, go.Type, (3, 1u))
                : GameObjectTestKit.GoTemplate(go.Entry, go.Type))],
            [.. gameObjects.Select((go, i) => GameObjectTestKit.GoSpawn(10000u + (uint)i, go.Entry, -16.4f + i * 2, -380f)
                with { MapId = Dungeon, Z = 61.78f })], [], [], []);
        _fixture.World.MapCreated += map =>
        {
            if (map.MapId == Dungeon)
            {
                map.AddUpdater(new CreatureMapSystem(map, creatureContent, random: new Random(1), aiServices: aiServices ?? new CreatureAiServices()));
                map.AddUpdater(new GameObjectMapSystem(map, objectContent));
            }
        };

        Player = _fixture.AddPlayer(1);
        Assert.True(_fixture.EnterDungeon(Player));
        _fixture.Tick();
    }

    public Player Player { get; }
    public Map Map => Player.Map!;
    public InstanceData Data => InstanceManager.InstanceDataOf(Map)!;
    public CreatureMapSystem Creatures => Map.FindUpdater<CreatureMapSystem>()!;
    public GameObjectMapSystem Objects => Map.FindUpdater<GameObjectMapSystem>()!;
    public Creature Creature(uint entry) => Assert.Single(Creatures.Creatures, c => c.Template.Entry == entry);
    public GameObject Object(uint entry) => Assert.Single(Objects.GameObjects, go => go.Entry == entry);
    public string? SavedData => _fixture.Manager.FindSave(Map.InstanceId)?.Data;
    public void Tick(uint diffMs = 50) => _fixture.Tick(diffMs);
    public void Kill(uint entry) => Map.Combat.Kill(Player, Creature(entry));
    public void Dispose() => _fixture.Dispose();
}

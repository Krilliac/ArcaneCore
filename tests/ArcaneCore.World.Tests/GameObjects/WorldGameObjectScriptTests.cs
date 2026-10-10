using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.GameObjects;

/// <summary>
/// The go_scripts.cpp object scripts of <see cref="WorldGameObjectScriptsFeature"/> (mangos-classic world/go_scripts.cpp): go_andorhal_tower,
/// go_dragon_head, go_unadorned_spike, go_transpolyporter_bb and go_containment_coffer, against a real map with a recording spell caster.
/// </summary>
public sealed class WorldGameObjectScriptTests : IDisposable
{
    private const float X = -8500f;
    private const float Y = 800f;
    private const float Z = 100f;
    private const uint TowerSpawn = 9501;
    private const uint HeadSpawn = 9502;
    private const uint StakeSpawn = 9503;
    private const uint CofferSpawn = 9504;
    private const uint TrapSpawn = 9505;

    private sealed class RecordingCaster : ICreatureSpellCaster
    {
        public List<(Creature Caster, uint Spell, Unit? Target, bool Triggered)> Casts { get; } = [];

        public event Action<Unit, Unit, SpellInfo>? SpellHit
        {
            add { }
            remove { }
        }

        public CreatureCastResult Cast(Creature caster, uint spellId, Unit? target, bool triggered)
        {
            Casts.Add((caster, spellId, target, triggered));
            return CreatureCastResult.Ok;
        }

        public bool IsCasting(Creature caster) => false;

        public bool HasAura(Unit unit, uint spellId) => false;

        public void Interrupt(Creature caster)
        {
        }

        public void OnCreatureRemoved(Creature creature)
        {
        }
    }

    private readonly ServiceProvider _provider;
    private readonly WorldRuntime _world;
    private readonly RecordingCaster _spells = new();
    private readonly CreatureMapSystem _creatures;
    private readonly GameObjectMapSystem _objects;
    private readonly Player _player;
    private readonly List<(Player Player, uint Credit)> _credits = [];

    public WorldGameObjectScriptTests()
    {
        _provider = new ServiceCollection().BuildServiceProvider();
        _world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 },
            new CharacterSaveQueue(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance),
            NullLogger<WorldRuntime>.Instance);
        Map map = _world.GetMap(0);

        uint[] creatureEntries = [DragonHeadAi.Heralds[179556], UnadornedSpikeAi.Thrall, ContainmentCofferAi.RiftSpawn];
        CreatureTemplate[] templates = [.. creatureEntries.Select(entry => new CreatureTemplate
        {
            Entry = entry, Name = $"Script {entry}", MinLevel = 60, MaxLevel = 60, DisplayIds = [16000], Faction = 35,
            MinLevelHealth = 1000, MaxLevelHealth = 1000,
        })];
        CreatureSpawn[] creatureSpawns = [.. creatureEntries.Select((entry, i) => new CreatureSpawn
        {
            Guid = 9100u + (uint)i, Entry = entry, MapId = 0, X = X + 2f + i, Y = Y, Z = Z,
        })];
        _creatures = new CreatureMapSystem(map, new CreatureContent(templates, creatureSpawns, [], [], []),
            aiServices: new CreatureAiServices { Spells = _spells });
        map.AddUpdater(_creatures);

        var tower = new GameObjectTemplate { Entry = 176094, Type = 5, DisplayId = 1, Name = "Tower", Size = 1f, Data = new uint[GameObjectTemplate.DataCount] };
        var head = new GameObjectTemplate { Entry = 179556, Type = 5, DisplayId = 1, Name = "Head", Size = 1f, Data = new uint[GameObjectTemplate.DataCount] };
        var stake = new GameObjectTemplate { Entry = WorldGameObjectScriptsFeature.UnadornedStakeObject, Type = 5, DisplayId = 1, Name = "Stake", Size = 1f, Data = new uint[GameObjectTemplate.DataCount] };
        var coffer = new GameObjectTemplate { Entry = WorldGameObjectScriptsFeature.ContainmentCofferObject, Type = (uint)GameObjectType.Button, DisplayId = 1, Name = "Coffer", Size = 1f, Data = new uint[GameObjectTemplate.DataCount] };
        var trap = new GameObjectTemplate { Entry = WorldGameObjectScriptsFeature.TranspolyporterObject, Type = 5, DisplayId = 1, Name = "Transpolyporter", Size = 1f, Data = new uint[GameObjectTemplate.DataCount] };
        _objects = new GameObjectMapSystem(map, new GameObjectContent([tower, head, stake, coffer, trap],
        [
            Spawn(TowerSpawn, tower.Entry, 0),
            Spawn(HeadSpawn, head.Entry, 1),
            Spawn(StakeSpawn, stake.Entry, 2),
            Spawn(CofferSpawn, coffer.Entry, 3, 0f),
            Spawn(TrapSpawn, trap.Entry, 4),
        ], [], [], []));
        map.AddUpdater(_objects);

        var player = new Player(new CharacterRecord
        {
            Id = 7, AccountId = 1, Name = "Watcher", Race = (byte)Race.Human, Class = (byte)Class.Warrior,
            Gender = (byte)Gender.Male, Level = 60, MapId = 0, X = X, Y = Y - 5f, Z = Z,
        }, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), new ArcaneCore.World.Tests.WorldState.ScourgeInvasionChoreographyTests.RecordingSession());
        player.Inventory.Templates = new ItemTemplateStore(
            [new ItemTemplate { Entry = TranspolyporterAi.ItemGoblinTransponder, Class = 12, Name = "Goblin Transponder", DisplayId = 1, Stackable = 1, Quality = 1 }], []);
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        _player = player;
        _world.AddPlayer(player);
        Run(2);
    }

    private static GameObjectSpawn Spawn(uint guid, uint entry, int slot, float dy = 6f) => new()
    {
        Guid = guid, Entry = entry, MapId = 0, X = X + slot, Y = Y + dy, Z = Z, SpawnTimeSeconds = 5,
    };

    public void Dispose()
    {
        _world.Dispose();
        _provider.Dispose();
    }

    private void Run(int ticks, uint ms = 500)
    {
        for (int i = 0; i < ticks; i++) _world.RunTick(ms);
    }

    private GameObject Object(uint entry) => Assert.Single(_objects.GameObjects, o => o.Entry == entry);

    private Creature Npc(uint entry) => Assert.Single(_creatures.Creatures, c => c.Entry == entry);

    [Fact]
    public void AnAndorhalTowerCreditsItsKillEntryOnlyWhileAWatchtowersQuestIsIncomplete()
    {
        GameObject tower = Object(176094);
        bool incomplete = true;
        var ai = new AndorhalTowerAi((_, quest) => incomplete && quest == AndorhalTowerAi.QuestWatchtowersHorde,
            (player, credit) => _credits.Add((player, credit)));

        Assert.True(ai.OnUse(_objects, tower, _player));
        Assert.Equal([(_player, 10902u)], _credits);

        incomplete = false;
        Assert.True(ai.OnUse(_objects, tower, _player)); // it handles every use, credited or not
        Assert.Single(_credits);

        incomplete = true;
        Assert.True(ai.OnUse(_objects, tower, Npc(UnadornedSpikeAi.Thrall))); // a creature is no player
        Assert.Single(_credits);
    }

    [Fact]
    public void TheFourAndorhalTowersCreditTheirOwnNpcEntries()
    {
        Assert.Equal(new Dictionary<uint, uint> { [176094] = 10902, [176095] = 10903, [176096] = 10904, [176097] = 10905 },
            AndorhalTowerAi.Credits.ToDictionary(p => p.Key, p => p.Value));
        Assert.Equal(5097u, AndorhalTowerAi.QuestWatchtowersAlliance);
        Assert.Equal(5098u, AndorhalTowerAi.QuestWatchtowersHorde);
    }

    [Fact]
    public void ADragonHeadComingIntoTheWorldHasTheNearestLivingHeraldCastRallyingCry()
    {
        GameObject head = Object(179556);
        _objects.RegisterAi(179556, new DragonHeadAi());
        Run(2);
        Assert.Empty(_spells.Casts); // present from the start: no spawn transition, so no cry

        Assert.True(_objects.DespawnForRespawn(head));
        Run(2);
        Assert.False(head.IsSpawned);
        Assert.Empty(_spells.Casts);

        Run(14); // the 5 s respawn delay passes
        Assert.True(head.IsSpawned);
        (Creature caster, uint spell, Unit? target, bool triggered) = Assert.Single(_spells.Casts);
        Assert.Same(Npc(DragonHeadAi.Heralds[179556]), caster);
        Assert.Equal(DragonHeadAi.SpellRallyingCry, spell);
        Assert.Null(target);
        Assert.True(triggered);
    }

    [Fact]
    public void ADragonHeadWhoseHeraldIsDeadCastsNothing()
    {
        GameObject head = Object(179556);
        _objects.RegisterAi(179556, new DragonHeadAi());
        Run(2);
        _creatures.KillCreature(Npc(DragonHeadAi.Heralds[179556]));
        Assert.True(_objects.DespawnForRespawn(head));
        Run(16);
        Assert.True(head.IsSpawned);
        Assert.Empty(_spells.Casts);
    }

    [Fact]
    public void TheUnadornedStakeBeingActivatedHasThrallCastWarchiefsBlessing()
    {
        GameObject stake = Object(WorldGameObjectScriptsFeature.UnadornedStakeObject);
        _objects.RegisterAi(stake.Entry, new UnadornedSpikeAi());
        Run(2);
        Assert.Empty(_spells.Casts);

        stake.LootState = GameObjectLootState.Activated;
        Run(1);
        (Creature caster, uint spell, _, bool triggered) = Assert.Single(_spells.Casts);
        Assert.Same(Npc(UnadornedSpikeAi.Thrall), caster);
        Assert.Equal(UnadornedSpikeAi.SpellWarchiefsBlessing, spell);
        Assert.True(triggered);

        Run(3); // still activated: no second blessing
        Assert.Single(_spells.Casts);
    }

    [Fact]
    public void TheTranspolyporterTakesOnlyAPlayerCarryingAGoblinTransponder()
    {
        var ai = new TranspolyporterAi();
        GameObject trap = Object(WorldGameObjectScriptsFeature.TranspolyporterObject);
        Assert.False(ai.AcceptsTrapTarget(_objects, trap, _player));
        Assert.False(ai.AcceptsTrapTarget(_objects, trap, Npc(UnadornedSpikeAi.Thrall)));

        Assert.Equal(InventoryResult.Ok, _player.Inventory.AddItem(TranspolyporterAi.ItemGoblinTransponder, 1, out _));
        Assert.True(ai.AcceptsTrapTarget(_objects, trap, _player));
        Assert.False(ai.AcceptsTrapTarget(_objects, trap, Npc(UnadornedSpikeAi.Thrall)));
        Assert.False(ai.OnTrapTarget(_objects, trap, _player)); // the engine casts the trap's own spell
    }

    [Fact]
    public void TheContainmentCofferIsUsedOnceByARiftSpawnTwoSecondsAfterItIsFirstSeen()
    {
        GameObject coffer = Object(WorldGameObjectScriptsFeature.ContainmentCofferObject);
        _objects.RegisterAi(coffer.Entry, new ContainmentCofferAi());
        Assert.Equal(GameObjectLootState.Ready, coffer.LootState);

        _world.RunTick(1_000);
        Assert.Equal(GameObjectLootState.Ready, coffer.LootState);
        _world.RunTick(1_000); // 2 s exactly: the timer is still running down (m_startTimer < diff fires)
        Assert.Equal(GameObjectLootState.Ready, coffer.LootState);
        _world.RunTick(1);
        Assert.Equal(GameObjectLootState.Activated, coffer.LootState); // the button was used and toggled
        // dbscripts_on_go_template_use 122088 (command 40, despawn self) is not imported and UseByUnit runs no object script, so the coffer stays.
        Assert.True(coffer.IsSpawned);
    }

    [Fact]
    public void TheContainmentCofferWaitsWhileNoLivingRiftSpawnIsWithinFiveYards()
    {
        GameObject coffer = Object(WorldGameObjectScriptsFeature.ContainmentCofferObject);
        _objects.RegisterAi(coffer.Entry, new ContainmentCofferAi());
        _creatures.KillCreature(Npc(ContainmentCofferAi.RiftSpawn));

        _world.RunTick(2_000);
        _world.RunTick(1_000);
        _world.RunTick(1_000);
        Assert.Equal(GameObjectLootState.Ready, coffer.LootState);
    }

    [Fact]
    public void TheFeatureRegistersEveryScriptOnTheContinents()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var feature = new WorldGameObjectScriptsFeature(services);
        feature.Attach(_world);
        Run(1);

        GameObject head = Object(179556);
        Assert.True(_objects.DespawnForRespawn(head));
        Run(16);
        Assert.Single(_spells.Casts, c => c.Spell == DragonHeadAi.SpellRallyingCry);

        Object(WorldGameObjectScriptsFeature.UnadornedStakeObject).LootState = GameObjectLootState.Activated;
        Run(1);
        Assert.Single(_spells.Casts, c => c.Spell == UnadornedSpikeAi.SpellWarchiefsBlessing);

        Run(6); // the coffer's rift spawn uses it after 2 s
        Assert.Equal(GameObjectLootState.Activated, Object(WorldGameObjectScriptsFeature.ContainmentCofferObject).LootState);
    }
}

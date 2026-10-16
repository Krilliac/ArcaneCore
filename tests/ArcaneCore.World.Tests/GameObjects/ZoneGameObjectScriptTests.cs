using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
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
/// The zone object scripts of <see cref="WorldGameObjectScriptsFeature"/> (mangos-classic blasted_lands.cpp and tirisfal_glades.cpp):
/// go_rune_of_the_defiler, go_mausoleum_door and go_mausoleum_trigger, against a real map.
/// </summary>
public sealed class ZoneGameObjectScriptTests : IDisposable
{
    private const float X = -8500f;
    private const float Y = 800f;
    private const float Z = 100f;
    private const uint RuneSpawn = 9601;
    private const uint DoorSpawn = 9602;
    private const uint TriggerSpawn = 9603;
    private const uint FarTriggerSpawn = 9604;

    private readonly ServiceProvider _provider;
    private readonly WorldRuntime _world;
    private readonly CreatureMapSystem _creatures;
    private readonly GameObjectMapSystem _objects;
    private readonly Player _player;

    public ZoneGameObjectScriptTests()
    {
        _provider = new ServiceCollection().BuildServiceProvider();
        _world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 },
            new CharacterSaveQueue(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance),
            NullLogger<WorldRuntime>.Instance);
        Map map = _world.GetMap(0);

        var ulag = new CreatureTemplate
        {
            Entry = MausoleumDoorAi.NpcUlag, Name = "Ulag the Cleaver", MinLevel = 8, MaxLevel = 8, DisplayIds = [16000], Faction = 35,
            MinLevelHealth = 1000, MaxLevelHealth = 1000,
        };
        _creatures = new CreatureMapSystem(map, new CreatureContent([ulag], [], [], [], []), aiServices: new CreatureAiServices());
        map.AddUpdater(_creatures);

        var rune = new GameObjectTemplate { Entry = WorldGameObjectScriptsFeature.RuneOfTheDefilerObject, Type = (uint)GameObjectType.Trap, DisplayId = 1, Name = "Rune", Size = 1f, Data = new uint[GameObjectTemplate.DataCount] };
        var door = new GameObjectTemplate { Entry = MausoleumDoorAi.DoorObject, Type = (uint)GameObjectType.Door, DisplayId = 1, Name = "Doors", Size = 1f, Data = new uint[GameObjectTemplate.DataCount] };
        var trigger = new GameObjectTemplate { Entry = MausoleumDoorAi.TriggerObject, Type = (uint)GameObjectType.Goober, DisplayId = 1, Name = "Mausoleum Trigger", Size = 1f, Data = new uint[GameObjectTemplate.DataCount] };
        _objects = new GameObjectMapSystem(map, new GameObjectContent([rune, door, trigger],
        [
            Spawn(RuneSpawn, rune.Entry, 0f, 6f),
            Spawn(DoorSpawn, door.Entry, 1f, 6f),
            Spawn(TriggerSpawn, trigger.Entry, 2f, 6f),
            Spawn(FarTriggerSpawn, trigger.Entry, 60f, 6f),
        ], [], [], []));
        map.AddUpdater(_objects);

        var player = new Player(new CharacterRecord
        {
            Id = 7, AccountId = 1, Name = "Watcher", Race = (byte)Race.Human, Class = (byte)Class.Warrior,
            Gender = (byte)Gender.Male, Level = 60, MapId = 0, X = X, Y = Y, Z = Z,
        }, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), new ArcaneCore.World.Tests.WorldState.ScourgeInvasionChoreographyTests.RecordingSession());
        player.Inventory.Templates = new ItemTemplateStore(
            [new ItemTemplate { Entry = RuneOfTheDefilerAi.ItemWardOfTheDefiler, Class = 12, Name = "Ward of the Defiler", DisplayId = 1, Stackable = 1, Quality = 1 }], []);
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        _player = player;
        _world.AddPlayer(player);
        _world.RunTick(500);
    }

    private static GameObjectSpawn Spawn(uint guid, uint entry, float dx, float dy) => new()
    {
        Guid = guid, Entry = entry, MapId = 0, X = X + dx, Y = Y + dy, Z = Z, SpawnTimeSeconds = 5,
    };

    public void Dispose()
    {
        _world.Dispose();
        _provider.Dispose();
    }

    private GameObject Door => Assert.Single(_objects.GameObjects, o => o.Entry == MausoleumDoorAi.DoorObject);

    private GameObject NearTrigger => Assert.Single(_objects.GameObjects, o => o.Spawn?.Guid == TriggerSpawn);

    private IEnumerable<Creature> Ulags => _creatures.Creatures.Where(c => c.Entry == MausoleumDoorAi.NpcUlag);

    // --- go_rune_of_the_defiler ------------------------------------------------------------------------------

    [Fact]
    public void TheRuneTakesOnlyAPlayerCarryingAWardOfTheDefiler()
    {
        var ai = new RuneOfTheDefilerAi();
        GameObject rune = Assert.Single(_objects.GameObjects, o => o.Entry == WorldGameObjectScriptsFeature.RuneOfTheDefilerObject);
        Assert.False(ai.AcceptsTrapTarget(_objects, rune, _player));

        Assert.Equal(InventoryResult.Ok, _player.Inventory.AddItem(RuneOfTheDefilerAi.ItemWardOfTheDefiler, 1, out _));

        Assert.True(ai.AcceptsTrapTarget(_objects, rune, _player));
        Assert.False(ai.OnTrapTarget(_objects, rune, _player)); // the engine casts the trap's own spell
    }

    // --- go_mausoleum_door -----------------------------------------------------------------------------------

    [Fact]
    public void TheDoorWithQuest1819IncompleteResetsTheTriggerAndSummonsUlag_ButStillLetsTheDoorOpen()
    {
        var ai = new MausoleumDoorAi((_, quest) => quest == MausoleumDoorAi.QuestUlag);
        GameObject trigger = NearTrigger;
        trigger.State = GameObjectState.Active;

        bool handled = ai.OnUse(_objects, Door, _player);

        Assert.False(handled); // GOUse returns false: the door opens as usual
        Assert.Equal(GameObjectState.Ready, trigger.State);
        Creature ulag = Assert.Single(Ulags);
        Assert.Equal(MausoleumDoorAi.UlagSpawn.X, ulag.X, 2);
        Assert.Equal(MausoleumDoorAi.UlagSpawn.Y, ulag.Y, 2);
        Assert.True(ulag.IsAlive);
    }

    [Fact]
    public void UlagGoesAfter300SecondsOutOfCombat()
    {
        var ai = new MausoleumDoorAi((_, quest) => quest == MausoleumDoorAi.QuestUlag);
        ai.OnUse(_objects, Door, _player);
        Creature ulag = Assert.Single(Ulags);
        _player.Relocate(ulag.X, ulag.Y, ulag.Z, 0, 0); // keep his grid awake

        for (int i = 0; i < 295; i++) _world.RunTick(1_000);
        Assert.Single(Ulags, c => c.IsAlive);

        for (int i = 0; i < 10; i++) _world.RunTick(1_000);
        Assert.Empty(Ulags);
    }

    [Fact]
    public void TheDoorDoesNothingWithoutTheQuestOrWithoutATriggerWithin30Yards()
    {
        GameObject trigger = NearTrigger;
        trigger.State = GameObjectState.Active;

        Assert.False(new MausoleumDoorAi((_, _) => false).OnUse(_objects, Door, _player));
        Assert.Equal(GameObjectState.Active, trigger.State);
        Assert.Empty(Ulags);

        // The player walks off: both triggers are now more than 30 yards away.
        _player.Relocate(X - 100f, Y, Z, 0, 0);
        GameObject far = Assert.Single(_objects.GameObjects, o => o.Spawn?.Guid == FarTriggerSpawn);
        far.State = GameObjectState.Active;
        trigger.State = GameObjectState.Active;
        Assert.False(new MausoleumDoorAi((_, _) => true).OnUse(_objects, Door, _player));
        Assert.Equal(GameObjectState.Active, far.State);
        Assert.Equal(GameObjectState.Active, trigger.State);
        Assert.Empty(Ulags);
    }

    // --- go_mausoleum_trigger --------------------------------------------------------------------------------

    [Fact]
    public void TheTriggerWithQuest1819IncompletePressesItselfAndUnlocksTheDoor()
    {
        var ai = new MausoleumTriggerAi((_, quest) => quest == MausoleumDoorAi.QuestUlag);
        GameObject trigger = NearTrigger;
        trigger.State = GameObjectState.Ready;
        Door.Flags |= GameObjectFlags.InteractCond | GameObjectFlags.Locked;

        bool handled = ai.OnUse(_objects, trigger, _player);

        Assert.True(handled);
        Assert.Equal(GameObjectState.Active, trigger.State);
        Assert.Equal(GameObjectFlags.None, Door.Flags & GameObjectFlags.InteractCond);
        Assert.Equal(GameObjectFlags.Locked, Door.Flags & GameObjectFlags.Locked); // only the interact condition goes
        Assert.Empty(Ulags);
    }

    [Fact]
    public void TheTriggerLeavesTheUseAloneWithoutTheQuest()
    {
        GameObject trigger = NearTrigger;
        trigger.State = GameObjectState.Ready;
        Door.Flags |= GameObjectFlags.InteractCond;

        Assert.False(new MausoleumTriggerAi((_, _) => false).OnUse(_objects, trigger, _player));

        Assert.Equal(GameObjectState.Ready, trigger.State);
        Assert.Equal(GameObjectFlags.InteractCond, Door.Flags & GameObjectFlags.InteractCond);
    }

    [Fact]
    public void TheTriggerLeavesTheUseAloneWhenNoDoorIsWithin30Yards()
    {
        var ai = new MausoleumTriggerAi((_, _) => true);
        GameObject far = Assert.Single(_objects.GameObjects, o => o.Entry == MausoleumDoorAi.TriggerObject && o.Spawn?.Guid == FarTriggerSpawn);
        _player.Relocate(X + 60f, Y, Z, 0, 0); // beside the far trigger, 59 yards from the doors
        far.State = GameObjectState.Ready;

        Assert.False(ai.OnUse(_objects, far, _player));
        Assert.Equal(GameObjectState.Ready, far.State);
    }

    [Fact]
    public void TheFeatureRegistersTheThreeZoneScripts()
    {
        var feature = new WorldGameObjectScriptsFeature(new ServiceCollection().BuildServiceProvider());
        feature.Attach(_world);
        _world.RunTick(500);

        Assert.IsType<RuneOfTheDefilerAi>(_objects.AiFor(Assert.Single(_objects.GameObjects, o => o.Entry == WorldGameObjectScriptsFeature.RuneOfTheDefilerObject)));
        Assert.IsType<MausoleumDoorAi>(_objects.AiFor(Door));
        Assert.IsType<MausoleumTriggerAi>(_objects.AiFor(NearTrigger));
    }
}

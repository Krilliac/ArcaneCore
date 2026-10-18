using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.GameObjects;

/// <summary>
/// Zone object scripts, batch 12: go_marshal_haggards_chest (mangos-classic elwynn_forest.cpp:36-56) and go_flagongut_fossil (wetlands.cpp:231-253),
/// on a real map with real creatures and a packet-recording player.
/// </summary>
public sealed class ZoneGameObjectScriptBatch12Tests : IDisposable
{
    private const float X = -9553f;
    private const float Y = -1420f;
    private const float Z = 62.377f;

    private sealed class PacketSession : IPlayerSession
    {
        public List<(WorldOpcode Opcode, byte[] Payload)> Sent { get; } = [];

        public int AccountId => 1;

        public AccountSecurity Security => AccountSecurity.Player;

        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload) => Sent.Add((opcode, payload.ToArray()));

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

    private readonly ServiceProvider _provider;
    private readonly WorldRuntime _world;
    private readonly CreatureMapSystem _creatures;
    private readonly GameObjectMapSystem _objects;
    private readonly Player _player;
    private readonly PacketSession _session = new();

    public ZoneGameObjectScriptBatch12Tests()
    {
        _provider = new ServiceCollection().BuildServiceProvider();
        _world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 },
            new CharacterSaveQueue(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance),
            NullLogger<WorldRuntime>.Instance);
        Map map = _world.GetMap(0);

        CreatureTemplate Tpl(uint entry, string name, uint faction) => new()
        {
            Entry = entry, Name = name, MinLevel = 20, MaxLevel = 20, DisplayIds = [16000], Faction = faction, MinLevelHealth = 50_000, MaxLevelHealth = 50_000,
        };
        _creatures = new CreatureMapSystem(map, new CreatureContent(
            [Tpl(HaggardsChestAi.NpcForlornSpirit, "Forlorn Spirit", 14), Tpl(FlagongutFossilAi.NpcProspectorWhelgar, "Prospector Whelgar", 35)],
            [new CreatureSpawn { Guid = 5000, Entry = FlagongutFossilAi.NpcProspectorWhelgar, MapId = 0, X = X + 40f, Y = Y + 4f, Z = Z }], [], [], []),
            aiServices: new CreatureAiServices());
        map.AddUpdater(_creatures);

        GameObjectTemplate Chest(uint entry) => new() { Entry = entry, Type = (uint)GameObjectType.Chest, DisplayId = 1, Name = $"GO {entry}", Size = 1f, Data = new uint[GameObjectTemplate.DataCount] };
        _objects = new GameObjectMapSystem(map, new GameObjectContent([Chest(HaggardsChestAi.ChestObject), Chest(FlagongutFossilAi.FossilObject)],
        [
            new GameObjectSpawn { Guid = 9801, Entry = HaggardsChestAi.ChestObject, MapId = 0, X = X, Y = Y, Z = Z, SpawnTimeSeconds = 5 },
            new GameObjectSpawn { Guid = 9802, Entry = FlagongutFossilAi.FossilObject, MapId = 0, X = X + 40f, Y = Y, Z = Z, SpawnTimeSeconds = 5 },
        ], [], [], []));
        map.AddUpdater(_objects);

        _player = new Player(new CharacterRecord
        {
            Id = 7, AccountId = 1, Name = "Looter", Race = (byte)Race.Human, Class = (byte)Class.Warrior,
            Gender = (byte)Gender.Male, Level = 60, MapId = 0, X = X + 2f, Y = Y, Z = Z,
        }, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), _session);
        _player.MaxHealth = 1_000_000;
        _player.Health = 1_000_000;
        _world.AddPlayer(_player);
        _world.RunTick(500);
    }

    public void Dispose()
    {
        _world.Dispose();
        _provider.Dispose();
    }

    private GameObject Object(uint guid) => Assert.Single(_objects.GameObjects, o => o.Spawn?.Guid == guid);

    private List<Creature> Spirits() => [.. _creatures.Creatures.Where(c => c.Template.Entry == HaggardsChestAi.NpcForlornSpirit && c.IsAlive)];

    [Fact]
    public void HaggardsChest_LootedOutRaisesAForlornSpiritAtTheFarmThatAttacksTheLooter()
    {
        new HaggardsChestAi().OnLootedOut(_objects, Object(9801), _player);

        Creature spirit = Assert.Single(Spirits());
        Assert.Equal(HaggardsChestAi.SpiritSpawn.X, spirit.X, 0.5f);
        Assert.Equal(HaggardsChestAi.SpiritSpawn.Y, spirit.Y, 0.5f);
        Assert.Same(_player, spirit.Combat.Victim);
    }

    [Fact]
    public void HaggardsChest_ALivingSpiritNearbyMeansNoSecondOne()
    {
        var ai = new HaggardsChestAi();
        ai.OnLootedOut(_objects, Object(9801), _player);
        ai.OnLootedOut(_objects, Object(9801), _player);

        Assert.Single(Spirits());
    }

    [Fact]
    public void HaggardsChest_WithoutALooterTheSpiritStillRises()
    {
        new HaggardsChestAi().OnLootedOut(_objects, Object(9801), null);

        Assert.Single(Spirits());
    }

    [Fact]
    public void FlagongutFossil_LootedOutMakesWhelgarTalk_OnlyWithinTenYards()
    {
        var ai = new FlagongutFossilAi();
        _session.Sent.Clear();
        ai.OnLootedOut(_objects, Object(9802), _player);
        Assert.Contains(_session.Sent, s => s.Opcode == WorldOpcode.SmsgEmote);

        Creature whelgar = Assert.Single(_creatures.Creatures, c => c.Template.Entry == FlagongutFossilAi.NpcProspectorWhelgar);
        whelgar.Relocate(X + 40f, Y + 15f, Z, 0f);
        _session.Sent.Clear();
        ai.OnLootedOut(_objects, Object(9802), _player);
        Assert.DoesNotContain(_session.Sent, s => s.Opcode == WorldOpcode.SmsgEmote);
    }

    [Fact]
    public void TheFeatureRegistersBothObjects()
    {
        var feature = new WorldGameObjectScriptsFeature(new ServiceCollection().BuildServiceProvider());
        feature.Attach(_world);
        _world.RunTick(500);

        Assert.IsType<HaggardsChestAi>(_objects.AiFor(Object(9801)));
        Assert.IsType<FlagongutFossilAi>(_objects.AiFor(Object(9802)));
    }
}

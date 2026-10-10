using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.WorldState;

/// <summary>
/// The Necropolis communique network of mangos-classic world/scourge_invasion.cpp: NecropolisAI (:347-377), NecropolisProxyAI (:542-588),
/// NecropolisRelayAI (:590-636), the necropolis objects (:290-298) and the camp's receipt of the relay's communique (:754-757).
/// </summary>
public sealed class ScourgeInvasionNetworkTests : IDisposable
{
    private const float X = -8500f;
    private const float Y = 800f;
    private const float Z = 100f;

    private sealed class RecordingCaster : ICreatureSpellCaster
    {
        public List<(Creature Caster, uint Spell, Unit? Target, bool Triggered)> Casts { get; } = [];
        public HashSet<uint> Auras { get; } = [];

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

        public bool HasAura(Unit unit, uint spellId) => Auras.Contains(spellId);

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
    private readonly Map _map;
    private readonly CreatureMapSystem _creatures;
    private readonly GameObjectMapSystem _objects;

    public ScourgeInvasionNetworkTests()
    {
        _provider = new ServiceCollection().BuildServiceProvider();
        _world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 },
            new CharacterSaveQueue(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance),
            NullLogger<WorldRuntime>.Instance);
        uint[] entries =
        [
            ScourgeInvasionCatalog.Necropolis, ScourgeInvasionCatalog.NecropolisProxy,
            ScourgeInvasionCatalog.NecropolisRelay, ScourgeInvasionCatalog.NecroticShard,
        ];
        CreatureTemplate[] templates = [.. entries.Select(entry => new CreatureTemplate
        {
            Entry = entry, Name = $"Network {entry}", MinLevel = 60, MaxLevel = 60, DisplayIds = [16000], Faction = 35,
            MinLevelHealth = 1000, MaxLevelHealth = 1000,
        })];
        CreatureSpawn[] spawns = [.. entries.Select((entry, i) => new CreatureSpawn
        {
            Guid = 9000u + (uint)i, Entry = entry, MapId = 0, X = X + i * 3f, Y = Y, Z = Z,
        })];
        _map = _world.GetMap(0);
        _creatures = new CreatureMapSystem(_map, new CreatureContent(templates, spawns, [], [], []),
            aiServices: new CreatureAiServices { Spells = _spells });
        _map.AddUpdater(_creatures);
        _creatures.RegisterEntryAi(ScourgeInvasionCatalog.Necropolis, c => new NecropolisAi(c));
        _creatures.RegisterEntryAi(ScourgeInvasionCatalog.NecropolisProxy, c => new NecropolisProxyAi(c));
        _creatures.RegisterEntryAi(ScourgeInvasionCatalog.NecropolisRelay, c => new NecropolisRelayAi(c));
        _creatures.RegisterEntryAi(ScourgeInvasionCatalog.NecroticShard, c => new NecroticShardAi(c));
        _objects = new GameObjectMapSystem(_map, new GameObjectContent(
            [.. ScourgeInvasionCatalog.NecropolisObjects.Select(entry => new GameObjectTemplate
            {
                Entry = entry, Type = 5, DisplayId = 1, Name = "Necropolis", Size = 1f, Data = new uint[GameObjectTemplate.DataCount],
            })],
            [new GameObjectSpawn { Guid = 9500, Entry = 181154, MapId = 0, X = X, Y = Y + 10f, Z = Z }], [], [], []));
        _map.AddUpdater(_objects);
        foreach (uint entry in ScourgeInvasionCatalog.NecropolisObjects) _objects.RegisterAi(entry, new NecropolisObjectAi());

        var player = new Player(new CharacterRecord
        {
            Id = 7, AccountId = 1, Name = "Watcher", Race = (byte)Race.Human, Class = (byte)Class.Warrior,
            Gender = (byte)Gender.Male, Level = 60, MapId = 0, X = X, Y = Y - 5f, Z = Z,
        }, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), new ScourgeInvasionChoreographyTests.RecordingSession());
        _world.AddPlayer(player);
        Run(2);
    }

    public void Dispose()
    {
        _world.Dispose();
        _provider.Dispose();
    }

    private void Run(int ticks)
    {
        for (int i = 0; i < ticks; i++) _world.RunTick(500);
    }

    private Creature Find(uint entry) => Assert.Single(_creatures.Creatures, c => c.Entry == entry);

    private static SpellInfo Spell(uint id) => new() { Id = id };

    private IEnumerable<uint> CastsBy(Creature creature) => _spells.Casts.Where(c => ReferenceEquals(c.Caster, creature)).Select(c => c.Spell);

    [Fact]
    public void TheNetworkCreaturesAndNecropolisObjectsAreActiveObjects()
    {
        foreach (uint entry in new[] { ScourgeInvasionCatalog.Necropolis, ScourgeInvasionCatalog.NecropolisProxy,
                     ScourgeInvasionCatalog.NecropolisRelay, ScourgeInvasionCatalog.NecroticShard })
            Assert.True(_map.Grids.IsActive(Find(entry)), $"creature {entry}");
        GameObject necropolis = Assert.Single(_objects.GameObjects, o => o.Entry == 181154);
        Assert.True(_map.Grids.IsActive(necropolis));
    }

    [Fact]
    public void TheProxyAnswersTheNecropolisCommuniqueAndTheRelaysReply()
    {
        Creature proxy = Find(ScourgeInvasionCatalog.NecropolisProxy);
        proxy.AI!.OnSpellHit(proxy, Spell(ScourgeInvasionCatalog.CommuniqueNecropolisToProxies));
        Assert.Equal([ScourgeInvasionCatalog.CommuniqueProxyToRelay], CastsBy(proxy));
        proxy.AI!.OnSpellHit(proxy, Spell(ScourgeInvasionCatalog.CommuniqueRelayToProxy));
        Assert.Equal([ScourgeInvasionCatalog.CommuniqueProxyToRelay, ScourgeInvasionCatalog.CommuniqueProxyToNecropolis], CastsBy(proxy));
        Assert.All(_spells.Casts, c => Assert.True(c.Triggered));
        proxy.AI!.OnSpellHit(proxy, Spell(ScourgeInvasionCatalog.CommuniqueTrigger)); // not part of its chain
        Assert.Equal(2, CastsBy(proxy).Count());
    }

    [Fact]
    public void TheRelayForwardsTheProxyCommuniqueToTheCampAndTheCampsToTheProxy()
    {
        Creature relay = Find(ScourgeInvasionCatalog.NecropolisRelay);
        relay.AI!.OnSpellHit(relay, Spell(ScourgeInvasionCatalog.CommuniqueProxyToRelay));
        relay.AI!.OnSpellHit(relay, Spell(ScourgeInvasionCatalog.CommuniqueCampToRelay));
        Assert.Equal([ScourgeInvasionCatalog.CommuniqueRelayToCamp, ScourgeInvasionCatalog.CommuniqueRelayToProxy], CastsBy(relay));
        relay.AI!.OnSpellHit(relay, Spell(ScourgeInvasionCatalog.CommuniqueNecropolisToProxies));
        Assert.Equal(2, CastsBy(relay).Count());
    }

    [Fact]
    public void TheNecropolisStartsItsTimerAuraOnlyWhenItIsAbsent()
    {
        Creature necropolis = Find(ScourgeInvasionCatalog.Necropolis);
        necropolis.AI!.OnSpellHit(necropolis, Spell(ScourgeInvasionCatalog.CommuniqueTrigger)); // wrong spell: nothing
        Assert.Empty(CastsBy(necropolis));

        necropolis.AI!.OnSpellHit(necropolis, Spell(ScourgeInvasionCatalog.CommuniqueProxyToNecropolis));
        Assert.Equal([ScourgeInvasionCatalog.CommuniqueTimerNecropolis], CastsBy(necropolis));

        _spells.Auras.Add(ScourgeInvasionCatalog.CommuniqueTimerNecropolis);
        necropolis.AI!.OnSpellHit(necropolis, Spell(ScourgeInvasionCatalog.CommuniqueProxyToNecropolis));
        Assert.Single(CastsBy(necropolis));
    }

    [Fact]
    public void TheCampReceivesTheRelaysCommuniqueAndStartsItsCommuniqueTimerOnce()
    {
        Creature camp = Find(ScourgeInvasionCatalog.NecroticShard);
        Assert.Contains(ScourgeInvasionCatalog.CommuniqueTimerCamp, CastsBy(camp)); // Reset: the 35 s timer aura
        Assert.Single(CastsBy(camp), s => s == ScourgeInvasionCatalog.CommuniqueTimerCamp);
        Run(2);
        Assert.Single(CastsBy(camp), s => s == ScourgeInvasionCatalog.CommuniqueTimerCamp);

        camp.AI!.OnSpellHit(camp, Spell(ScourgeInvasionCatalog.CommuniqueRelayToCamp));
        Assert.Single(CastsBy(camp), s => s == ScourgeInvasionCatalog.CampReceivesCommunique);
        camp.AI!.OnSpellHit(camp, Spell(ScourgeInvasionCatalog.CommuniqueNecropolisToProxies));
        Assert.Single(CastsBy(camp), s => s == ScourgeInvasionCatalog.CampReceivesCommunique);
    }

    [Theory]
    [InlineData(6f, true)]
    [InlineData(4f, false)]
    public void ARelayThatFellBelowItsRespawnHeightIsPutBack(float fall, bool returned)
    {
        Creature relay = Find(ScourgeInvasionCatalog.NecropolisRelay);
        float homeZ = relay.Home.Z;
        _creatures.NearTeleport(relay, relay.X, relay.Y, homeZ - fall, relay.Orientation);
        Assert.Equal(homeZ - fall, relay.Z, 0.01f);
        Run(1);
        // The relay tolerates 5 yd, the proxy and necropolis 10 yd (scourge_invasion.cpp:351-356, :546-551, :595-600).
        Assert.Equal(returned ? homeZ : homeZ - fall, relay.Z, 0.01f);
    }

    [Fact]
    public void AProxyAndTheNecropolisTolerateTenYardsOfFall()
    {
        foreach (uint entry in new[] { ScourgeInvasionCatalog.NecropolisProxy, ScourgeInvasionCatalog.Necropolis })
        {
            Creature c = Find(entry);
            float homeZ = c.Home.Z;
            _creatures.NearTeleport(c, c.X, c.Y, homeZ - 8f, c.Orientation);
            Run(1);
            Assert.Equal(homeZ - 8f, c.Z, 0.01f);
            _creatures.NearTeleport(c, c.X, c.Y, homeZ - 12f, c.Orientation);
            Run(1);
            Assert.Equal(homeZ, c.Z, 0.01f);
        }
    }
}

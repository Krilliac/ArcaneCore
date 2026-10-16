using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.GameObjects;

/// <summary>
/// Zone object scripts, batch 5: go_lightning (mangos-classic azshara.cpp:369-396) and go_covert_ops (stonetalon_mountains.cpp:108-216), against a
/// real map with a packet-recording player and the Venture Co. workers as real creatures.
/// </summary>
public sealed class ZoneGameObjectScriptBatch5Tests : IDisposable
{
    private const float X = -8500f;
    private const float Y = 800f;
    private const float Z = 100f;
    private const uint LightningSpawn = 9701;
    private const uint WagonSpawn = 9702;

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

    public ZoneGameObjectScriptBatch5Tests()
    {
        _provider = new ServiceCollection().BuildServiceProvider();
        _world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 },
            new CharacterSaveQueue(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance),
            NullLogger<WorldRuntime>.Instance);
        Map map = _world.GetMap(0);

        List<CreatureTemplate> templates = [];
        List<CreatureSpawn> spawns = [];
        uint guid = 5000;
        foreach (uint entry in CovertOpsAi.VentureCoWorkers.Append(1234u))
        {
            templates.Add(new CreatureTemplate
            {
                Entry = entry, Name = $"Worker{entry}", MinLevel = 20, MaxLevel = 20, DisplayIds = [16000], Faction = 35, MinLevelHealth = 500, MaxLevelHealth = 500,
            });
            spawns.Add(new CreatureSpawn { Guid = guid++, Entry = entry, MapId = 0, X = X + 20f, Y = Y + 5f, Z = Z });
        }

        _creatures = new CreatureMapSystem(map, new CreatureContent(templates, spawns, [], [], []), aiServices: new CreatureAiServices());
        map.AddUpdater(_creatures);

        var lightning = new GameObjectTemplate { Entry = WorldGameObjectScriptsFeature.TheatricLightningObject, Type = (uint)GameObjectType.Generic, DisplayId = 1, Name = "Lightning", Size = 1f, Data = new uint[GameObjectTemplate.DataCount] };
        var wagon = new GameObjectTemplate { Entry = WorldGameObjectScriptsFeature.VentureCoWagonTrapObject, Type = (uint)GameObjectType.Trap, DisplayId = 1, Name = "Wagon", Size = 1f, Data = new uint[GameObjectTemplate.DataCount] };
        _objects = new GameObjectMapSystem(map, new GameObjectContent([lightning, wagon],
        [
            Spawn(LightningSpawn, lightning.Entry, 0f, 6f),
            Spawn(WagonSpawn, wagon.Entry, 1f, 6f),
        ], [], [], []));
        map.AddUpdater(_objects);

        _player = new Player(new CharacterRecord
        {
            Id = 7, AccountId = 1, Name = "Watcher", Race = (byte)Race.Human, Class = (byte)Class.Warrior,
            Gender = (byte)Gender.Male, Level = 60, MapId = 0, X = X, Y = Y, Z = Z,
        }, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), _session);
        _world.AddPlayer(_player);
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

    private GameObject Flash => Assert.Single(_objects.GameObjects, o => o.Spawn?.Guid == LightningSpawn);

    private GameObject Wagon => Assert.Single(_objects.GameObjects, o => o.Spawn?.Guid == WagonSpawn);

    private Creature Worker(uint entry) => Assert.Single(_creatures.Creatures, c => c.Template.Entry == entry);

    private List<byte[]> Anims() => [.. _session.Sent.Where(s => s.Opcode == WorldOpcode.SmsgGameobjectCustomAnim).Select(s => s.Payload)];

    // --- go_lightning ----------------------------------------------------------------------------------------

    [Fact]
    public void TheLightningTimerStartsAtAWholeNumberOfSecondsFromTwoToFive_OnFirstUpdate()
    {
        var seen = new HashSet<uint>();
        for (int seed = 0; seed < 60; seed++)
        {
            var ai = new LightningAi(new Random(seed));
            Assert.Null(ai.RemainingMs(Flash));
            ai.Update(_objects, Flash, 0);
            uint ms = ai.RemainingMs(Flash)!.Value;
            Assert.Equal(0u, ms % 1000);
            Assert.InRange(ms, 2000u, 5000u);
            seen.Add(ms);
        }

        Assert.Equal([2000u, 3000u, 4000u, 5000u], seen.Order()); // both ends of the range are reachable
    }

    [Fact]
    public void TheLightningFlashesWithAnimationZeroWhenItsTimerRunsOut_AndThenWaitsAFreshTimer()
    {
        var ai = new LightningAi(new Random(3));
        GameObject go = Flash;
        ai.Update(_objects, go, 0);
        uint first = ai.RemainingMs(go)!.Value;
        _session.Sent.Clear();

        ai.Update(_objects, go, first - 1);
        Assert.Empty(Anims());
        Assert.Equal(1u, ai.RemainingMs(go));

        ai.Update(_objects, go, 1); // RemainingMs <= diff: the source's m_uiTimer <= uiDiff
        byte[] anim = Assert.Single(Anims());
        Assert.Equal(12, anim.Length);
        Assert.Equal(go.Guid.Value, BinaryPrimitives.ReadUInt64LittleEndian(anim));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(anim.AsSpan(8)));
        Assert.InRange(ai.RemainingMs(go)!.Value, 2000u, 5000u);
    }

    [Fact]
    public void TheLightningHoldsItsTimerWhileTheObjectIsNotSpawned()
    {
        var ai = new LightningAi(new Random(5));
        GameObject go = Flash;
        ai.Update(_objects, go, 0);
        uint before = ai.RemainingMs(go)!.Value;
        _objects.Despawn(go);
        Assert.False(go.IsSpawned);
        _session.Sent.Clear();

        ai.Update(_objects, go, 60_000);

        Assert.Equal(before, ai.RemainingMs(go));
        Assert.Empty(Anims());
    }

    [Fact]
    public void EachLightningObjectKeepsItsOwnTimer()
    {
        var ai = new LightningAi(new Random(11));
        GameObject a = Flash;
        GameObject b = Wagon;
        ai.Update(_objects, a, 0);
        Assert.Null(ai.RemainingMs(b));
        ai.Update(_objects, b, 0);
        ai.Update(_objects, a, 1500);

        Assert.NotNull(ai.RemainingMs(b));
    }

    // --- go_covert_ops ---------------------------------------------------------------------------------------

    private void Spring(CovertOpsAi ai, GameObject wagon)
    {
        wagon.LootState = GameObjectLootState.JustDeactivated;
        ai.Update(_objects, wagon, 0);
    }

    [Fact]
    public void TheWagonDoesNothingUntilItIsSprung()
    {
        var ai = new CovertOpsAi();
        GameObject wagon = Wagon;

        ai.Update(_objects, wagon, 60_000);

        Assert.Equal(CovertOpsAi.Phase.Inactive, ai.PhaseOf(wagon));
        Assert.All(CovertOpsAi.VentureCoWorkers, e => Assert.Equal(MovementGeneratorType.Idle, Worker(e).Motion.CurrentType));
    }

    [Fact]
    public void ASprungWagonCallsEveryAliveWorkerAfterTwoSeconds_AndSendsThemHomeFortyFiveSecondsLater()
    {
        var ai = new CovertOpsAi();
        GameObject wagon = Wagon;
        Spring(ai, wagon);
        Assert.Equal(CovertOpsAi.Phase.Calling, ai.PhaseOf(wagon));

        ai.Update(_objects, wagon, 2000); // timer < diff is false at equality
        Assert.Equal(CovertOpsAi.Phase.Calling, ai.PhaseOf(wagon));
        Assert.All(CovertOpsAi.VentureCoWorkers, e => Assert.False(Worker(e).ScriptRun));

        ai.Update(_objects, wagon, 1);
        Assert.Equal(CovertOpsAi.Phase.Homing, ai.PhaseOf(wagon));
        foreach (uint entry in CovertOpsAi.VentureCoWorkers)
        {
            Creature worker = Worker(entry);
            Assert.True(worker.ScriptRun);
            Assert.Equal(MovementGeneratorType.Point, worker.Motion.CurrentType);
        }

        ai.Update(_objects, wagon, 45_000);
        Assert.Equal(CovertOpsAi.Phase.Homing, ai.PhaseOf(wagon));
        Assert.All(CovertOpsAi.VentureCoWorkers, e => Assert.True(Worker(e).ScriptRun));

        ai.Update(_objects, wagon, 1);
        Assert.Equal(CovertOpsAi.Phase.Inactive, ai.PhaseOf(wagon));
        Assert.All(CovertOpsAi.VentureCoWorkers, e => Assert.False(Worker(e).ScriptRun));
    }

    [Fact]
    public void TheWorkersRunToThePointTenYardsFromTheWagonOnTheirSide()
    {
        var ai = new CovertOpsAi();
        GameObject wagon = Wagon;
        Creature worker = Worker(3991);
        Spring(ai, wagon);
        ai.Update(_objects, wagon, 2001);

        float wanted = CovertOpsAi.ContactDistance + wagon.BoundingRadius + worker.BoundingRadius;
        float dx = worker.X - wagon.X, dy = worker.Y - wagon.Y;
        float length = MathF.Sqrt((dx * dx) + (dy * dy));

        Assert.Equal(MovementGeneratorType.Point, worker.Motion.CurrentType);
        for (int i = 0; i < 100; i++)
        {
            _world.RunTick(100); // it runs the 19 yd at ~7 yd/s and stops at the point
        }

        Assert.Equal(wagon.X + (dx / length * wanted), worker.X, 0.5f);
        Assert.Equal(wagon.Y + (dy / length * wanted), worker.Y, 0.5f);
    }

    [Fact]
    public void WorkersOutOfRangeOfAnotherEntryDeadOrFightingAreLeftAlone()
    {
        var ai = new CovertOpsAi();
        GameObject wagon = Wagon;
        Worker(3989).Relocate(X + 150f, Y, Z); // 100+ yd from the wagon
        _world.GetMap(0).Combat.Kill(null, Worker(3999));
        _creatures.AttackStart(Worker(4004), _player);
        Creature other = Worker(1234); // not a Venture Co. entry
        Spring(ai, wagon);

        ai.Update(_objects, wagon, 2001);

        Assert.True(Worker(3991).ScriptRun);
        Assert.True(Worker(3988).ScriptRun);
        Assert.False(Worker(3989).ScriptRun);
        Assert.False(Worker(3999).ScriptRun);
        Assert.False(Worker(4004).ScriptRun);
        Assert.False(other.ScriptRun);
        Assert.Equal(MovementGeneratorType.Idle, other.Motion.CurrentType);
    }

    [Fact]
    public void AWorkerThatStartsFightingBeforeTheReturnIsNotSentHome()
    {
        var ai = new CovertOpsAi();
        GameObject wagon = Wagon;
        Spring(ai, wagon);
        ai.Update(_objects, wagon, 2001);
        _creatures.AttackStart(Worker(3991), _player);

        ai.Update(_objects, wagon, 45_001);

        Assert.True(Worker(3991).ScriptRun); // still running: it was in combat
        Assert.False(Worker(3989).ScriptRun);
    }

    [Fact]
    public void TheWagonSeesAnSpringOnlyOncePerSpawn_AndARespawnStartsItOver()
    {
        var ai = new CovertOpsAi();
        GameObject wagon = Wagon;
        Spring(ai, wagon);
        ai.Update(_objects, wagon, 2001);
        ai.Update(_objects, wagon, 45_001);
        Assert.Equal(CovertOpsAi.Phase.Inactive, ai.PhaseOf(wagon));

        ai.Update(_objects, wagon, 100); // the object still reads just-deactivated: the edge is not seen twice
        Assert.Equal(CovertOpsAi.Phase.Inactive, ai.PhaseOf(wagon));

        wagon.LootState = GameObjectLootState.Ready;
        ai.OnRespawn(_objects, wagon);
        Spring(ai, wagon);
        Assert.Equal(CovertOpsAi.Phase.Calling, ai.PhaseOf(wagon));
        ai.Update(_objects, wagon, 2001);
        Assert.Equal(CovertOpsAi.Phase.Homing, ai.PhaseOf(wagon));
    }

    [Fact]
    public void ARespawnMidEventCancelsIt()
    {
        var ai = new CovertOpsAi();
        GameObject wagon = Wagon;
        Spring(ai, wagon);
        Assert.Equal(CovertOpsAi.Phase.Calling, ai.PhaseOf(wagon));

        wagon.LootState = GameObjectLootState.Ready;
        ai.OnRespawn(_objects, wagon);
        ai.Update(_objects, wagon, 10_000);

        Assert.Equal(CovertOpsAi.Phase.Inactive, ai.PhaseOf(wagon));
        Assert.All(CovertOpsAi.VentureCoWorkers, e => Assert.False(Worker(e).ScriptRun));
    }

    [Fact]
    public void AnUnspawnedWagonIsNotSeenSpringing()
    {
        var ai = new CovertOpsAi();
        GameObject wagon = Wagon;
        _objects.Despawn(wagon);
        Assert.False(wagon.IsSpawned);

        ai.Update(_objects, wagon, 0);

        Assert.Equal(CovertOpsAi.Phase.Inactive, ai.PhaseOf(wagon));
    }

    // --- registration and the quest seam -----------------------------------------------------------------------

    [Fact]
    public void TheFeatureRegistersTheTwoScripts()
    {
        var feature = new WorldGameObjectScriptsFeature(new ServiceCollection().BuildServiceProvider());
        feature.Attach(_world);
        _world.RunTick(500);

        Assert.IsType<LightningAi>(_objects.AiFor(Flash));
        Assert.IsType<CovertOpsAi>(_objects.AiFor(Wagon));
    }

    [Fact]
    public void TheFeatureDrivesTheLightningThroughTheMapUpdate()
    {
        var feature = new WorldGameObjectScriptsFeature(new ServiceCollection().BuildServiceProvider());
        feature.Attach(_world);
        _world.RunTick(500);
        _session.Sent.Clear();

        for (int i = 0; i < 80; i++)
        {
            _world.RunTick(100);
        }

        // 8 s of ticks: at least one flash (every 2 to 5 s) and at most four.
        Assert.InRange(Anims().Count, 1, 4);
    }

    [Fact]
    public void WithoutTheQuestFeatureNoQuestIsIncomplete()
    {
        var events = new ScriptQuestEvents(new ServiceCollection().BuildServiceProvider());

        Assert.False(events.IsQuestIncomplete(_player, 9364));
    }
}

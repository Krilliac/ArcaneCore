using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Scripting;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.HotCode;
using ArcaneCore.World.HotCode.Modules;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.HotCode;

/// <summary>
/// A module swap that changes EventAI event handlers respawns the live database creatures bound to the old ones (owner policy, item 8): through
/// the map's own despawn/respawn path, at most <see cref="EventAiRespawnPolicy.BatchSize"/> per batch (one batch per world tick), creatures in
/// combat deferred and retried, creatures whose rows do not use a changed event type left alone. The HotScripts fixture adds a handler for event
/// type 200; the creatures are built before the load, so their type 200 rows are bound to no handler and need the respawn to pick it up.
/// </summary>
public sealed class ModuleEventAiRespawnTests : IDisposable
{
    private const byte ModuleEventType = 200;
    private const uint AffectedEntry = 990401;
    private const uint OtherEventAiEntry = 990402;
    private const uint PlainEntry = 990403;
    private const uint LoaderEntry = 990404;
    private const int BatchSize = 3;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "arcane-modules-eventai-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly ThreadedWorld _world = new();
    private readonly OpcodeTable _opcodes = new();
    private readonly CommandTableSource _commands;
    private readonly CreatureAiFactory _ai = new();
    private readonly ScriptHookRegistry _hooks = new();
    private readonly ServiceProvider _services;
    private readonly WorldRuntime _runtime;
    private readonly CreatureMapSystem _system;
    private readonly SpellScriptRegistry _spells = new([]);
    private readonly InstanceScriptRegistry _instances = new();
    private readonly EventAiRegistry _eventAi = EventAiRegistry.Discover(typeof(EventAiRegistry).Assembly);
    private readonly CreatureAiContent _rows;
    private readonly SemaphoreSlim _batchGate = new(0);
    private readonly SemaphoreSlim _batchDone = new(0);
    private readonly Dictionary<ObjectGuid, int> _respawns = [];
    private readonly List<(int Respawns, int Waiting)> _batches = [];

    public ModuleEventAiRespawnTests()
    {
        _root = Path.Combine(_dir, "modules");
        Directory.CreateDirectory(_root);
        new BaseOpcodeGroup().Register(_opcodes);
        _commands = new CommandTableSource(new CommandTable(new BaseCommandGroup().Commands));
        var collection = new ServiceCollection();
        collection.AddLogging();
        _services = collection.BuildServiceProvider();
        var saves = new CharacterSaveQueue(_services.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance);
        _runtime = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves, NullLogger<WorldRuntime>.Instance);

        _rows = new CreatureAiContent(
            [
                new CreatureAiEvent { Id = 1, CreatureId = AffectedEntry, EventType = ModuleEventType },
                new CreatureAiEvent { Id = 2, CreatureId = OtherEventAiEntry, EventType = (byte)EventAiEventType.Aggro },
            ],
            []);
        CreatureTemplate[] templates = [Template(AffectedEntry), Template(OtherEventAiEntry), Template(PlainEntry), Template(LoaderEntry)];
        List<CreatureSpawn> spawns = [];
        for (uint i = 1; i <= 7; i++)
        {
            spawns.Add(Spawn(i, AffectedEntry, i));
        }

        spawns.Add(Spawn(20, OtherEventAiEntry, 20));
        spawns.Add(Spawn(21, PlainEntry, 21));
        Map map = _runtime.GetMap(0);
        _system = new CreatureMapSystem(map, new CreatureContent(templates, spawns, [], [], [], _rows), random: new Random(1), aiServices: new CreatureAiServices { Factory = _ai });
        map.AddUpdater(_system);

        // A temporary creature at the spawns loads their grid (and so creates them); it then goes again.
        _system.Despawn(_system.SpawnTemporary(Template(LoaderEntry), 1, 0, 83.5f, 0));
        foreach (Creature creature in _system.Creatures.Where(c => c.Entry is AffectedEntry or OtherEventAiEntry))
        {
            creature.AI = new CreatureEventAI(creature, _rows, _eventAi); // on this test's registry, not the shared Default
        }

        _system.Respawning += c => _respawns[c.Guid] = _respawns.GetValueOrDefault(c.Guid) + 1;
    }

    public void Dispose()
    {
        _world.Dispose();
        _runtime.Dispose();
        _services.Dispose();
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // a still-mapped file on a loaded machine must not fail the test
        }
    }

    private static CreatureTemplate Template(uint entry) => new() { Entry = entry, Name = "EventAI respawn " + entry, MinLevelHealth = 50, MaxLevelHealth = 50 };

    private static CreatureSpawn Spawn(uint guid, uint entry, float x)
        => new() { Guid = guid, Entry = entry, MapId = 0, X = x, Y = 0, Z = 83.5f, SpawnTimeMinSeconds = 120, SpawnTimeMaxSeconds = 120 };

    private Creature[] Creatures(uint entry) => [.. _system.Creatures.Where(c => c.Entry == entry).OrderBy(c => c.Spawn!.Guid)];

    private ModuleHost NewHost()
    {
        var seams = new ModuleGameSeams(
            () => _ai, () => _hooks, () => _runtime.Maps, _world.Post, NullLogger.Instance,
            spellScripts: () => _spells,
            instanceScripts: () => _instances,
            eventAi: () => _eventAi,
            eventAiRespawn: new EventAiRespawnPolicy(BatchSize, TimeSpan.FromMilliseconds(1)),
            waitBetweenEventAiBatches: () => _batchGate.WaitAsync(TimeSpan.FromSeconds(30)));
        seams.EventAiRespawnBatchRan += (_, waiting) =>
        {
            // Counted from the map's own Respawning event, not from the sweep's own counter.
            int total = _respawns.Values.Sum();
            _batches.Add((total - _batches.Sum(b => b.Respawns), waiting));
            _batchDone.Release();
        };
        Directory.CreateDirectory(Path.Combine(_root, "HotScripts"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "hotfixtures", "scripts1", "HotScripts.dll"), Path.Combine(_root, "HotScripts", "HotScripts.dll"), overwrite: true);
        return new ModuleHost(
            new HotModuleOptions { Enabled = true, Directory = _root },
            _world, _opcodes, _commands,
            new HotCodeAudit(Path.Combine(_dir, "audit.log")),
            NullLogger.Instance,
            TimeSpan.FromSeconds(10),
            reporter: new RecordingReporter(),
            seams: seams);
    }

    /// <summary>Let one batch run on the world thread and wait for it.</summary>
    private async Task RunBatch()
    {
        _batchGate.Release();
        Assert.True(await _batchDone.WaitAsync(TimeSpan.FromSeconds(30)), "the batch did not run");
    }

    private bool BoundToCurrentHandler(Creature creature)
        => creature.AI is CreatureEventAI ai && ai.Engine.Holders.All(h => ReferenceEquals(h.Handler, _eventAi.FindEvent(h.Event.EventType)));

    [Fact]
    public async Task AffectedCreatures_RespawnOverSeveralTicks_NeverMoreThanTheCapPerTick()
    {
        Creature[] affected = Creatures(AffectedEntry);
        Assert.Equal(7, affected.Length);
        Assert.All(affected, c => Assert.Null(Assert.Single(((CreatureEventAI)c.AI!).Engine.Holders).Handler)); // no handler for type 200 yet
        ModuleHost host = NewHost();

        ModuleResult load = await host.LoadAsync("HotScripts");

        Assert.True(load.Ok, load.Detail);
        Assert.Contains(load.Fields!, f => f.Name == "eventai_respawns_queued" && f.Value == "7");
        Assert.Empty(_respawns); // nothing respawns in the commit tick itself

        await RunBatch();
        await RunBatch();
        await RunBatch();

        Assert.Equal([(3, 4), (3, 1), (1, 0)], _batches);
        Assert.All(affected, c => Assert.Equal(1, _respawns.GetValueOrDefault(c.Guid)));
        Assert.All(affected, c => Assert.Equal(CreatureDeathState.Alive, c.DeathState));
        Assert.All(affected, c => Assert.True(BoundToCurrentHandler(c), "a respawned creature runs the module's handler"));
        Assert.All(affected, c => Assert.True(_eventAi.IsCurrent(Assert.Single(((CreatureEventAI)c.AI!).Engine.Holders).Handler!)));

        // Done: no further batch is scheduled.
        _batchGate.Release();
        Assert.False(await _batchDone.WaitAsync(TimeSpan.FromMilliseconds(300)), "the sweep must stop once every creature respawned");
    }

    [Fact]
    public async Task ACreatureInCombat_IsDeferred_ThenRespawnedOnceItLeavesCombat()
    {
        Creature[] affected = Creatures(AffectedEntry);
        Creature fighting = affected[0];
        _runtime.GetMap(0).Combat.SetInCombatState(fighting, 0);
        Assert.True(fighting.Combat.IsInCombat);
        ModuleHost host = NewHost();
        Assert.True((await host.LoadAsync("HotScripts")).Ok);

        await RunBatch();
        await RunBatch();
        await RunBatch();

        Assert.All(affected.Skip(1), c => Assert.Equal(1, _respawns.GetValueOrDefault(c.Guid)));
        Assert.False(_respawns.ContainsKey(fighting.Guid), "a creature in combat must not be respawned");
        Assert.False(BoundToCurrentHandler(fighting));
        Assert.Equal(1, _batches[^1].Waiting);

        MapCombat.ClearInCombat(fighting);
        await RunBatch();

        Assert.Equal(1, _respawns.GetValueOrDefault(fighting.Guid));
        Assert.True(BoundToCurrentHandler(fighting));
        Assert.Equal(0, _batches[^1].Waiting);
    }

    [Fact]
    public async Task CreaturesWhoseRowsDoNotUseAChangedEventType_AreUntouched()
    {
        Creature other = Assert.Single(Creatures(OtherEventAiEntry));
        Creature plain = Assert.Single(Creatures(PlainEntry));
        CreatureAI otherAi = other.AI!;
        CreatureAI? plainAi = plain.AI;
        EventAiEventHandler? otherHandler = Assert.Single(((CreatureEventAI)otherAi).Engine.Holders).Handler;
        ModuleHost host = NewHost();
        Assert.True((await host.LoadAsync("HotScripts")).Ok);

        await RunBatch();
        await RunBatch();
        await RunBatch();

        Assert.Equal(7, _respawns.Count);
        Assert.False(_respawns.ContainsKey(other.Guid));
        Assert.False(_respawns.ContainsKey(plain.Guid));
        Assert.Same(otherAi, other.AI);
        Assert.Same(plainAi, plain.AI);
        Assert.Same(otherHandler, Assert.Single(((CreatureEventAI)other.AI!).Engine.Holders).Handler);
        Assert.Equal(CreatureDeathState.Alive, other.DeathState);
    }
}

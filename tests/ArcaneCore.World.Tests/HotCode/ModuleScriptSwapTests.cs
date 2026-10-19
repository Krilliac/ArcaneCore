using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Scripting;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Tests;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.HotCode;
using ArcaneCore.World.HotCode.Modules;
using ArcaneCore.World.Ops.Announce;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.HotCode;

/// <summary>
/// The module lane's spell scripts, instance scripts and EventAI handlers (tests/hotmodule-fixtures/HotScriptsV1 and V2, one spell script, one
/// instance script, one EventAI event and one action handler) swapped through real <see cref="SpellScriptRegistry"/>,
/// <see cref="InstanceScriptRegistry"/> and <see cref="EventAiRegistry"/> objects: load makes them active, reload replaces them, unload removes
/// them and lets the module be collected, a clash with a built-in rejects the whole module with nothing changed, and the retired-code report
/// names an instance still running a replaced script.
/// </summary>
public sealed class ModuleScriptSwapTests : IDisposable
{
    private const uint SpellId = 990201;
    private const uint ExtraSpellId = 990202;
    private const uint MapId = 9901;
    private const byte EventType = 200;
    private const byte ActionType = 200;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "arcane-modules-scripts-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly ThreadedWorld _world = new();
    private readonly OpcodeTable _opcodes = new();
    private readonly CommandTableSource _commands;
    private readonly CreatureAiFactory _ai = new();
    private readonly ScriptHookRegistry _hooks = new();
    private readonly RecordingReporter _reporter = new();
    private readonly ServiceProvider _services;
    private readonly WorldRuntime _runtime;
    private SpellScriptRegistry _spells = new([]);
    private InstanceScriptRegistry _instances = new();
    private EventAiRegistry _eventAi = EventAiRegistry.Discover(typeof(EventAiRegistry).Assembly);

    public ModuleScriptSwapTests()
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

    private ModuleHost NewHost(bool withSeams = true, bool withScriptRegistries = true)
        => new(
            new HotModuleOptions { Enabled = true, Directory = _root },
            _world, _opcodes, _commands,
            new HotCodeAudit(Path.Combine(_dir, "audit.log")),
            NullLogger.Instance,
            TimeSpan.FromSeconds(10),
            reporter: _reporter,
            seams: withSeams
                ? new ModuleGameSeams(
                    () => _ai, () => _hooks, () => _runtime.Maps, _world.Post, NullLogger.Instance,
                    spellScripts: withScriptRegistries ? () => _spells : null,
                    instanceScripts: withScriptRegistries ? () => _instances : null,
                    eventAi: withScriptRegistries ? () => _eventAi : null)
                : null);

    private void Install(int version)
    {
        Directory.CreateDirectory(Path.Combine(_root, "HotScripts"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "hotfixtures", "scripts" + version, "HotScripts.dll"), Path.Combine(_root, "HotScripts", "HotScripts.dll"), overwrite: true);
    }

    // NoInlining: a local of a module type in a test frame would keep the module alive and fake a leak.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int VersionOf(object moduleObject)
    {
        Type type = moduleObject.GetType();
        AssemblyLoadContext? context = AssemblyLoadContext.GetLoadContext(type.Assembly);
        Assert.True(context is { IsCollectible: true } && context != AssemblyLoadContext.Default, $"{type.FullName} must come from the module's collectible context");
        return (int)type.Assembly.GetType("HotScripts.Marker")!.GetField("Version")!.GetRawConstantValue()!;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int SpellScriptVersion(uint spellId) => VersionOf(_spells.Find(spellId) ?? throw new Xunit.Sdk.XunitException($"spell {spellId} has no script"));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int EventHandlerVersion() => VersionOf(_eventAi.FindEvent(EventType) ?? throw new Xunit.Sdk.XunitException("no event handler"));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int ActionHandlerVersion() => VersionOf(_eventAi.FindAction(ActionType) ?? throw new Xunit.Sdk.XunitException("no action handler"));

    /// <summary>Creates instance data for <see cref="MapId"/> from the registry's current script and reads its version (the data is dropped).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int InstanceScriptVersion() => VersionOf(_instances.Create(_runtime.GetMap(MapId, 99)) ?? throw new Xunit.Sdk.XunitException($"map {MapId} has no instance script"));

    /// <summary>A loaded instance: its data is created from the current script and attached to its map (where it stays).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void OpenInstance(uint instanceId)
    {
        Map map = _runtime.GetMap(MapId, instanceId);
        map.AddUpdater(_instances.Create(map)!);
    }

    private static async Task<bool> WaitForRetiredCount(ModuleHost host, int expected)
    {
        for (int i = 0; i < 80; i++)
        {
            AllocationProbe.CollectBetweenWindows();
            if (host.RetiredStillReferenced == expected)
            {
                return true;
            }

            await Task.Delay(25);
        }

        return false;
    }

    [Fact]
    public async Task Load_MakesTheSpellScriptInstanceScriptAndEventAiHandlersActive()
    {
        Install(1);
        ModuleHost host = NewHost();
        Assert.Null(_spells.Find(SpellId));
        Assert.False(_instances.HasScript(MapId));
        Assert.Null(_eventAi.FindEvent(EventType));
        Assert.Null(_eventAi.FindAction(ActionType));

        ModuleResult load = await host.LoadAsync("HotScripts");

        Assert.True(load.Ok, load.Detail);
        Assert.Equal(1, SpellScriptVersion(SpellId));
        Assert.Null(_spells.Find(ExtraSpellId));
        Assert.False(_spells.IsBuiltIn(SpellId));
        Assert.Single(_spells.HotScripts);
        Assert.True(_instances.HasScript(MapId));
        Assert.False(_instances.IsBuiltIn(MapId));
        Assert.Equal(1, InstanceScriptVersion());
        Assert.Equal(1, EventHandlerVersion());
        Assert.Equal(1, ActionHandlerVersion());
        Assert.Equal(EventType, Assert.Single(_eventAi.ModuleEventTypes));
        Assert.Equal(ActionType, Assert.Single(_eventAi.ModuleActionTypes));
        ModuleInfo info = Assert.Single(host.List());
        Assert.Equal((1, 1, 2), (info.SpellScripts, info.InstanceScripts, info.EventAiHandlers));
        Assert.Equal(0, info.Opcodes);
        IReadOnlyList<AnnounceField> fields = Assert.Single(_reporter.Events).Fields!;
        Assert.Contains(fields, f => f.Name == "spell_scripts" && f.Value == "1");
        Assert.Contains(fields, f => f.Name == "instance_scripts" && f.Value == "9901");
        Assert.Contains(fields, f => f.Name == "eventai" && f.Value == "1 events, 1 actions");
        Assert.Contains("1 spell scripts, 1 instance scripts, 2 EventAI handlers", HotModuleCommands.Format(host));
    }

    [Fact]
    public async Task Reload_ReplacesEveryScript_WithTheNewVersion_AndTheOldOneIsCollected()
    {
        Install(1);
        ModuleHost host = NewHost();
        Assert.True((await host.LoadAsync("HotScripts")).Ok);
        long instanceGeneration = _instances.Generation;
        long eventAiGeneration = _eventAi.Generation;
        Install(2);

        ModuleResult reload = await host.ReloadAsync("HotScripts");

        Assert.True(reload.Ok, reload.Detail);
        Assert.Equal(2, SpellScriptVersion(SpellId));
        Assert.Equal(2, SpellScriptVersion(ExtraSpellId)); // version 2 serves one more id
        Assert.Same(_spells.Find(SpellId), _spells.Find(ExtraSpellId));
        Assert.Single(_spells.HotScripts);
        Assert.Equal(2, InstanceScriptVersion());
        Assert.Equal(2, EventHandlerVersion());
        Assert.Equal(2, ActionHandlerVersion());
        Assert.Equal(instanceGeneration + 1, _instances.Generation);
        Assert.Equal(eventAiGeneration + 1, _eventAi.Generation);
        Assert.Contains(_reporter.Events[^1].Fields!, f => f.Name == "replaced_sha256");
        Assert.True(await WaitForRetiredCount(host, 0), "the replaced version must be collected: no registry may keep a version 1 object");
    }

    [Fact]
    public async Task Unload_RemovesEveryScript_AndNothingKeepsTheModule()
    {
        Install(1);
        ModuleHost host = NewHost();
        Assert.True((await host.LoadAsync("HotScripts")).Ok);

        ModuleResult unload = await host.UnloadAsync("HotScripts");

        Assert.True(unload.Ok, unload.Detail);
        Assert.Null(_spells.Find(SpellId));
        Assert.Empty(_spells.HotScripts);
        Assert.False(_instances.HasScript(MapId));
        Assert.Empty(_instances.ModuleMapIds);
        Assert.Null(_eventAi.FindEvent(EventType));
        Assert.Null(_eventAi.FindAction(ActionType));
        Assert.Empty(_eventAi.ModuleEventTypes);
        Assert.Empty(_eventAi.ModuleActionTypes);
        Assert.Empty(host.List());
        Assert.True(await WaitForRetiredCount(host, 0), "after unload the spell, instance and EventAI registries must have let go of the module");
    }

    [Theory]
    [InlineData("spell")]
    [InlineData("instance")]
    [InlineData("eventai-event")]
    public async Task AClashWithABuiltIn_RejectsTheWholeModule_AndChangesNothing(string clash)
    {
        switch (clash)
        {
            case "spell":
                _spells = new SpellScriptRegistry([new BuiltInClashScript()]);
                break;
            case "instance":
                _instances.Register(MapId, map => new BuiltInClashInstance(map));
                break;
            default:
                EventAiHandlerSet builtIns = EventAiRegistry.DiscoverHandlers([typeof(EventAiRegistry).Assembly]);
                _eventAi = EventAiRegistry.FromHandlers([.. builtIns.Events, new BuiltInClashEventHandler(EventType)], builtIns.Actions);
                break;
        }

        ISpellScript? spellBefore = _spells.Find(SpellId);
        long instanceGeneration = _instances.Generation;
        long eventAiGeneration = _eventAi.Generation;
        EventAiEventHandler? eventBefore = _eventAi.FindEvent(EventType);
        int hooksBefore = _hooks.Registered.Count;
        Install(1);
        ModuleHost host = NewHost();

        ModuleResult result = await host.LoadAsync("HotScripts");

        Assert.False(result.Ok);
        Assert.False(result.Failed); // a refusal, not a half-applied commit
        Assert.Matches("built[- ]in", result.Detail);
        Assert.Contains(clash == "spell" ? SpellId.ToString() : clash == "instance" ? MapId.ToString() : EventType.ToString(), result.Detail);
        Assert.Empty(host.List());
        Assert.Same(spellBefore, _spells.Find(SpellId));
        Assert.Empty(_spells.HotScripts);
        Assert.Equal(instanceGeneration, _instances.Generation);
        Assert.Empty(_instances.ModuleMapIds);
        Assert.Equal(eventAiGeneration, _eventAi.Generation);
        Assert.Same(eventBefore, _eventAi.FindEvent(EventType));
        Assert.Empty(_eventAi.ModuleEventTypes);
        Assert.Empty(_eventAi.ModuleActionTypes);
        Assert.Equal(hooksBefore, _hooks.Registered.Count);
        Assert.Equal(HotSwapResult.Rejected, Assert.Single(_reporter.Events).Result);
        Assert.True(await WaitForRetiredCount(host, 0), "the rejected module must not stay loaded");
    }

    [Fact]
    public async Task AnInstanceStillRunningAReplacedScript_IsReportedAsRetiredCode_AndKeepsTheOldVersionAlive()
    {
        Install(1);
        ModuleHost host = NewHost();
        Assert.True((await host.LoadAsync("HotScripts")).Ok);
        OpenInstance(1);
        Assert.Equal(new ModuleRetiredGame(0, 0), host.RetiredGameReferences());

        Install(2);
        Assert.True((await host.ReloadAsync("HotScripts")).Ok);

        // The loaded instance keeps its version 1 data (its progress); a new instance gets version 2.
        Assert.Equal(new ModuleRetiredGame(1, 0), host.RetiredGameReferences());
        Assert.Equal(2, InstanceScriptVersion());
        Assert.False(await WaitForRetiredCount(host, 0), "the instance's version 1 data must keep version 1 loaded");
        Assert.Contains("1 instance maps until they unload", HotModuleCommands.Format(host));
    }

    private const uint EventAiEntry = 990301;

    /// <summary>A real creature in a loaded map whose EventAI (on this test's registry) has one row of the module's event type, bound now.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Creature SpawnEventAiCreature()
    {
        var template = new CreatureTemplate { Entry = EventAiEntry, Name = "EventAI creature", MinLevelHealth = 50, MaxLevelHealth = 50 };
        Map map = _runtime.GetMap(0);
        var system = new CreatureMapSystem(map, new CreatureContent([template], [], [], [], []), random: new Random(1), aiServices: new CreatureAiServices { Factory = _ai });
        map.AddUpdater(system);
        Creature creature = system.SpawnTemporary(template, 1, 0, 83.5f, 0);
        var rows = new CreatureAiContent([new CreatureAiEvent { Id = 1, CreatureId = EventAiEntry, EventType = EventType }], []);
        var ai = new CreatureEventAI(creature, rows, _eventAi);
        creature.AI = ai;
        Assert.NotNull(Assert.Single(ai.Engine.Holders).Handler); // bound to the module's handler
        return creature;
    }

    /// <summary>A weak reference to the load context the module's spell script came from (nothing of the module stays in this frame).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference ModuleContext()
        => new(AssemblyLoadContext.GetLoadContext((_spells.Find(SpellId) ?? throw new Xunit.Sdk.XunitException("no module spell script")).GetType().Assembly)
            ?? throw new Xunit.Sdk.XunitException("no load context"));

    private static async Task<bool> WaitForCollected(WeakReference context)
    {
        for (int i = 0; i < 80 && context.IsAlive; i++)
        {
            AllocationProbe.CollectBetweenWindows();
            await Task.Delay(25);
        }

        return !context.IsAlive;
    }

    [Fact]
    public async Task Unload_TheModulesLoadContextIsCollected()
    {
        Install(1);
        ModuleHost host = NewHost();
        Assert.True((await host.LoadAsync("HotScripts")).Ok);
        WeakReference context = ModuleContext();
        Assert.True(context.IsAlive);

        Assert.True((await host.UnloadAsync("HotScripts")).Ok);

        Assert.True(await WaitForCollected(context), "the unloaded module's load context must be collected");
        Assert.Equal(0, host.RetiredStillReferenced);
    }

    [Fact]
    public async Task AnEventAiCreatureStillBoundToAnUnloadedHandler_IsCounted_KeepsTheModuleAlive_UntilItRespawns()
    {
        Install(1);
        ModuleHost host = NewHost();
        Assert.True((await host.LoadAsync("HotScripts")).Ok);
        Creature creature = SpawnEventAiCreature();
        WeakReference context = ModuleContext();
        Assert.Equal(new ModuleRetiredGame(0, 0), host.RetiredGameReferences());

        Assert.True((await host.UnloadAsync("HotScripts")).Ok);

        // The creature's row still holds the version 1 handler: counted, and it keeps the module loaded.
        Assert.Equal(new ModuleRetiredGame(0, 1), host.RetiredGameReferences());
        Assert.False(await WaitForCollected(context), "the creature's bound handler must keep the unloaded module alive");
        Assert.Equal(1, host.RetiredStillReferenced);
        Assert.Contains("1 EventAI creatures", HotModuleCommands.Format(host));

        // A respawn rebuilds the rows against the current table (no handler for type 200 any more): nothing references the module now.
        ((CreatureEventAI)creature.AI!).OnRespawn();

        Assert.Equal(new ModuleRetiredGame(0, 0), host.RetiredGameReferences());
        Assert.True(await WaitForCollected(context), "after the respawn the unloaded module must be collected");
        Assert.Equal(0, host.RetiredStillReferenced);
    }

    [Fact]
    public async Task AnInstanceStillRunningAnUnloadedScript_IsCounted_AndKeepsTheModuleAlive()
    {
        Install(1);
        ModuleHost host = NewHost();
        Assert.True((await host.LoadAsync("HotScripts")).Ok);
        OpenInstance(1);
        WeakReference context = ModuleContext();

        Assert.True((await host.UnloadAsync("HotScripts")).Ok);

        Assert.Equal(new ModuleRetiredGame(1, 0), host.RetiredGameReferences());
        Assert.False(await WaitForCollected(context), "the loaded instance's data must keep the unloaded module alive");
        Assert.Equal(1, host.RetiredStillReferenced);
    }

    [Fact]
    public void NoDiscoveryScanOfThisAssembly_FindsTheBuiltInClashHandler()
    {
        EventAiHandlerSet found = EventAiRegistry.DiscoverHandlers([typeof(ModuleScriptSwapTests).Assembly]);

        Assert.DoesNotContain(found.Events, h => h is BuiltInClashEventHandler);
        Assert.DoesNotContain(found.Events, h => h.EventType == EventType);
    }

    [Fact]
    public async Task AServerWhoseSeamsLackTheScriptRegistries_RefusesTheModule()
    {
        Install(1);
        ModuleHost host = NewHost(withScriptRegistries: false);

        ModuleResult result = await host.LoadAsync("HotScripts");

        Assert.False(result.Ok);
        Assert.Contains("spell scripts are not available", result.Detail);
        Assert.Empty(host.List());
    }

    [Fact]
    public async Task AServerWithoutGameSeams_RefusesTheModuleBeforeAnyCommit()
    {
        Install(1);
        ModuleHost host = NewHost(withSeams: false);

        ModuleResult result = await host.LoadAsync("HotScripts");

        Assert.False(result.Ok);
        Assert.Contains("does not offer spell scripts, instance scripts or EventAI handlers", result.Detail);
        Assert.Empty(host.List());
    }

    [SpellScript(SpellId)]
    private sealed class BuiltInClashScript : ISpellScript;

    private sealed class BuiltInClashInstance(Map instance) : InstanceData(instance);

    /// <summary>
    /// A built-in event handler for the fixture's event type, handed to <see cref="EventAiRegistry.FromHandlers"/>. The constructor argument keeps
    /// it out of every discovery scan of this assembly (<see cref="EventAiRegistry.DiscoverHandlers"/> only takes parameterless handlers).
    /// </summary>
    private sealed class BuiltInClashEventHandler(byte type) : EventAiEventHandler
    {
        public override byte EventType => type;

        public override bool Check(EventAiContext context, EventAiHolder holder, Unit? invoker) => false;
    }
}

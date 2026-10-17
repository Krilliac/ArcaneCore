using System.Runtime.CompilerServices;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Scripting;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Protocol;
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
/// A module swap that fails after validation is rolled back in every slot (design hot-reload-tiers.md section 3.5): opcodes, creature AI, script
/// hooks, spell scripts, instance scripts, EventAI and commands are exactly as before, the module keeps its previous state, the reply and the
/// announcement say "failed". Faults are injected after each slot changed (<see cref="ModuleHost.FaultInjection"/>), on a replace of each fixture
/// module (HotFixture: opcodes and commands; HotGame: creature AI and hooks; HotScripts: spell, instance and EventAI scripts), so every slot is
/// both removed from and added to before it is undone. Also: a failing <see cref="SpellScriptRegistry.ScriptsChanged"/> handler fails the swap
/// visibly, and a registry that went away does not keep a module from being unloaded.
/// </summary>
public sealed class ModuleRollbackTests : IDisposable
{
    private const uint SpellId = 990201;
    private const uint ExtraSpellId = 990202;
    private const uint MapId = 9901;
    private const byte EventType = 200;
    private const byte ActionType = 200;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "arcane-modules-rollback-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly ThreadedWorld _world = new();
    private readonly OpcodeTable _opcodes = new();
    private readonly CommandTableSource _commands;
    private readonly CreatureAiFactory _ai = new();
    private readonly ScriptHookRegistry _hooks = new();
    private readonly RecordingReporter _reporter = new();
    private readonly ServiceProvider _services;
    private readonly WorldRuntime _runtime;
    private readonly SpellScriptRegistry _spells = new([]);
    private readonly InstanceScriptRegistry _instances = new();
    private readonly EventAiRegistry _eventAi = EventAiRegistry.Discover(typeof(EventAiRegistry).Assembly);
    private bool _instancesGone;

    public ModuleRollbackTests()
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

    public static TheoryData<string, string> FixtureAndSlot()
    {
        var data = new TheoryData<string, string>();
        foreach (string fixture in new[] { "HotFixture", "HotGame", "HotScripts" })
        {
            foreach (string slot in SwapSlots.InCommitOrder)
            {
                data.Add(fixture, slot);
            }
        }

        return data;
    }

    private ModuleHost NewHost()
        => new(
            new HotModuleOptions { Enabled = true, Directory = _root },
            _world, _opcodes, _commands,
            new HotCodeAudit(Path.Combine(_dir, "audit.log")),
            NullLogger.Instance,
            TimeSpan.FromSeconds(10),
            reporter: _reporter,
            seams: new ModuleGameSeams(
                () => _ai, () => _hooks, () => _runtime.Maps, _world.Post, NullLogger.Instance,
                spellScripts: () => _spells,
                instanceScripts: () => _instancesGone ? null : _instances,
                eventAi: () => _eventAi));

    private void Install(string module, int version)
    {
        (string folder, string dll) = module switch
        {
            "HotFixture" => ("v" + version, "HotFixture"),
            "HotGame" => ("game" + version, "HotGame"),
            _ => ("scripts" + version, "HotScripts"),
        };
        Directory.CreateDirectory(Path.Combine(_root, module));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "hotfixtures", folder, dll + ".dll"), Path.Combine(_root, module, module + ".dll"), overwrite: true);
    }

    private static Action<string> FailAt(string slot)
        => current =>
        {
            if (current == slot)
            {
                throw new InvalidOperationException($"injected fault at {slot}");
            }
        };

    /// <summary>Everything a module swap can change, by reference (the objects themselves, not copies).</summary>
    private sealed record LiveState(
        Dictionary<WorldOpcode, OpcodeHandler> Opcodes,
        ChatCommand[] Roots,
        (bool Entry1, bool Entry2, bool Named) Ai,
        IScriptHooks[] Hooks,
        ISpellScript? Spell,
        ISpellScript? ExtraSpell,
        ISpellScript[] HotSpells,
        SpellEffectName[] ExecuteEffects,
        uint[] ModuleMaps,
        bool? InstanceDataCurrent,
        EventAiEventHandler? Event,
        EventAiActionHandler? Action,
        byte[] ModuleEvents,
        byte[] ModuleActions);

    // NoInlining: the captured state holds module objects, and must not survive in a caller's frame longer than the test needs it.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private LiveState Capture(InstanceData? instanceData)
    {
        var opcodes = new Dictionary<WorldOpcode, OpcodeHandler>();
        foreach (WorldOpcode opcode in _opcodes.Opcodes)
        {
            Assert.True(_opcodes.TryGet(opcode, out OpcodeHandler handler));
            opcodes[opcode] = handler;
        }

        return new LiveState(
            opcodes,
            [.. _commands.Current.Roots],
            (_ai.HasScript(990001, string.Empty), _ai.HasScript(990002, string.Empty), _ai.HasScript(0, "HotGameAI")),
            [.. _hooks.Registered],
            _spells.Find(SpellId),
            _spells.Find(ExtraSpellId),
            [.. _spells.HotScripts],
            [.. _spells.ExecuteEffects.Order()],
            [.. _instances.ModuleMapIds.Order()],
            instanceData is null ? null : _instances.RunsCurrentScript(instanceData),
            _eventAi.FindEvent(EventType),
            _eventAi.FindAction(ActionType),
            [.. _eventAi.ModuleEventTypes.Order()],
            [.. _eventAi.ModuleActionTypes.Order()]);
    }

    private static void AssertSame(LiveState expected, LiveState actual)
    {
        Assert.Equal(expected.Opcodes.Keys.Order(), actual.Opcodes.Keys.Order());
        Assert.All(expected.Opcodes, pair => Assert.Same(pair.Value, actual.Opcodes[pair.Key]));
        Assert.Equal(expected.Roots.Length, actual.Roots.Length);
        Assert.All(expected.Roots.Zip(actual.Roots), pair => Assert.Same(pair.First, pair.Second)); // same objects, same order
        Assert.Equal(expected.Ai, actual.Ai);
        Assert.Equal(expected.Hooks.Length, actual.Hooks.Length);
        Assert.All(expected.Hooks.Zip(actual.Hooks), pair => Assert.Same(pair.First, pair.Second));
        Assert.Same(expected.Spell, actual.Spell);
        Assert.Same(expected.ExtraSpell, actual.ExtraSpell);
        Assert.Equal(expected.HotSpells.Length, actual.HotSpells.Length);
        Assert.All(expected.HotSpells.Zip(actual.HotSpells), pair => Assert.Same(pair.First, pair.Second));
        Assert.Equal(expected.ExecuteEffects, actual.ExecuteEffects);
        Assert.Equal(expected.ModuleMaps, actual.ModuleMaps);
        Assert.Equal(expected.InstanceDataCurrent, actual.InstanceDataCurrent);
        Assert.Same(expected.Event, actual.Event);
        Assert.Same(expected.Action, actual.Action);
        Assert.Equal(expected.ModuleEvents, actual.ModuleEvents);
        Assert.Equal(expected.ModuleActions, actual.ModuleActions);
    }

    /// <summary>What version 2 adds that version 1 lacks: proof that a later, clean reload applies it (the rollback left a consistent state).</summary>
    private bool Version2IsLive(string module) => module switch
    {
        "HotFixture" => _commands.Current.Roots.Any(r => r.Name == "hotfixtureextra") && _opcodes.TryGet(WorldOpcode.CmsgTutorialFlag, out _),
        "HotGame" => _ai.HasScript(990002, string.Empty),
        _ => _spells.Find(ExtraSpellId) is not null,
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private InstanceData? OpenInstanceIfScripted()
        => _instances.HasScript(MapId) ? _instances.Create(_runtime.GetMap(MapId, 1)) : null;

    [Theory]
    [MemberData(nameof(FixtureAndSlot))]
    public async Task AReplaceThatFailsAfterValidation_InAnySlot_RollsEverySlotBack(string module, string slot)
    {
        Install(module, 1);
        ModuleHost host = NewHost();
        Assert.True((await host.LoadAsync(module)).Ok);
        string sha1 = Assert.Single(host.List()).Sha256;
        InstanceData? v1Data = OpenInstanceIfScripted();
        LiveState before = Capture(v1Data);
        Install(module, 2);
        host.FaultInjection = FailAt(slot);

        ModuleResult result = await host.ReloadAsync(module);

        Assert.False(result.Ok);
        Assert.True(result.Failed, result.Detail);
        Assert.Contains($"injected fault at {slot}", result.Detail);
        Assert.Contains("every slot was rolled back", result.Detail);
        AssertSame(before, Capture(v1Data));
        Assert.Equal(sha1, Assert.Single(host.List()).Sha256); // version 1 stays the loaded module
        Assert.Equal(HotSwapResult.Failed, _reporter.Events[^1].Result);
        Assert.Contains("module-failed", await File.ReadAllTextAsync(Path.Combine(_dir, "audit.log")));

        host.FaultInjection = null;
        ModuleResult retry = await host.ReloadAsync(module);
        Assert.True(retry.Ok, retry.Detail);
        Assert.True(Version2IsLive(module), "after the rollback a clean reload must apply version 2");
    }

    [Theory]
    [InlineData("HotScripts", SwapSlots.EventAi)]
    [InlineData("HotGame", SwapSlots.Commands)]
    [InlineData("HotFixture", SwapSlots.Opcodes)]
    public async Task ALoadThatFailsAfterValidation_LeavesNothingBehind_AndTheModuleIsCollected(string module, string slot)
    {
        Install(module, 1);
        ModuleHost host = NewHost();
        LiveState before = Capture(null);
        host.FaultInjection = FailAt(slot);

        ModuleResult result = await host.LoadAsync(module);

        Assert.True(result.Failed, result.Detail);
        Assert.Empty(host.List());
        AssertSame(before, Capture(null));
        Assert.True(await WaitForRetiredCount(host, 0), "a rolled-back load must not keep the module alive");
    }

    [Fact]
    public async Task AnUnloadThatFailsAfterValidation_KeepsTheModuleLoadedAndServing()
    {
        Install("HotScripts", 1);
        ModuleHost host = NewHost();
        Assert.True((await host.LoadAsync("HotScripts")).Ok);
        InstanceData? v1Data = OpenInstanceIfScripted();
        LiveState before = Capture(v1Data);
        host.FaultInjection = FailAt(SwapSlots.Commands);

        ModuleResult result = await host.UnloadAsync("HotScripts");

        Assert.True(result.Failed, result.Detail);
        AssertSame(before, Capture(v1Data));
        Assert.Equal("HotScripts", Assert.Single(host.List()).Name);
    }

    [Fact]
    public async Task AFailingScriptsChangedHandler_FailsTheSwapVisibly_AndRollsItBack()
    {
        Install("HotScripts", 1);
        ModuleHost host = NewHost();
        Assert.True((await host.LoadAsync("HotScripts")).Ok);
        InstanceData? v1Data = OpenInstanceIfScripted();
        LiveState before = Capture(v1Data);
        _spells.ScriptsChanged += () => throw new InvalidOperationException("effect chaining failed");
        Install("HotScripts", 2);

        ModuleResult result = await host.ReloadAsync("HotScripts");

        Assert.False(result.Ok);
        Assert.True(result.Failed, result.Detail);
        Assert.Contains("effect chaining failed", result.Detail);
        AssertSame(before, Capture(v1Data));
        Assert.Equal(HotSwapResult.Failed, _reporter.Events[^1].Result);
    }

    [Fact]
    public async Task ARegistryThatWentAway_DoesNotBlockRemovals_ButStillRejectsAdditions()
    {
        Install("HotScripts", 1);
        ModuleHost host = NewHost();
        Assert.True((await host.LoadAsync("HotScripts")).Ok);
        _instancesGone = true; // e.g. the instance feature was detached

        Install("HotScripts", 2);
        ModuleResult replace = await host.ReloadAsync("HotScripts"); // version 2 still adds an instance script
        Assert.False(replace.Ok);
        Assert.False(replace.Failed); // refused at validation, nothing changed
        Assert.Contains("instance scripts are not available", replace.Detail);
        Assert.NotNull(_spells.Find(SpellId));

        ModuleResult unload = await host.UnloadAsync("HotScripts");

        Assert.True(unload.Ok, unload.Detail);
        Assert.Empty(host.List());
        Assert.Null(_spells.Find(SpellId));
        Assert.Null(_eventAi.FindEvent(EventType));
        Assert.Null(_eventAi.FindAction(ActionType));
        Assert.True(_instances.HasScript(MapId)); // the vanished registry was not touched: its owner drops what it holds
    }

    private static async Task<bool> WaitForRetiredCount(ModuleHost host, int expected)
    {
        for (int i = 0; i < 80; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (host.RetiredStillReferenced == expected)
            {
                return true;
            }

            await Task.Delay(25);
        }

        return false;
    }
}

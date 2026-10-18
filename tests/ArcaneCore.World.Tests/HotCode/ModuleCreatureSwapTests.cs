using System.Runtime.CompilerServices;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Scripting;
using ArcaneCore.Game.Tests;
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
/// A hot creature AI module against a real <see cref="CreatureMapSystem"/> with real creatures: who switches at once, who waits for combat to
/// end, that a creature which already switched is never rebuilt by the retry, and that nothing keeps an unloaded module alive.
/// </summary>
public sealed class ModuleCreatureSwapTests : IDisposable
{
    private const uint Entry = 990101;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "arcane-modules-creat-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly ThreadedWorld _threads = new();
    private readonly OpcodeTable _opcodes = new();
    private readonly CommandTableSource _commands;
    private readonly CreatureAiFactory _factory = new();
    private readonly ScriptHookRegistry _hooks = new();
    private readonly ServiceProvider _services;
    private readonly WorldRuntime _runtime;
    private readonly Map _map;
    private readonly CreatureMapSystem _system;
    private readonly CreatureTemplate _template = new() { Entry = Entry, Name = "Hot creature", MinLevelHealth = 50, MaxLevelHealth = 50 };

    public ModuleCreatureSwapTests()
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
        _map = _runtime.GetMap(0);
        _system = new CreatureMapSystem(
            _map, new CreatureContent([_template], [], [], [], []), random: new Random(1), aiServices: new CreatureAiServices { Factory = _factory });
        _map.AddUpdater(_system);
    }

    public void Dispose()
    {
        _threads.Dispose();
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

    private ModuleHost NewHost(Func<Task>? waitBetweenAttempts = null)
        => new(
            new HotModuleOptions { Enabled = true, Directory = _root },
            _threads, _opcodes, _commands,
            new HotCodeAudit(Path.Combine(_dir, "audit.log")),
            NullLogger.Instance,
            TimeSpan.FromSeconds(10),
            reporter: new RecordingReporter(),
            seams: new ModuleGameSeams(() => _factory, () => _hooks, () => _runtime.Maps, _threads.Post, NullLogger.Instance, TimeSpan.FromMilliseconds(50), waitBetweenAttempts));

    private void Install()
    {
        Directory.CreateDirectory(Path.Combine(_root, "HotCreatures"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "hotfixtures", "creatures", "HotCreatures.dll"), Path.Combine(_root, "HotCreatures", "HotCreatures.dll"), overwrite: true);
    }

    private Creature Spawn(float x) => _system.SpawnTemporary(_template, x, 0, 83.5f, 0);

    // NoInlining: a local of the module's AI type in a test frame would keep the module alive and fake a leak.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string AiName(Creature creature) => creature.AI!.GetType().Name;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int AiHash(Creature creature) => RuntimeHelpers.GetHashCode(creature.AI!);

    private static async Task<bool> WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 200; i++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return false;
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
    public async Task ACreatureOutOfCombat_SwitchesToTheModuleAi_AtOnce_AndBackWhenTheModuleLeaves()
    {
        Install();
        Creature creature = Spawn(1);
        Assert.NotEqual("ModuleCreatureAI", AiName(creature));
        ModuleHost host = NewHost();

        ModuleResult load = await host.LoadAsync("HotCreatures");

        Assert.True(load.Ok, load.Detail);
        Assert.Equal("ModuleCreatureAI", AiName(creature));
        Assert.Contains(load.Fields!, f => f.Name == "creatures_switched" && f.Value == "1");

        ModuleResult unload = await host.UnloadAsync("HotCreatures");

        Assert.True(unload.Ok, unload.Detail);
        Assert.NotEqual("ModuleCreatureAI", AiName(creature));
    }

    [Fact]
    public async Task ACreatureInCombat_IsDeferred_ThenSwitchesOnceCombatEnds()
    {
        Install();
        Creature fighting = Spawn(1);
        _map.Combat.SetInCombatState(fighting, 0);
        Assert.True(fighting.Combat.IsInCombat);
        ModuleHost host = NewHost();

        ModuleResult load = await host.LoadAsync("HotCreatures");

        Assert.True(load.Ok, load.Detail);
        Assert.Contains(load.Fields!, f => f.Name == "creatures_in_combat_deferred" && f.Value == "1");
        Assert.NotEqual("ModuleCreatureAI", AiName(fighting));

        MapCombat.ClearInCombat(fighting);

        Assert.True(await WaitFor(() => AiName(fighting) == "ModuleCreatureAI"), "the retry must switch the creature once its fight is over");
    }

    [Fact]
    public async Task TheRetrySweep_NeverRebuilds_ACreatureThatAlreadySwitched()
    {
        Install();
        Creature calm = Spawn(1);
        Creature fighting = Spawn(2);
        _map.Combat.SetInCombatState(fighting, 0);
        ModuleHost host = NewHost();

        ModuleResult load = await host.LoadAsync("HotCreatures");

        Assert.True(load.Ok, load.Detail);
        Assert.Equal("ModuleCreatureAI", AiName(calm));
        int before = AiHash(calm);

        // Let several retry rounds run (50 ms apart) while the other creature is still fighting, then let it finish.
        await Task.Delay(400);
        MapCombat.ClearInCombat(fighting);
        Assert.True(await WaitFor(() => AiName(fighting) == "ModuleCreatureAI"));
        await Task.Delay(150);

        Assert.Equal(before, AiHash(calm)); // same AI object: its escort / phase / timer state was not wiped
    }

    [Fact]
    public async Task AfterUnloadWithNoCombat_NothingKeepsTheModuleAlive()
    {
        Install();
        Creature creature = Spawn(1);
        ModuleHost host = NewHost();
        Assert.True((await host.LoadAsync("HotCreatures")).Ok);
        Assert.Equal("ModuleCreatureAI", AiName(creature));

        ModuleResult unload = await host.UnloadAsync("HotCreatures");

        Assert.True(unload.Ok, unload.Detail);
        Assert.True(await WaitForRetiredCount(host, 0), "the creature's AI object came from the module; once it switched, nothing may hold the module");
    }

    [Fact]
    public async Task ACreatureStillInCombatAtUnload_KeepsTheModuleAlive_UntilItSwitches()
    {
        Install();
        Creature creature = Spawn(1);
        using var retryGate = new SemaphoreSlim(0);
        ModuleHost host = NewHost(() => retryGate.WaitAsync());
        Assert.True((await host.LoadAsync("HotCreatures")).Ok);
        _map.Combat.SetInCombatState(creature, 0);

        ModuleResult unload = await host.UnloadAsync("HotCreatures");

        Assert.True(unload.Ok, unload.Detail);
        Assert.Equal("ModuleCreatureAI", AiName(creature));
        AllocationProbe.CollectBetweenWindows();
        Assert.Equal(1, host.RetiredStillReferenced);

        // The combat state is world-thread data: clear it there, then let the (gated) retry run exactly once more.
        var cleared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _threads.Post(() =>
        {
            MapCombat.ClearInCombat(creature);
            cleared.SetResult();
        });
        await cleared.Task.WaitAsync(TimeSpan.FromSeconds(30));
        retryGate.Release(int.MaxValue / 2);

        Assert.True(await WaitFor(() => AiName(creature) != "ModuleCreatureAI"));
        Assert.True(await WaitForRetiredCount(host, 0));
    }
}

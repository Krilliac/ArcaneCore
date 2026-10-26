using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Scripting;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.HotCode;
using ArcaneCore.World.HotCode.Modules;
using ArcaneCore.World.Ops.Announce;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.HotCode;

/// <summary>
/// Module lane beyond opcodes and commands: creature AI and script hooks swapped through the real <see cref="CreatureAiFactory"/> and
/// <see cref="ScriptHookRegistry"/>, the documented template module, and the hot swap reports every decision produces.
/// </summary>
public sealed class ModuleLaneExtensionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "arcane-modules-ext-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly ThreadedWorld _world = new();
    private readonly OpcodeTable _opcodes = new();
    private readonly CommandTableSource _commands;
    private readonly CreatureAiFactory _ai = new();
    private readonly ScriptHookRegistry _hooks = new();
    private readonly RecordingReporter _reporter = new();

    public ModuleLaneExtensionTests()
    {
        _root = Path.Combine(_dir, "modules");
        Directory.CreateDirectory(_root);
        new BaseOpcodeGroup().Register(_opcodes);
        _commands = new CommandTableSource(new CommandTable(new BaseCommandGroup().Commands));
    }

    public void Dispose()
    {
        _world.Dispose();
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

    private ModuleHost NewHost(bool withSeams)
        => new(
            new HotModuleOptions { Enabled = true, Directory = _root },
            _world, _opcodes, _commands,
            new HotCodeAudit(Path.Combine(_dir, "audit.log")),
            NullLogger.Instance,
            TimeSpan.FromSeconds(10),
            reporter: _reporter,
            seams: withSeams ? new ModuleGameSeams(() => _ai, () => _hooks, () => [], _world.Post, NullLogger.Instance) : null);

    private void Install(string fixtureFolder, string dllName, string moduleName)
    {
        Directory.CreateDirectory(Path.Combine(_root, moduleName));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "hotfixtures", fixtureFolder, dllName + ".dll"), Path.Combine(_root, moduleName, moduleName + ".dll"), overwrite: true);
    }

    private async Task<bool> WaitForRetiredCount(ModuleHost host, int expected)
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
    public async Task TheTemplateModule_LoadsRegistersItsCommand_AndUnloadsToNothing()
    {
        Install("sample", "GmCommands", "GmCommands");
        ModuleHost host = NewHost(withSeams: false);

        ModuleResult load = await host.LoadAsync("GmCommands");

        Assert.True(load.Ok, load.Detail);
        Assert.Contains(".modhello", Assert.Single(host.List()).CommandRoots.Select(r => "." + r));
        Assert.Equal(AccountSecurity.GameMaster, _commands.Current.Resolve("modhello", AccountSecurity.Administrator)?.Security);

        ModuleResult unload = await host.UnloadAsync("GmCommands");

        Assert.True(unload.Ok, unload.Detail);
        Assert.Null(_commands.Current.Resolve("modhello", AccountSecurity.Administrator));
        Assert.True(await WaitForRetiredCount(host, 0), "the unloaded template module must be collected");
    }

    [Fact]
    public async Task CreatureAiAndScriptHooks_GoLive_ReplaceAtomically_AndLeaveCleanlyOnUnload()
    {
        Install("game1", "HotGame", "HotGame");
        ModuleHost host = NewHost(withSeams: true);
        int hookObjectsBefore = _hooks.Registered.Count;

        ModuleResult load = await host.LoadAsync("HotGame");

        Assert.True(load.Ok, load.Detail);
        Assert.True(_ai.HasScript(990001, "HotGameAI"));
        Assert.False(_ai.HasScript(990002, string.Empty));
        Assert.True(_hooks.Player.HasLogin);
        Assert.Equal(hookObjectsBefore + 1, _hooks.Registered.Count);
        ModuleInfo info = Assert.Single(host.List());
        Assert.Equal(2, info.CreatureAi);
        Assert.Equal(1, info.ScriptHooks);

        Install("game2", "HotGame", "HotGame");
        ModuleResult replace = await host.ReloadAsync("HotGame");

        Assert.True(replace.Ok, replace.Detail);
        Assert.True(_ai.HasScript(990001, "HotGameAI"));
        Assert.True(_ai.HasScript(990002, string.Empty));
        Assert.Equal(hookObjectsBefore + 1, _hooks.Registered.Count); // the old hook object left, the new one arrived

        ModuleResult unload = await host.UnloadAsync("HotGame");

        Assert.True(unload.Ok, unload.Detail);
        Assert.False(_ai.HasScript(990001, "HotGameAI"));
        Assert.False(_ai.HasScript(990002, string.Empty));
        Assert.False(_hooks.Player.HasLogin);
        Assert.Equal(hookObjectsBefore, _hooks.Registered.Count);
        Assert.True(await WaitForRetiredCount(host, 0), "after unload nothing may keep the module: the AI factory and the hook arrays must have let go");
    }

    [Fact]
    public async Task AnAiEntryTheServerAlreadyHas_RejectsTheWholeModule_AndChangesNothing()
    {
        Install("game1", "HotGame", "HotGame");
        _ai.RegisterEntryScript(990001, _ => throw new NotSupportedException());
        ModuleHost host = NewHost(withSeams: true);
        int hooksBefore = _hooks.Registered.Count;

        ModuleResult result = await host.LoadAsync("HotGame");

        Assert.False(result.Ok);
        Assert.Contains("990001", result.Detail);
        Assert.Empty(host.List());
        Assert.False(_ai.HasScript(0, "HotGameAI"));
        Assert.False(_hooks.Player.HasLogin);
        Assert.Equal(hooksBefore, _hooks.Registered.Count);
        Assert.True(await WaitForRetiredCount(host, 0));
    }

    [Fact]
    public async Task AModuleWithGameContent_IsRefusedByAServerWithoutTheSeams()
    {
        Install("game1", "HotGame", "HotGame");
        ModuleHost host = NewHost(withSeams: false);

        ModuleResult result = await host.LoadAsync("HotGame");

        Assert.False(result.Ok);
        Assert.Contains("does not offer creature AI or script hooks", result.Detail);
        Assert.Empty(host.List());
    }

    [Fact]
    public async Task EveryDecision_IsReported_WithTheActorTheReasonAndTheModuleFacts()
    {
        Install("v1", "HotFixture", "HotFixture");
        ModuleHost host = NewHost(withSeams: false);
        var gm = new HotSwapOrigin("acct (Char)", "testing");

        await host.LoadAsync("HotFixture", gm);
        await host.LoadAsync("HotFixture", gm); // already loaded: rejected
        Install("v2", "HotFixture", "HotFixture");
        await host.ReloadAsync("HotFixture", gm);
        await host.UnloadAsync("HotFixture");

        IReadOnlyList<HotSwapEvent> events = _reporter.Events;
        Assert.Equal(4, events.Count);
        Assert.All(events, e => Assert.Equal(HotSwapLane.Module, e.Lane));
        Assert.Equal(["load", "load", "replace", "unload"], events.Select(e => e.Action));
        Assert.Equal([HotSwapResult.Applied, HotSwapResult.Rejected, HotSwapResult.Applied, HotSwapResult.Applied], events.Select(e => e.Result));
        Assert.Equal("acct (Char)", events[0].Origin.Actor);
        Assert.Equal("testing", events[0].Origin.Reason);
        Assert.Contains("already loaded", events[1].Detail);
        Assert.Equal("console", events[3].Origin.Actor);
        Assert.Contains(events[2].Fields!, f => f.Name == "replaced_sha256");
        Assert.Contains(events[0].Fields!, f => f.Name == "sha256" && f.Value.Length == 12);
        Assert.All(events, e => Assert.Equal("HotFixture", e.Area));
    }

    [Fact]
    public async Task ACommitThatFailsAfterValidation_IsReportedFailed_AuditedModuleFailed_AndRolledBack()
    {
        Install("game1", "HotGame", "HotGame");
        int aiCalls = 0;
        // Validate asks for the factory first and Commit second: the second answer is gone, as if the creature AI system vanished in between.
        var seams = new ModuleGameSeams(() => ++aiCalls == 1 ? _ai : null, () => _hooks, () => [], _world.Post, NullLogger.Instance);
        var host = new ModuleHost(
            new HotModuleOptions { Enabled = true, Directory = _root },
            _world, _opcodes, _commands,
            new HotCodeAudit(Path.Combine(_dir, "audit.log")),
            NullLogger.Instance,
            TimeSpan.FromSeconds(10),
            reporter: _reporter,
            seams: seams);

        ModuleResult result = await host.LoadAsync("HotGame");

        Assert.False(result.Ok);
        Assert.True(result.Failed);
        Assert.Contains("rolled back", result.Detail);
        Assert.Equal(2, aiCalls);
        HotSwapEvent evt = Assert.Single(_reporter.Events);
        Assert.Equal(HotSwapResult.Failed, evt.Result);
        Assert.Equal("load", evt.Action);
        HotCodeAudit.FlushAll(); // the audit lines are written by a background appender
        string audit = await File.ReadAllTextAsync(Path.Combine(_dir, "audit.log"));
        Assert.Contains("module-failed", audit);
        Assert.DoesNotContain("module-load", audit);
        Assert.DoesNotContain("module-rejected", audit);
        // Rolled back: the module is not loaded, nothing of it is registered, and it can be collected.
        Assert.Empty(host.List());
        Assert.False(_ai.HasScript(990001, "HotGameAI"));
        Assert.False(_hooks.Player.HasLogin);
        Assert.Empty(_hooks.Registered);
        Assert.True(await WaitForRetiredCount(host, 0), "a rolled-back module must not stay referenced");
    }
}

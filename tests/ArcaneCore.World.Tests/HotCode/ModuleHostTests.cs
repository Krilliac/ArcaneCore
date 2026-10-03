using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.HotCode;
using ArcaneCore.World.HotCode.Modules;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.HotCode;

/// <summary>
/// The module lane: real assemblies (tests/hotmodule-fixtures, built from the same source as version 1
/// and version 2) are loaded into collectible load contexts, swapped into the live tables, replaced
/// and unloaded; a bad module changes nothing; an unloaded module is actually collected.
/// </summary>
public sealed class ModuleHostTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "arcane-modules-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly ThreadedWorld _world = new();
    private readonly OpcodeTable _opcodes = new();
    private readonly CommandTableSource _commands;
    private readonly ModuleHost _host;
    private OpcodeHandler? _held;

    public ModuleHostTests()
    {
        _root = Path.Combine(_dir, "modules");
        Directory.CreateDirectory(_root);
        new BaseOpcodeGroup().Register(_opcodes);
        _commands = new CommandTableSource(new CommandTable(new BaseCommandGroup().Commands));
        _host = NewHost(_world, TimeSpan.FromSeconds(10));
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

    private ModuleHost NewHost(IHotCodeWorld world, TimeSpan timeout, string allowlist = "")
        => new(new HotModuleOptions { Enabled = true, Directory = _root, Allowlist = allowlist }, world, _opcodes, _commands,
            new HotCodeAudit(Path.Combine(_dir, "audit.log")), NullLogger.Instance, timeout);

    private static string FixtureDll(int version) => Path.Combine(AppContext.BaseDirectory, "hotfixtures", "v" + version, "HotFixture.dll");

    private string Install(int version, string name = "HotFixture")
    {
        Directory.CreateDirectory(Path.Combine(_root, name));
        string target = Path.Combine(_root, name, name + ".dll");
        File.Copy(FixtureDll(version), target, overwrite: true);
        return target;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int VersionOf(OpcodeTable table, WorldOpcode opcode)
    {
        Assert.True(table.TryGet(opcode, out OpcodeHandler handler));
        Type marker = handler.Session!.Method.DeclaringType!.Assembly.GetType("HotFixture.Marker")!;
        return (int)marker.GetField("Version")!.GetRawConstantValue()!;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsInCollectibleContext(OpcodeTable table, WorldOpcode opcode)
    {
        Assert.True(table.TryGet(opcode, out OpcodeHandler handler));
        AssemblyLoadContext? context = AssemblyLoadContext.GetLoadContext(handler.Session!.Method.DeclaringType!.Assembly);
        return context is { IsCollectible: true } && context != AssemblyLoadContext.Default;
    }

    private string? Help(string root) => _commands.Current.Resolve(root, AccountSecurity.Player)?.Help;

    private async Task<bool> WaitForRetiredCount(int expected)
    {
        for (int i = 0; i < 80; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (_host.RetiredStillReferenced == expected)
            {
                return true;
            }

            await Task.Delay(25);
        }

        return false;
    }

    [Fact]
    public async Task ALoadedModule_ServesItsHandlersAndCommands_FromACollectibleContext()
    {
        Install(1);
        _opcodes.TryGet(WorldOpcode.CmsgPing, out OpcodeHandler basePing);

        ModuleResult result = await _host.LoadAsync("HotFixture");

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(1, VersionOf(_opcodes, WorldOpcode.CmsgQueryTime));
        Assert.True(IsInCollectibleContext(_opcodes, WorldOpcode.CmsgQueryTime));
        Assert.Equal("fixture v1", Help("hotfixture"));
        Assert.True(_opcodes.TryGet(WorldOpcode.CmsgPing, out OpcodeHandler pingAfter));
        Assert.Same(basePing, pingAfter); // the module's internal HiddenGroup is not an extension point
        Assert.Equal("base", Help("hotbase"));
        ModuleInfo info = Assert.Single(_host.List());
        Assert.Equal("HotFixture", info.Name);
        Assert.Equal(1, info.Opcodes);
    }

    [Fact]
    public async Task Unload_RemovesWhatTheModuleAdded_AndKeepsEverythingElse()
    {
        Install(1);
        await _host.LoadAsync("HotFixture");

        ModuleResult result = await _host.UnloadAsync("HotFixture");

        Assert.True(result.Ok, result.Detail);
        Assert.False(_opcodes.TryGet(WorldOpcode.CmsgQueryTime, out _));
        Assert.True(_opcodes.TryGet(WorldOpcode.CmsgPing, out _));
        Assert.Null(Help("hotfixture"));
        Assert.Equal("base", Help("hotbase"));
        Assert.Empty(_host.List());
    }

    [Fact]
    public async Task AnUnloadedModule_IsActuallyCollected_AndAHeldHandlerShowsWhenItIsNot()
    {
        Install(1);
        await _host.LoadAsync("HotFixture");
        _held = Grab(_opcodes); // something outside the host keeps a module delegate (a leak)

        await _host.UnloadAsync("HotFixture");

        Assert.False(await WaitForRetiredCount(0), "a held module delegate must keep the context alive, and the host must say so");
        Assert.Equal(1, _host.RetiredStillReferenced);

        _held = null;
        Assert.True(await WaitForRetiredCount(0), "once nothing references the module the runtime must free its load context");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static OpcodeHandler Grab(OpcodeTable table)
    {
        Assert.True(table.TryGet(WorldOpcode.CmsgQueryTime, out OpcodeHandler handler));
        return handler;
    }

    private static string Sha(string dll) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(dll)));

    [Fact]
    public async Task AModuleWhoseHashIsNotOnTheAllowlist_IsRefused_AuditedAndChangesNothing()
    {
        string dll = Install(1);
        string list = Path.Combine(_dir, "allow.txt");
        File.WriteAllText(list, Sha(FixtureDll(2)) + Environment.NewLine); // only version 2 is approved
        ModuleHost host = NewHost(_world, TimeSpan.FromSeconds(10), list);

        ModuleResult result = await host.LoadAsync("HotFixture");

        Assert.False(result.Ok);
        Assert.Contains("allowlist", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Sha(dll), result.Detail);
        Assert.Empty(host.List());
        Assert.False(_opcodes.TryGet(WorldOpcode.CmsgQueryTime, out _));
        Assert.Null(Help("hotfixture"));
        string audit = await File.ReadAllTextAsync(Path.Combine(_dir, "audit.log"));
        Assert.Contains("module-rejected", audit);
        Assert.Contains(Sha(dll), audit);
    }

    [Fact]
    public async Task AModuleWhoseHashIsListed_Loads_AndAReloadToAnUnlistedBuildKeepsTheRunningVersion()
    {
        Install(1);
        string list = Path.Combine(_dir, "allow.txt");
        File.WriteAllText(list, "# approved" + Environment.NewLine + Sha(FixtureDll(1)) + "  HotFixture v1" + Environment.NewLine);
        ModuleHost host = NewHost(_world, TimeSpan.FromSeconds(10), list);
        Assert.True((await host.LoadAsync("HotFixture")).Ok);

        Install(2); // a different build is dropped in; it is not approved
        ModuleResult reload = await host.ReloadAsync("HotFixture");

        Assert.False(reload.Ok);
        Assert.Equal(1, VersionOf(_opcodes, WorldOpcode.CmsgQueryTime));
        Assert.Equal("fixture v1", Help("hotfixture"));

        File.AppendAllText(list, Sha(FixtureDll(2)) + Environment.NewLine); // approving it needs no restart
        Assert.True((await host.ReloadAsync("HotFixture")).Ok);
        Assert.Equal("fixture v2", Help("hotfixture"));
    }

    [Fact]
    public async Task Reload_SwapsInTheNewVersion_AndTheOldOneIsCollected()
    {
        Install(1);
        await _host.LoadAsync("HotFixture");
        Install(2); // the file is replaced while version 1 is loaded: modules load from bytes, nothing is locked

        ModuleResult result = await _host.ReloadAsync("HotFixture");

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(2, VersionOf(_opcodes, WorldOpcode.CmsgQueryTime));
        Assert.Equal(2, VersionOf(_opcodes, WorldOpcode.CmsgTutorialFlag));
        Assert.Equal("fixture v2", Help("hotfixture"));
        Assert.Equal("fixture v2 extra", Help("hotfixtureextra"));
        Assert.Equal(2, Assert.Single(_host.List()).Opcodes);
        Assert.True(await WaitForRetiredCount(0), "the replaced version must be collected");
    }

    [Fact]
    public async Task ReloadingToAVersionWithFewerHandlers_RemovesTheOnesItNoLongerHas()
    {
        Install(2);
        await _host.LoadAsync("HotFixture");
        Install(1);

        ModuleResult result = await _host.ReloadAsync("HotFixture");

        Assert.True(result.Ok, result.Detail);
        Assert.False(_opcodes.TryGet(WorldOpcode.CmsgTutorialFlag, out _));
        Assert.Null(Help("hotfixtureextra"));
        Assert.Equal("fixture v1", Help("hotfixture"));
    }

    [Fact]
    public async Task ABadReload_LeavesTheRunningVersionInForce()
    {
        Install(1);
        await _host.LoadAsync("HotFixture");
        File.WriteAllText(Path.Combine(_root, "HotFixture", "HotFixture.dll"), "this is not an assembly");

        ModuleResult result = await _host.ReloadAsync("HotFixture");

        Assert.False(result.Ok);
        Assert.Equal(1, VersionOf(_opcodes, WorldOpcode.CmsgQueryTime));
        Assert.Equal("fixture v1", Help("hotfixture"));
        Assert.Single(_host.List());
        Assert.True(await WaitForRetiredCount(0), "the rejected candidate must not stay loaded");
    }

    [Fact]
    public async Task AReloadWhoseNewVersionClashesWithAHandlerItDoesNotOwn_ChangesNothing()
    {
        Install(1);
        await _host.LoadAsync("HotFixture");
        Install(2);

        // Another owner takes CmsgTutorialFlag, which version 2 also wants.
        var other = new OpcodeTable();
        other.OnSession(WorldOpcode.CmsgTutorialFlag, SessionStates.Authenticated, (_, _) => Task.CompletedTask);
        _opcodes.Replace(_opcodes.WithNewHandlersFrom(other, out _));
        _opcodes.TryGet(WorldOpcode.CmsgTutorialFlag, out OpcodeHandler theirs);

        ModuleResult result = await _host.ReloadAsync("HotFixture");

        Assert.False(result.Ok);
        Assert.Contains("does not own", result.Detail);
        Assert.Equal(1, VersionOf(_opcodes, WorldOpcode.CmsgQueryTime));
        Assert.Equal("fixture v1", Help("hotfixture"));
        Assert.Null(Help("hotfixtureextra"));
        Assert.True(_opcodes.TryGet(WorldOpcode.CmsgTutorialFlag, out OpcodeHandler still));
        Assert.Same(theirs, still);
    }

    [Fact]
    public async Task ALoadThatClashesWithABuiltInHandler_IsRejected_AndNothingIsAdded()
    {
        var builtin = new OpcodeTable();
        builtin.OnSession(WorldOpcode.CmsgQueryTime, SessionStates.Authenticated, (_, _) => Task.CompletedTask);
        _opcodes.Replace(_opcodes.WithNewHandlersFrom(builtin, out _));
        _opcodes.TryGet(WorldOpcode.CmsgQueryTime, out OpcodeHandler theirs);
        Install(1);

        ModuleResult result = await _host.LoadAsync("HotFixture");

        Assert.False(result.Ok);
        Assert.Null(Help("hotfixture")); // the command half of the module was not applied either
        Assert.Empty(_host.List());
        Assert.True(_opcodes.TryGet(WorldOpcode.CmsgQueryTime, out OpcodeHandler still));
        Assert.Same(theirs, still);
        Assert.True(await WaitForRetiredCount(0));
    }

    [Fact]
    public async Task ACommandRootThatShadowsAnExistingOne_IsRejected()
    {
        Install(1);
        // "hotfixture" would be a proper prefix of this existing root.
        _commands.TryAdd([new ChatCommand("hotfixturelong", AccountSecurity.Player, "x", (_, _) => true)]);

        ModuleResult result = await _host.LoadAsync("HotFixture");

        Assert.False(result.Ok);
        Assert.Contains("prefix", result.Detail);
        Assert.False(_opcodes.TryGet(WorldOpcode.CmsgQueryTime, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("..\\evil")]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("C:\\temp\\x")]
    [InlineData("trailing.")]
    [InlineData("with space")]
    [InlineData("1startsWithDigit")]
    public async Task ANameThatIsNotAPlainModuleName_IsRefusedBeforeAnyFileIsTouched(string name)
    {
        ModuleResult result = await _host.LoadAsync(name);

        Assert.False(result.Ok);
        Assert.Contains("module name", result.Detail);
    }

    [Fact]
    public async Task AModuleThatIsNotThere_IsRejected()
    {
        ModuleResult missingFolder = await _host.LoadAsync("Nothing");
        Directory.CreateDirectory(Path.Combine(_root, "Hollow"));
        ModuleResult missingFile = await _host.LoadAsync("Hollow");

        Assert.False(missingFolder.Ok);
        Assert.Contains("not found", missingFolder.Detail);
        Assert.False(missingFile.Ok);
        Assert.Contains("not found", missingFile.Detail);
    }

    [Fact]
    public async Task AnAssemblyThatContributesNothing_IsRejected()
    {
        // A real managed assembly (the server's own protocol library) that has no handler or command group.
        string name = "ArcaneCore.Protocol";
        Directory.CreateDirectory(Path.Combine(_root, name));
        File.Copy(Path.Combine(AppContext.BaseDirectory, name + ".dll"), Path.Combine(_root, name, name + ".dll"));

        ModuleResult result = await _host.LoadAsync(name);

        Assert.False(result.Ok);
        Assert.Contains("registers anything", result.Detail);
        Assert.True(await WaitForRetiredCount(0));
    }

    [Fact]
    public async Task AnAssemblyWhoseNameIsNotTheModuleName_IsRejected()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Mislabelled"));
        File.Copy(FixtureDll(1), Path.Combine(_root, "Mislabelled", "Mislabelled.dll"));

        ModuleResult result = await _host.LoadAsync("Mislabelled");

        Assert.False(result.Ok);
        Assert.Contains("is named 'HotFixture'", result.Detail);
        Assert.False(_opcodes.TryGet(WorldOpcode.CmsgQueryTime, out _));
    }

    [Fact]
    public async Task AModuleFolderThatIsALink_IsRefused()
    {
        string real = Path.Combine(_dir, "elsewhere", "HotFixture");
        Directory.CreateDirectory(real);
        File.Copy(FixtureDll(1), Path.Combine(real, "HotFixture.dll"));
        string link = Path.Combine(_root, "HotFixture");
        MakeDirectoryLink(link, real);
        try
        {
            ModuleResult result = await _host.LoadAsync("HotFixture");

            Assert.False(result.Ok);
            Assert.Contains("link", result.Detail);
            Assert.False(_opcodes.TryGet(WorldOpcode.CmsgQueryTime, out _));
        }
        finally
        {
            Directory.Delete(link, recursive: false); // the link itself, never the target
        }
    }

    private static void MakeDirectoryLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            // A junction needs no privilege (a symbolic link does); the runtime reports it through LinkTarget too.
            using Process process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, "could not create the junction for the test");
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }
    }

    [Fact]
    public async Task LoadingTwice_AndUnloadingWhatIsNotLoaded_AreRefused()
    {
        Install(1);
        await _host.LoadAsync("HotFixture");

        ModuleResult again = await _host.LoadAsync("HotFixture");
        ModuleResult unknown = await _host.UnloadAsync("Other");
        ModuleResult reloadUnknown = await _host.ReloadAsync("Other");

        Assert.False(again.Ok);
        Assert.Contains("already loaded", again.Detail);
        Assert.False(unknown.Ok);
        Assert.False(reloadUnknown.Ok);
        Assert.Single(_host.List());
    }

    [Fact]
    public async Task ACommitTheWorldThreadNeverReaches_AppliesNothing_EvenWhenItRunsLater()
    {
        var held = new HeldWorld();
        ModuleHost host = NewHost(held, TimeSpan.FromMilliseconds(150));
        Install(1);

        ModuleResult result = await host.LoadAsync("HotFixture");

        Assert.False(result.Ok);
        Assert.Contains("did not run the commit", result.Detail);
        held.RunHeld(); // the abandoned commit must not apply behind the caller's back
        Assert.False(_opcodes.TryGet(WorldOpcode.CmsgQueryTime, out _));
        Assert.Null(Help("hotfixture"));
        Assert.Empty(host.List());
    }

    [Fact]
    public async Task AReload_NeverLeavesAWindowWhereAHandlerBothVersionsHaveIsMissing()
    {
        Install(1);
        await _host.LoadAsync("HotFixture");
        int misses = 0;
        int reads = 0;
        using var stop = new CancellationTokenSource();
        Task reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                reads++;
                if (!_opcodes.TryGet(WorldOpcode.CmsgQueryTime, out _) || _commands.Current.Resolve("hotfixture", AccountSecurity.Player) is null)
                {
                    Interlocked.Increment(ref misses);
                }
            }
        });

        for (int i = 0; i < 12; i++)
        {
            Install(i % 2 == 0 ? 2 : 1);
            Assert.True((await _host.ReloadAsync("HotFixture")).Ok);
        }

        await stop.CancelAsync();
        await reader;

        Assert.True(reads > 0);
        Assert.Equal(0, misses);
    }

    [Fact]
    public async Task EveryDecision_IsAudited_WithTheModulesHash()
    {
        Install(1);
        await _host.LoadAsync("HotFixture");
        await _host.LoadAsync("Nothing");
        await _host.UnloadAsync("HotFixture");

        string[] lines = await File.ReadAllLinesAsync(Path.Combine(_dir, "audit.log"));

        Assert.Equal(3, lines.Length);
        Assert.Contains("\tmodule-load\t", lines[0]);
        Assert.Matches("sha256 [0-9A-F]{64}", lines[0]);
        Assert.Contains("\tmodule-rejected\t", lines[1]);
        Assert.Contains("\tmodule-unload\t", lines[2]);
    }

    [Fact]
    public void TheHotmodulePlumbing_IsAdministratorOnly_AndCannotChangeHowAnExistingCommandResolves()
    {
        ChatCommand root = HotModuleCommands.Create(_host, new HotCodeAudit(null));

        Assert.Equal(AccountSecurity.Administrator, root.Security);
        Assert.All(root.SubCommands, c => Assert.Equal(AccountSecurity.Administrator, c.Security));
        Assert.Equal(["list", "load", "reload", "unload"], root.SubCommands.Select(c => c.Name));
        Assert.Null(CommandTableSource.Validate(ChatCommands.CreateTable().Roots, [root]));
        Assert.Contains("No modules loaded", HotModuleCommands.Format(_host));
    }
}

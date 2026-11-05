using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.HotCode;
using ArcaneCore.World.HotCode.Modules;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.HotCode;

/// <summary>
/// The module host with a real module that has a private dependency (tests/hotmodule-fixtures/HotDepUse and HotDepSwallow, whose
/// public command group loads the dependency HotDepLib when it is constructed). With an allowlist, an unlisted dependency rejects the
/// module (also when the module code swallows the load error), the running version stays in force and the opcode and command tables
/// do not change; a listed dependency loads. The dependency dll is a tiny assembly written next to the module by each test.
/// </summary>
public sealed class ModuleHostDependencyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "arcane-moddephost-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly ThreadedWorld _world = new();
    private readonly OpcodeTable _opcodes = new();
    private readonly CommandTableSource _commands;

    public ModuleHostDependencyTests()
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

    private ModuleHost NewHost(string allowlist)
        => new(new HotModuleOptions { Enabled = true, Directory = _root, Allowlist = allowlist }, _world, _opcodes, _commands,
            new HotCodeAudit(Path.Combine(_dir, "audit.log")), NullLogger.Instance, TimeSpan.FromSeconds(10));

    private string InstallModule(string fixtureFolder, string name)
    {
        Directory.CreateDirectory(Path.Combine(_root, name));
        string target = Path.Combine(_root, name, name + ".dll");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "hotfixtures", fixtureFolder, name + ".dll"), target, overwrite: true);
        return target;
    }

    /// <summary>Writes the private dependency HotDepLib (one public type named <paramref name="typeName"/>, so two builds differ) into the module's folder.</summary>
    private static string WriteDependency(string moduleFolder, string typeName)
    {
        var builder = new PersistedAssemblyBuilder(new AssemblyName("HotDepLib"), typeof(object).Assembly);
        ModuleBuilder module = builder.DefineDynamicModule("HotDepLib");
        module.DefineType("HotDepLib." + typeName, TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed).CreateType();
        string path = Path.Combine(moduleFolder, "HotDepLib.dll");
        builder.Save(path);
        return path;
    }

    private string WriteAllowlist(params string[] dlls)
    {
        string list = Path.Combine(_dir, "allow.txt");
        File.WriteAllLines(list, dlls.Select(Sha));
        return list;
    }

    private static string Sha(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

    private string? Help(string root) => _commands.Current.Resolve(root, AccountSecurity.Player)?.Help;

    [Theory]
    [InlineData("depuse", "HotDepUse")]
    [InlineData("depswallow", "HotDepSwallow")]
    public async Task AnUnlistedDependency_RejectsTheModule_EvenWhenTheModuleSwallowsTheLoadError(string folder, string name)
    {
        string entry = InstallModule(folder, name);
        string dependency = WriteDependency(Path.Combine(_root, name), "Marker");
        ModuleHost host = NewHost(WriteAllowlist(entry)); // the entry dll is approved, the dependency is not
        int opcodes = _opcodes.Count;

        ModuleResult result = await host.LoadAsync(name);

        Assert.False(result.Ok);
        Assert.Contains("dependency 'HotDepLib.dll' refused", result.Detail, StringComparison.Ordinal);
        Assert.Contains(Sha(dependency), result.Detail, StringComparison.Ordinal);
        Assert.Empty(host.List());
        Assert.Null(Help("hotdep"));
        Assert.Equal(opcodes, _opcodes.Count);
    }

    [Fact]
    public async Task AListedDependency_Loads()
    {
        string entry = InstallModule("depuse", "HotDepUse");
        string dependency = WriteDependency(Path.Combine(_root, "HotDepUse"), "Marker");
        ModuleHost host = NewHost(WriteAllowlist(entry, dependency));

        ModuleResult result = await host.LoadAsync("HotDepUse");

        Assert.True(result.Ok, result.Detail);
        Assert.Single(host.List());
        Assert.Equal("hotdep module", Help("hotdep"));
    }

    [Fact]
    public async Task AReloadWhoseDependencyChangedToAnUnlistedBuild_KeepsTheRunningVersion()
    {
        string entry = InstallModule("depuse", "HotDepUse");
        string folder = Path.Combine(_root, "HotDepUse");
        string approved = WriteDependency(folder, "Marker");
        ModuleHost host = NewHost(WriteAllowlist(entry, approved));
        Assert.True((await host.LoadAsync("HotDepUse")).Ok);
        string approvedHash = Sha(approved); // the same path is overwritten below

        string replaced = WriteDependency(folder, "Tampered"); // a swapped private dependency, not on the allowlist
        Assert.NotEqual(approvedHash, Sha(replaced));
        ModuleResult reload = await host.ReloadAsync("HotDepUse");

        Assert.False(reload.Ok);
        Assert.Contains("dependency 'HotDepLib.dll' refused", reload.Detail, StringComparison.Ordinal);
        Assert.Single(host.List());
        Assert.Equal("hotdep module", Help("hotdep"));
    }
}

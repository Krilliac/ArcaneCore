using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using ArcaneCore.World.HotCode.Modules;
using Xunit;

namespace ArcaneCore.World.Tests.HotCode;

/// <summary>
/// The module allowlist covers the private dependency dlls a module loads from its folder, not only the entry dll:
/// with an allowlist configured an unlisted dependency is never loaded and the load context records why; a listed one
/// loads; without an allowlist nothing is checked. The dependency is a tiny real assembly written to disk here.
/// </summary>
public sealed class ModuleDependencyAllowlistTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "arcane-moddep-" + Guid.NewGuid().ToString("N"));

    public ModuleDependencyAllowlistTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // a still-mapped file must not fail the test
        }
    }

    private string WriteDependency()
    {
        var builder = new PersistedAssemblyBuilder(new AssemblyName("HotDep"), typeof(object).Assembly);
        ModuleBuilder module = builder.DefineDynamicModule("HotDep");
        module.DefineType("HotDep.Marker", TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed).CreateType();
        string path = Path.Combine(_dir, "HotDep.dll");
        builder.Save(path);
        return path;
    }

    private string WriteAllowlist(params string[] hashes)
    {
        string path = Path.Combine(_dir, "allow.txt");
        File.WriteAllLines(path, hashes);
        return path;
    }

    private static string Sha(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

    [Fact]
    public void AnUnlistedDependency_IsNotLoaded_AndTheContextRecordsTheRefusal()
    {
        string dependency = WriteDependency();
        string list = WriteAllowlist(new string('A', 64)); // some other hash is approved
        var context = new ModuleLoadContext("Entry", _dir, list);
        try
        {
            Exception? error = Record.Exception(() => context.LoadFromAssemblyName(new AssemblyName("HotDep")));

            Assert.NotNull(error);
            Assert.NotNull(context.Refusal);
            Assert.Contains("HotDep.dll", context.Refusal, StringComparison.Ordinal);
            Assert.Contains("allowlist", context.Refusal, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(Sha(dependency), context.Refusal, StringComparison.Ordinal);
            Assert.DoesNotContain(context.Assemblies, a => a.GetName().Name == "HotDep");
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact]
    public void AMissingAllowlistFile_RefusesTheDependency()
    {
        WriteDependency();
        var context = new ModuleLoadContext("Entry", _dir, Path.Combine(_dir, "absent.txt"));
        try
        {
            Assert.NotNull(Record.Exception(() => context.LoadFromAssemblyName(new AssemblyName("HotDep"))));
            Assert.NotNull(context.Refusal);
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact]
    public void AListedDependency_Loads()
    {
        string dependency = WriteDependency();
        string list = WriteAllowlist("# approved", Sha(dependency) + "  HotDep");
        var context = new ModuleLoadContext("Entry", _dir, list);
        try
        {
            Assembly loaded = context.LoadFromAssemblyName(new AssemblyName("HotDep"));

            Assert.Equal("HotDep", loaded.GetName().Name);
            Assert.Null(context.Refusal);
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact]
    public void WithoutAnAllowlist_TheDependencyLoadsUnchecked()
    {
        WriteDependency();
        var context = new ModuleLoadContext("Entry", _dir);
        try
        {
            Assembly loaded = context.LoadFromAssemblyName(new AssemblyName("HotDep"));

            Assert.Equal("HotDep", loaded.GetName().Name);
            Assert.Null(context.Refusal);
        }
        finally
        {
            context.Unload();
        }
    }
}

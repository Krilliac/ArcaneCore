using System.Reflection;
using ArcaneCore.Kernel.Reflection;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Reflection;

/// <summary>
/// <see cref="CodeAssemblies"/>: the set of assemblies discovery scans. Ordering is by assembly simple name then type full name
/// whatever order the assemblies were given in, an assembly can be in the set once, and a type that cannot load fails the scan.
/// </summary>
public sealed class CodeAssembliesTests
{
    private static readonly Assembly Kernel = typeof(CodeAssemblies).Assembly;      // "ArcaneCore.Kernel"
    private static readonly Assembly Tests = typeof(CodeAssembliesTests).Assembly;  // "ArcaneCore.Kernel.Tests"
    private static readonly Assembly CoreLib = typeof(object).Assembly;             // "System.Private.CoreLib"

    [Fact]
    public void Assemblies_AreOrderedBySimpleName_WhateverTheGivenOrder()
    {
        CodeAssemblies forward = CodeAssemblies.Of(Kernel, Tests);
        CodeAssemblies backward = CodeAssemblies.Of(Tests, Kernel);

        Assert.Equal(new[] { Kernel, Tests }, forward.Assemblies);
        Assert.Equal(new[] { Kernel, Tests }, backward.Assemblies);
        Assert.Equal(new[] { Tests, Kernel }, backward.BuiltIns);
    }

    [Fact]
    public void APack_IsOrderedAmongTheBuiltIns_ByName()
    {
        CodeAssemblies set = CodeAssemblies.Of(CoreLib, Kernel).WithPack(Tests);

        Assert.Equal(new[] { Kernel, Tests, CoreLib }, set.Assemblies);
        Assert.Equal(new[] { Tests }, set.Packs);
        Assert.True(set.Contains(Tests));
    }

    [Fact]
    public void GetLoadableTypes_IsOrderedByAssemblyThenFullName_AndCoversEveryAssembly()
    {
        IReadOnlyList<Type> types = CodeAssemblies.Of(Tests, Kernel).GetLoadableTypes();

        Assert.Contains(typeof(CodeAssemblies), types);
        Assert.Contains(typeof(CodeAssembliesTests), types);
        Assert.Equal(Kernel.GetTypes().Length + Tests.GetTypes().Length, types.Count);

        Type[] expected = [.. types
            .OrderBy(t => t.Assembly.GetName().Name, StringComparer.Ordinal)
            .ThenBy(t => t.FullName, StringComparer.Ordinal)];
        Assert.Equal(expected, types);
        // Every Kernel type comes before every test type (the order is not reflection's, nor the order given to Of).
        Assert.True(types.ToList().FindLastIndex(t => t.Assembly == Kernel) < types.ToList().FindIndex(t => t.Assembly == Tests));
    }

    [Fact]
    public void GetLoadableTypes_IsTheSameOnEveryCall()
    {
        CodeAssemblies set = CodeAssemblies.Of(Kernel).WithPack(Tests);
        Assert.Equal(set.GetLoadableTypes(), set.GetLoadableTypes());
    }

    [Fact]
    public void AnAssemblyGivenTwice_IsRefused()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() => CodeAssemblies.Of(Kernel, Kernel));
        Assert.Contains("ArcaneCore.Kernel", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APackAlreadyInTheSet_IsRefused_AndTheSetIsUnchanged()
    {
        CodeAssemblies builtIn = CodeAssemblies.Of(Kernel);
        CodeAssemblies withPack = builtIn.WithPack(Tests);

        Assert.Throws<ArgumentException>(() => withPack.WithPack(Tests));
        Assert.Throws<ArgumentException>(() => builtIn.WithPack(Kernel));
        Assert.Equal(new[] { Kernel }, builtIn.Assemblies);
        Assert.Equal(new[] { Kernel, Tests }, withPack.Assemblies);
    }

    [Fact]
    public void ASameNamedPack_IsRefused_UntilThePreviousOneIsRemoved()
    {
        CodeAssemblies set = CodeAssemblies.Of(Kernel).WithPack(new FakeAssembly("Pack.One", []));

        Assert.Throws<ArgumentException>(() => set.WithPack(new FakeAssembly("Pack.One", [])));

        var reloaded = new FakeAssembly("Pack.One", []);
        CodeAssemblies replaced = set.WithoutPack("Pack.One").WithPack(reloaded);
        Assert.Same(reloaded, Assert.Single(replaced.Packs));
    }

    [Fact]
    public void RemovingAnUnknownPackOrABuiltIn_IsRefused()
    {
        CodeAssemblies set = CodeAssemblies.Of(Kernel).WithPack(Tests);

        Assert.Throws<ArgumentException>(() => set.WithoutPack("No.Such.Pack"));
        Assert.Throws<ArgumentException>(() => set.WithoutPack("ArcaneCore.Kernel"));
    }

    [Fact]
    public void AnEmptyOrNullSet_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => CodeAssemblies.Of());
        Assert.Throws<ArgumentNullException>(() => CodeAssemblies.Of(Kernel, null!));
    }

    [Fact]
    public void ATypeThatCannotLoad_FailsTheScan_NamingTheAssemblyAndTheLoaderError()
    {
        var broken = new FakeAssembly("Pack.Broken", [typeof(string), null], new TypeLoadException("Could not load type 'Pack.Broken.Gone'"));
        CodeAssemblies set = CodeAssemblies.Of(Kernel).WithPack(broken);

        CodeAssemblyLoadException ex = Assert.Throws<CodeAssemblyLoadException>(() => set.GetLoadableTypes());

        Assert.Equal("Pack.Broken", ex.AssemblyName);
        Assert.Equal(new[] { "Could not load type 'Pack.Broken.Gone'" }, ex.LoaderErrors);
        Assert.IsType<ReflectionTypeLoadException>(ex.InnerException);
        Assert.Contains("Pack.Broken", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1 type(s)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("fail closed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATypeThatCannotLoad_IsNotDroppedSilently_EvenWhenTheRestLoaded()
    {
        // The fail-soft pattern (ex.Types.OfType<Type>()) would hand back typeof(string) here; the set refuses instead.
        var broken = new FakeAssembly("Pack.Partial", [typeof(string), null], new TypeLoadException("missing dependency"));
        Assert.Throws<CodeAssemblyLoadException>(() => CodeAssemblies.GetLoadableTypes(broken));
    }

    /// <summary>An assembly whose <see cref="Assembly.GetTypes"/> is controlled: either the given types or a type-load failure.</summary>
    private sealed class FakeAssembly(string name, Type?[] types, Exception? loaderError = null) : Assembly
    {
        public override string FullName => name + ", Version=0.0.0.0, Culture=neutral, PublicKeyToken=null";

        public override AssemblyName GetName() => new(name);

        public override AssemblyName GetName(bool copiedName) => new(name);

        public override Type[] GetTypes()
            => loaderError is null
                ? [.. types.OfType<Type>()]
                : throw new ReflectionTypeLoadException(types, [loaderError]);
    }
}

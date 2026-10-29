using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Reflection;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// The pack-aware handler module discovery (<see cref="SpellHandlerModules.Discover(CodeAssemblies)"/>) applied to a spell system
/// (<see cref="SpellSystem.RegisterModules(CodeAssemblies)"/>): a module in an assembly other than Game is found and applied, the Game modules the
/// constructor already applied are not applied twice, and the order is assembly name then full type name.
/// </summary>
public sealed class SpellHandlerModulePackTests
{
    private static readonly System.Reflection.Assembly GameAssembly = typeof(SpellSystem).Assembly;
    private static readonly System.Reflection.Assembly PackAssembly = typeof(PackProbeSpellModule).Assembly;

    private static SpellSystem NewSystem() => new(SpellStore.Empty, () => 0);

    [Fact]
    public void Discover_OverAPackSet_FindsThePackModule_AfterTheBuiltIns()
    {
        IReadOnlyList<Type> modules = SpellHandlerModules.Discover(CodeAssemblies.Of(GameAssembly).WithPack(PackAssembly));

        Assert.Contains(typeof(PackProbeSpellModule), modules);
        Assert.DoesNotContain(typeof(PackProbeSpellModule), SpellHandlerModules.BuiltIn);
        Assert.Equal(SpellHandlerModules.BuiltIn.Count + modules.Count(t => t.Assembly == PackAssembly), modules.Count);
        Assert.True(modules.ToList().IndexOf(typeof(PackProbeSpellModule)) > modules.ToList().FindLastIndex(t => t.Assembly == GameAssembly),
            "the pack assembly (ArcaneCore.TestSupport) sorts after ArcaneCore.Game");
    }

    [Fact]
    public void RegisterModules_OverAPackSet_AppliesThePackModule_AndNotTheGameModulesTwice()
    {
        SpellSystem system = NewSystem();
        Assert.False(system.HasEffectHandler(PackProbeSpellModule.ProbeEffect));

        system.RegisterModules(CodeAssemblies.Of(GameAssembly).WithPack(PackAssembly));

        Assert.True(system.HasEffectHandler(PackProbeSpellModule.ProbeEffect));
        Assert.Contains(typeof(PackProbeSpellModule), system.Modules);
        Assert.Equal(SpellHandlerModules.BuiltIn.Count + 1, system.Modules.Count(t => t.Assembly == GameAssembly || t == typeof(PackProbeSpellModule)));

        int applied = system.Modules.Count;
        system.RegisterModules(CodeAssemblies.Of(GameAssembly).WithPack(PackAssembly)); // nothing new: a second pass is a no-op, not a duplicate failure
        Assert.Equal(applied, system.Modules.Count);
    }
}

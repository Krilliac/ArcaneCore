using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Scripting;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.HotCode.Modules;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.HotCode;

/// <summary>
/// The hot module lane's spell handler modules (<see cref="ISpellHandlerModule"/> types of a module assembly) through the real
/// <see cref="ModuleGameSeams"/> and <see cref="SpellSystem"/>: load applies a module's handlers, a reload replaces the old version's, an unload takes
/// them out, a module that claims an installed handler is rolled back with nothing changed, and a server with no spell system rejects the module.
/// </summary>
public sealed class ModuleSpellHandlerSwapTests
{
    private const SpellEffectName V1Effect = SpellEffectName.Unk127;
    private const SpellEffectName V2Effect = SpellEffectName.Unk126;

    public sealed class HotModuleV1 : ISpellHandlerModule
    {
        public void Register(SpellSystem system) => system.RegisterEffect(V1Effect, static _ => { });
    }

    public sealed class HotModuleV2 : ISpellHandlerModule
    {
        public void Register(SpellSystem system)
        {
            system.RegisterEffect(V1Effect, static _ => { });
            system.RegisterEffect(V2Effect, static _ => { });
        }
    }

    public sealed class ClashingModule : ISpellHandlerModule
    {
        public void Register(SpellSystem system) => system.RegisterEffect(SpellEffectName.SchoolDamage, static _ => { });
    }

    private static SpellSystem NewSystem() => new(SpellStore.Empty, () => 0);

    private static ModuleGameSeams Seams(SpellSystem? system)
        => new(() => new CreatureAiFactory(), () => new ScriptHookRegistry(), () => [], static action => action(), NullLogger.Instance,
            spellHandlers: () => system);

    private static ModuleGameParts Parts(params Type[] types) => ModuleGameParts.Empty with { SpellHandlerTypes = types };

    private static void Swap(ModuleGameSeams seams, ModuleGameParts? old, ModuleGameParts? added)
    {
        Assert.Null(seams.Validate(old, added));
        seams.CommitSlots(old, added, new SwapUndo());
    }

    [Fact]
    public void Load_AppliesTheModulesHandlers_AndUnloadTakesThemOut()
    {
        SpellSystem system = NewSystem();
        ModuleGameSeams seams = Seams(system);
        ModuleGameParts v1 = Parts(typeof(HotModuleV1));
        Assert.False(system.HasEffectHandler(V1Effect));

        Swap(seams, null, v1);
        Assert.True(system.HasEffectHandler(V1Effect));
        Assert.Contains(typeof(HotModuleV1), system.Modules);

        Swap(seams, v1, null);
        Assert.False(system.HasEffectHandler(V1Effect));
        Assert.DoesNotContain(typeof(HotModuleV1), system.Modules);
    }

    [Fact]
    public void Reload_ReplacesTheOldVersionsHandlers()
    {
        SpellSystem system = NewSystem();
        ModuleGameSeams seams = Seams(system);
        ModuleGameParts v1 = Parts(typeof(HotModuleV1));
        ModuleGameParts v2 = Parts(typeof(HotModuleV2));
        Swap(seams, null, v1);
        SpellEffectHandler first = system.GetEffectHandler(V1Effect)!;

        Swap(seams, v1, v2);

        Assert.NotSame(first, system.GetEffectHandler(V1Effect));
        Assert.True(system.HasEffectHandler(V2Effect));
        Assert.DoesNotContain(typeof(HotModuleV1), system.Modules);
        Assert.Contains(typeof(HotModuleV2), system.Modules);
    }

    [Fact]
    public void AModuleThatReplacesAnInstalledHandler_IsRolledBack_WithNothingChanged()
    {
        SpellSystem system = NewSystem();
        ModuleGameSeams seams = Seams(system);
        SpellEffectHandler builtIn = system.GetEffectHandler(SpellEffectName.SchoolDamage)!;
        int modules = system.Modules.Count;
        ModuleGameParts clash = Parts(typeof(ClashingModule));
        Assert.Null(seams.Validate(null, clash)); // found only when it runs
        var undo = new SwapUndo();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => seams.CommitSlots(null, clash, undo));
        Assert.Contains("SchoolDamage", ex.Message);
        Assert.Null(undo.RollBack((_, _) => { }));

        Assert.Same(builtIn, system.GetEffectHandler(SpellEffectName.SchoolDamage));
        Assert.Equal(modules, system.Modules.Count);
    }

    [Fact]
    public void ReapplyingAnAppliedModule_IsRefusedInValidation()
    {
        SpellSystem system = NewSystem();
        ModuleGameSeams seams = Seams(system);
        Swap(seams, null, Parts(typeof(HotModuleV1)));

        string? error = seams.Validate(null, Parts(typeof(HotModuleV1)));

        Assert.NotNull(error);
        Assert.Contains(nameof(HotModuleV1), error);
    }

    [Fact]
    public void WithoutASpellSystem_AModuleWithHandlerModulesIsRejected_ButAnUnloadStillWorks()
    {
        ModuleGameSeams seams = Seams(null);

        Assert.Equal("spell handler modules are not available on this server", seams.Validate(null, Parts(typeof(HotModuleV1))));
        Assert.Null(seams.Validate(Parts(typeof(HotModuleV1)), null));
        seams.CommitSlots(Parts(typeof(HotModuleV1)), null, new SwapUndo());
    }
}

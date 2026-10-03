using System.Reflection;
using ArcaneCore.Game.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// The handler-module registry: <see cref="ISpellHandlerModule"/> types are found by reflection over an
/// assembly, ordered by full type name, and register effects and auras without anyone editing the
/// built-in tables (docs/integration/seams.md precedent: [DefaultMapUpdater] / IWorldFeature discovery).
/// </summary>
public sealed class SpellHandlerModuleTests
{
    // Unused ids inside the byte-sized enums (the generated enums stop well below these).
    internal const SpellEffectName FixtureEffect = (SpellEffectName)250;
    internal const AuraType FixtureAura = (AuraType)250;

    [Fact]
    public void RegisterModules_InstallsTheEffectsAndAurasAModuleDeclares()
    {
        using var kit = new SpellTestKit();
        Assert.False(kit.System.HasEffectHandler(FixtureEffect));
        Assert.False(kit.System.HasAuraHandler(FixtureAura));

        kit.System.RegisterModules([typeof(GoodFixtureModule)]);

        Assert.True(kit.System.HasEffectHandler(FixtureEffect));
        Assert.True(kit.System.HasAuraHandler(FixtureAura));
        Assert.Equal([typeof(GoodFixtureModule)], kit.System.Modules.Skip(kit.System.Modules.Count - 1));
    }

    [Fact]
    public void ModuleHandlers_RunLikeBuiltInOnes()
    {
        using var kit = new SpellTestKit(
            SpellTestKit.Spell(900, SpellTestKit.Effect(FixtureEffect, 7)));
        (var player, _) = kit.AddPlayer(1);
        kit.Spellbook.Teach(player, 900);
        GoodFixtureModule.Seen = 0;
        kit.System.RegisterModules([typeof(GoodFixtureModule)]);

        kit.System.HandleCastRequest(player, 900, SpellCastTargets.ForSelf());

        Assert.Equal(7, GoodFixtureModule.Seen);
    }

    [Fact]
    public void ReplacingAnExistingEffectHandler_FailsClosed()
    {
        using var kit = new SpellTestKit();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => kit.System.RegisterModules([typeof(ReplacesSchoolDamageModule)]));

        Assert.Contains(nameof(ReplacesSchoolDamageModule), error.Message);
        Assert.Contains("SchoolDamage", error.Message);
    }

    [Fact]
    public void ReplacingAnExistingAuraHandler_FailsClosed()
    {
        using var kit = new SpellTestKit();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => kit.System.RegisterModules([typeof(ReplacesStunModule)]));

        Assert.Contains(nameof(ReplacesStunModule), error.Message);
        Assert.Contains("ModStun", error.Message);
    }

    [Fact]
    public void TwoModulesClaimingTheSameEffect_FailClosed()
    {
        using var kit = new SpellTestKit();
        kit.System.RegisterModules([typeof(GoodFixtureModule)]);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => kit.System.RegisterModules([typeof(SecondClaimantModule)]));

        Assert.Contains(nameof(SecondClaimantModule), error.Message);
    }

    [Fact]
    public void ApplyingTheSameModuleTwice_FailsClosed()
    {
        using var kit = new SpellTestKit();
        kit.System.RegisterModules([typeof(GoodFixtureModule)]);

        Assert.Throws<InvalidOperationException>(() => kit.System.RegisterModules([typeof(GoodFixtureModule)]));
    }

    [Fact]
    public void ModuleWithoutParameterlessConstructor_IsRejectedNotSkipped()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => SpellHandlerModules.Create(typeof(NeedsArgumentModule)));

        Assert.Contains(nameof(NeedsArgumentModule), error.Message);
    }

    [Fact]
    public void NonModuleAndAbstractTypes_AreRejectedByCreate()
    {
        Assert.Throws<InvalidOperationException>(() => SpellHandlerModules.Create(typeof(string)));
        Assert.Throws<InvalidOperationException>(() => SpellHandlerModules.Create(typeof(AbstractModule)));
    }

    [Fact]
    public void Discover_ReturnsModulesOrderedByFullTypeName()
    {
        // The order is part of the contract: it decides which module reports a duplicate, and it must
        // not depend on reflection enumeration order.
        IReadOnlyList<Type> found = SpellHandlerModules.Discover(typeof(SpellSystem).Assembly);

        Assert.Contains(typeof(DirectCombatEffects), found);
        Assert.Equal(found.OrderBy(t => t.FullName, StringComparer.Ordinal), found);
        Assert.All(found, t => Assert.True(typeof(ISpellHandlerModule).IsAssignableFrom(t) && !t.IsAbstract));
        Assert.Equal(found, SpellHandlerModules.BuiltIn);
    }

    [Fact]
    public void BuiltInModules_AreAppliedByTheConstructor()
    {
        using var kit = new SpellTestKit();

        Assert.Equal(SpellHandlerModules.BuiltIn, kit.System.Modules);
        // The original handler tables are intact: a module only adds.
        Assert.True(kit.System.HasEffectHandler(SpellEffectName.SchoolDamage));
        Assert.True(kit.System.HasAuraHandler(AuraType.ModStun));
    }

    [Fact]
    public void Discover_FindsFixtureModulesInTheTestAssemblyAndRejectsTheBrokenOne()
    {
        // The broken fixture (no parameterless constructor) makes assembly discovery fail instead of
        // silently dropping the module.
        Assert.Throws<InvalidOperationException>(() => SpellHandlerModules.Discover(Assembly.GetExecutingAssembly()));
    }

    public sealed class GoodFixtureModule : ISpellHandlerModule
    {
        public static int Seen { get; set; }

        public void Register(SpellSystem system)
        {
            system.RegisterEffect(FixtureEffect, static context => Seen = context.Value);
            system.RegisterAura(FixtureAura, new AuraHandler(null, null));
        }
    }

    public sealed class SecondClaimantModule : ISpellHandlerModule
    {
        public void Register(SpellSystem system) => system.RegisterEffect(FixtureEffect, static _ => { });
    }

    public sealed class ReplacesSchoolDamageModule : ISpellHandlerModule
    {
        public void Register(SpellSystem system) => system.RegisterEffect(SpellEffectName.SchoolDamage, static _ => { });
    }

    public sealed class ReplacesStunModule : ISpellHandlerModule
    {
        public void Register(SpellSystem system) => system.RegisterAura(AuraType.ModStun, new AuraHandler(null, null));
    }

    public sealed class NeedsArgumentModule : ISpellHandlerModule
    {
        public NeedsArgumentModule(int unused) => _ = unused;

        public void Register(SpellSystem system)
        {
        }
    }

    public abstract class AbstractModule : ISpellHandlerModule
    {
        public abstract void Register(SpellSystem system);
    }
}

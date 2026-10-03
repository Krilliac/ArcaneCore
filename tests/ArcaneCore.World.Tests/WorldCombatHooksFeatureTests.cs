using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.World.Combat;
using ArcaneCore.World.Features;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests;

public sealed class WorldCombatHooksFeatureTests
{
    private static WorldRuntime NewWorld(ServiceProvider empty)
    {
        var saves = new CharacterSaveQueue(empty.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance);
        return new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves, NullLogger<WorldRuntime>.Instance);
    }

    private static ServiceProvider WithCatalog(FactionTemplateCatalog catalog)
        => new ServiceCollection().AddSingleton(catalog).BuildServiceProvider();

    private static readonly FactionTemplateCatalog OneRow = new([new FactionTemplateRecord(1, 1, 0, 1, 0, 0)]);

    [Fact]
    public void IsDiscoveredAsAWorldFeature()
        => Assert.Contains(typeof(WorldCombatHooksFeature), WorldFeatures.FeatureTypes);

    [Fact]
    public void WithALoadedCatalog_RegistersFactionHooks()
    {
        using ServiceProvider empty = new ServiceCollection().BuildServiceProvider();
        using WorldRuntime world = NewWorld(empty);
        using ServiceProvider sp = WithCatalog(OneRow);
        new WorldCombatHooksFeature(sp, NullLogger<WorldCombatHooksFeature>.Instance).Attach(world);
        Assert.IsType<FactionCombatHooks>(CombatHooks.For(world));
    }

    [Fact]
    public void WithoutACatalogOrWithAnEmptyOne_KeepsTheDefaultHooks()
    {
        using ServiceProvider empty = new ServiceCollection().BuildServiceProvider();
        using WorldRuntime world = NewWorld(empty);
        new WorldCombatHooksFeature(empty, NullLogger<WorldCombatHooksFeature>.Instance).Attach(world);
        Assert.Same(CombatHooks.Default, CombatHooks.For(world));
        using ServiceProvider sp = WithCatalog(FactionTemplateCatalog.Empty);
        new WorldCombatHooksFeature(sp, NullLogger<WorldCombatHooksFeature>.Instance).Attach(world);
        Assert.Same(CombatHooks.Default, CombatHooks.For(world));
    }

    [Fact]
    public void DoesNotOverrideHooksAnotherFeatureRegisteredFirst()
    {
        using ServiceProvider empty = new ServiceCollection().BuildServiceProvider();
        using WorldRuntime world = NewWorld(empty);
        var other = new CombatHooks();
        CombatHooks.Register(world, other);
        using ServiceProvider sp = WithCatalog(OneRow);
        new WorldCombatHooksFeature(sp, NullLogger<WorldCombatHooksFeature>.Instance).Attach(world);
        Assert.Same(other, CombatHooks.For(world));
    }
}

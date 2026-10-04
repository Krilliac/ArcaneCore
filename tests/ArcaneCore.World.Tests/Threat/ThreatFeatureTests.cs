using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Combat.Threat;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Combat;
using ArcaneCore.World.Features;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Threat;

/// <summary>The world feature that binds the threat formula to every map's combat.</summary>
public sealed class ThreatFeatureTests
{
    [Fact]
    public async Task Attach_BindsTheSpellSystemsThreatModifiersAndTheCatalog_ToEveryMap()
    {
        await using ServiceProvider scopes = new ServiceCollection().BuildServiceProvider();
        using var world = new WorldRuntime(new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 }, new NoopSaveQueue(), NullLogger<WorldRuntime>.Instance);
        await using var spells = new SpellFeature(scopes.GetRequiredService<IServiceScopeFactory>(), NullLogger<SpellFeature>.Instance);
        var catalog = new EmptyCatalog();
        var feature = new ThreatFeature(new Services(spells, catalog));

        _ = world.GetMap(0); // exists before the feature attaches
        feature.Attach(world);
        _ = world.GetMap(1); // created after it (MapCreated)

        Assert.True(world.Maps.Count() >= 2);
        foreach (var map in world.Maps)
        {
            MapCombat combat = Assert.IsType<MapCombat>(map.FindUpdater<MapCombat>());
            Assert.IsType<SpellThreatModifiers>(combat.ThreatModifiers);
            Assert.Same(catalog, combat.SpellThreatCatalog);
        }
    }

    [Fact]
    public void TheDamageSinkOfTheWorld_IsTheMapCombatSink()
    {
        Assert.True(typeof(MapCombatDamageSink).IsAssignableFrom(typeof(SpellFeature).Assembly.GetType("ArcaneCore.World.Spells.WorldSpellDamageSink")));
        Assert.Contains(typeof(ThreatFeature), WorldFeatures.FeatureTypes);
    }

    private sealed class Services(SpellFeature spells, ISpellThreatCatalog catalog) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(SpellFeature) ? spells : serviceType == typeof(ISpellThreatCatalog) ? catalog : null;
    }

    private sealed class EmptyCatalog : ISpellThreatCatalog
    {
        public SpellThreatEntry? Find(uint spellId) => null;
    }

    private sealed class NoopSaveQueue : ICharacterSaveQueue
    {
        public void Enqueue(CharacterState state)
        {
        }
    }
}

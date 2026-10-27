using System.Runtime.CompilerServices;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Crafting;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.World.Combat;
using ArcaneCore.World.Crafting;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Items;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Skills;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Instances;

/// <summary>
/// An unloaded instance or battleground map must be released by every feature that tracks maps: the per-map collections
/// (<c>_maps</c> of the enchanting, economy, item maintenance and skills features, <c>_observed</c> of the combo point and
/// reactive services, and the spell feature's combat subscriptions) used to keep each destroyed map reachable for the life of the process.
/// </summary>
public sealed class InstanceMapRetentionTests
{
    private const uint MapId = 33;

    private sealed class Rig : IAsyncDisposable
    {
        private readonly ServiceProvider _services;

        public Rig()
        {
            _services = new ServiceCollection()
                .AddSingleton(sp => new SpellFeature(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<SpellFeature>.Instance))
                .AddSingleton(sp => new TeleportFeature(sp, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<TeleportFeature>.Instance))
                .AddSingleton(new EnchantCatalog([new SpellItemEnchantment(1, [0u, 0u, 0u], [0, 0, 0], [0u, 0u, 0u], "test", 0, 0)]))
                .AddSingleton(SkillCatalog.Empty)
                .AddSingleton(sp => new ComboFeature(sp))
                .BuildServiceProvider();
            IServiceScopeFactory scopes = _services.GetRequiredService<IServiceScopeFactory>();
            var saves = new CharacterSaveQueue(scopes, NullLogger<CharacterSaveQueue>.Instance);
            World = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves, NullLogger<WorldRuntime>.Instance);
            Spells = _services.GetRequiredService<SpellFeature>();
            Spells.Attach(World);
            Enchanting = new EnchantingFeature(_services, NullLogger<EnchantingFeature>.Instance);
            Enchanting.Attach(World);
            Skills = new SkillsFeature(_services, scopes, NullLogger<SkillsFeature>.Instance);
            Skills.Attach(World);
            Combo = _services.GetRequiredService<ComboFeature>();
            Combo.Attach(World);
            Reactive = new ReactiveFeature(_services);
            Reactive.Attach(World);
            Maintenance = new ItemMaintenanceFeature(new ItemsFeature(scopes, NullLogger<ItemsFeature>.Instance));
            Maintenance.Attach(World);
            Economy = new EconomyFeature(_services, scopes, NullLogger<EconomyFeature>.Instance);
            Economy.Attach(World);
        }

        public WorldRuntime World { get; }
        public SpellFeature Spells { get; }
        public ComboFeature Combo { get; }
        public ReactiveFeature Reactive { get; }
        public ItemMaintenanceFeature Maintenance { get; }
        public EconomyFeature Economy { get; }
        public EnchantingFeature Enchanting { get; }
        public SkillsFeature Skills { get; }

        public int Tracked(string name) => name switch
        {
            "spell" => Spells.SubscribedCombatCount,
            "combo" => Combo.Service!.ObservedCount,
            "reactive" => Reactive.Reactives!.ObservedCount,
            "maintenance" => Maintenance.TrackedMapCount,
            "economy" => Economy.TrackedMapCount,
            "enchanting" => Enchanting.TrackedMapCount,
            "skills" => Skills.TrackedMapCount,
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };

        public async ValueTask DisposeAsync()
        {
            await Economy.DisposeAsync();
            await Skills.DisposeAsync();
            await Spells.DisposeAsync();
            World.Dispose();
            await _services.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("spell")]
    [InlineData("combo")]
    [InlineData("reactive")]
    [InlineData("maintenance")]
    [InlineData("economy")]
    [InlineData("enchanting")]
    [InlineData("skills")]
    public async Task UnloadingAnInstanceMap_ReleasesItFromTheFeatureCollection(string feature)
    {
        await using var rig = new Rig();
        Map retired = rig.World.GetMap(MapId, 101);
        Map neighbour = rig.World.GetMap(MapId, 102);
        Assert.Equal(2, rig.Tracked(feature));

        rig.World.UnloadMap(retired);
        rig.World.RunTick(0);

        Assert.True(retired.IsUnloaded);
        Assert.Equal(1, rig.Tracked(feature));
        Map replacement = rig.World.GetMap(MapId, 101);
        Assert.NotSame(retired, replacement);
        Assert.Equal(2, rig.Tracked(feature));
        Assert.Same(neighbour, rig.World.FindMap(MapId, 102));
    }

    [Fact]
    public async Task AnUnloadedInstanceMap_IsCollectable_AfterAllSevenFeaturesHaveSeenIt()
    {
        await using var rig = new Rig();
        WeakReference<Map> weak = CreateAndUnload(rig.World);

        for (int i = 0; i < 3; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
        }

        Assert.False(weak.TryGetTarget(out _), "the unloaded instance map is still reachable from some root");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<Map> CreateAndUnload(WorldRuntime world)
    {
        Map map = world.GetMap(MapId, 201);
        var weak = new WeakReference<Map>(map);
        world.UnloadMap(map);
        world.RunTick(0);
        Assert.True(map.IsUnloaded);
        return weak;
    }
}

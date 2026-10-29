using System.Reflection;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Items;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Spells.Procs;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

/// <summary>
/// <see cref="SpellProcFeature"/> (kills) and <see cref="ItemCombatProcFeature"/> (weapon hits) follow each map's combat once and let go when the
/// map unloads, as <see cref="SpellFeature"/> does: a second subscription would fire every KILL proc and item proc twice, and a subscription left on an
/// unloaded map's combat is never released.
/// </summary>
public sealed class ProcFeatureSubscriptionTests
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
                .BuildServiceProvider();
            var saves = new CharacterSaveQueue(_services.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance);
            World = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves, NullLogger<WorldRuntime>.Instance);
            Spells = _services.GetRequiredService<SpellFeature>();
            Spells.Attach(World);
            Procs = new SpellProcFeature(_services, NullLogger<SpellProcFeature>.Instance);
            Items = new ItemCombatProcFeature(Spells);
        }

        public WorldRuntime World { get; }

        public SpellFeature Spells { get; }

        public SpellProcFeature Procs { get; }

        public ItemCombatProcFeature Items { get; }

        public async ValueTask DisposeAsync()
        {
            await Spells.DisposeAsync();
            World.Dispose();
            await _services.DisposeAsync();
        }
    }

    /// <summary>How many of <paramref name="eventName"/>'s handlers on <paramref name="combat"/> belong to <paramref name="target"/> (the field-like event's backing delegate).</summary>
    private static int Handlers(MapCombat combat, string eventName, object target)
    {
        Delegate? field = (Delegate?)typeof(MapCombat).GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(combat);
        return field?.GetInvocationList().Count(d => ReferenceEquals(d.Target, target)) ?? 0;
    }

    [Fact]
    public async Task SpellProcFeature_FollowsAMapOnce_AndReleasesItOnUnload()
    {
        await using var rig = new Rig();
        Map map = rig.World.GetMap(MapId, 101);
        rig.Procs.Attach(rig.World); // the map exists: reached by the attach-time walk
        MapCombat combat = map.FindUpdater<MapCombat>()!;
        Assert.Equal(1, Handlers(combat, nameof(MapCombat.UnitKilled), rig.Spells.System));

        typeof(SpellProcFeature).GetMethod("Subscribe", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(rig.Procs, [map]); // MapCreated for the same map
        Assert.Equal(1, Handlers(combat, nameof(MapCombat.UnitKilled), rig.Spells.System));

        rig.World.UnloadMap(map);
        rig.World.RunTick(0);

        Assert.True(map.IsUnloaded);
        Assert.Equal(0, Handlers(combat, nameof(MapCombat.UnitKilled), rig.Spells.System));
    }

    [Fact]
    public async Task ItemCombatProcFeature_FollowsAMapOnce_AndReleasesItOnUnload()
    {
        await using var rig = new Rig();
        Map map = rig.World.GetMap(MapId, 102);
        rig.Items.Attach(rig.World);
        MapCombat combat = map.FindUpdater<MapCombat>()!;
        Assert.Equal(1, Handlers(combat, nameof(MapCombat.MeleeWeaponHitDealt), rig.Spells.System));

        typeof(ItemCombatProcFeature).GetMethod("Subscribe", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(rig.Items, [map]);
        Assert.Equal(1, Handlers(combat, nameof(MapCombat.MeleeWeaponHitDealt), rig.Spells.System));

        rig.World.UnloadMap(map);
        rig.World.RunTick(0);

        Assert.True(map.IsUnloaded);
        Assert.Equal(0, Handlers(combat, nameof(MapCombat.MeleeWeaponHitDealt), rig.Spells.System));
    }
}

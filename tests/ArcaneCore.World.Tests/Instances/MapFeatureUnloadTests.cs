using System.Reflection;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Progression;
using ArcaneCore.World.Reputation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Instances;

/// <summary>Actual instance unloads release the feature's map ownership and combat event subscription.</summary>
public sealed class MapFeatureUnloadTests
{
    [Fact]
    public void Unload_DefaultCombatReleasesItsWorldEventRoots_AndPreservesOtherInstances()
    {
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        var saves = new CharacterSaveQueue(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CharacterSaveQueue>.Instance);
        using var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves,
            NullLogger<WorldRuntime>.Instance);
        Map retired = world.GetMap(33, 101);
        Map neighbour = world.GetMap(33, 102);
        Assert.Equal(1, WorldHandlers(world, nameof(WorldRuntime.PlayerLoggingOut), retired.Combat));

        world.UnloadMap(retired);
        world.RunTick(0);

        Assert.True(retired.IsUnloaded);
        Assert.Equal(0, WorldHandlers(world, nameof(WorldRuntime.PlayerLoggingOut), retired.Combat));
        Assert.Equal(0, WorldHandlers(world, nameof(WorldRuntime.MapUnloading), retired.Combat));
        Assert.Equal(1, WorldHandlers(world, nameof(WorldRuntime.PlayerLoggingOut), neighbour.Combat));
        Assert.Equal(1, WorldHandlers(world, nameof(WorldRuntime.MapUnloading), neighbour.Combat));
        Map replacement = world.GetMap(33, 101);
        Assert.NotSame(retired, replacement);
        Assert.Equal(1, WorldHandlers(world, nameof(WorldRuntime.PlayerLoggingOut), replacement.Combat));
        Assert.Equal(1, WorldHandlers(world, nameof(WorldRuntime.MapUnloading), replacement.Combat));
        Assert.Equal(0, WorldHandlers(world, nameof(WorldRuntime.PlayerLoggingOut), retired.Combat));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unload_ReleasesOnlyTheExactInstance_AndAttachesItsReplacement(bool reputationOwner)
    {
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        IServiceScopeFactory scopes = services.GetRequiredService<IServiceScopeFactory>();
        var saves = new CharacterSaveQueue(scopes, NullLogger<CharacterSaveQueue>.Instance);
        using var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves,
            NullLogger<WorldRuntime>.Instance);
        var npcs = new NpcServicesFeature(services, NullLogger<NpcServicesFeature>.Instance);
        using IDisposable? npcLifetime = (object)npcs as IDisposable;
        await using var reputation = new ReputationFeature(services, scopes, NullLoggerFactory.Instance);
        npcs.Attach(world);
        reputation.Attach(world);
        Map shared = world.GetMap(33);
        Map retired = world.GetMap(33, 101);
        Map neighbour = world.GetMap(33, 102);
        Assert.True(IsRetained(npcs, reputation, retired, reputationOwner));
        if (reputationOwner)
        {
            Assert.Equal(1, KillHandlers(retired.Combat, reputation));
        }

        world.UnloadMap(retired);
        world.RunTick(0);

        Assert.True(retired.IsUnloaded);
        Assert.Null(world.FindMap(33, 101));
        Assert.False(IsRetained(npcs, reputation, retired, reputationOwner));
        Assert.True(IsRetained(npcs, reputation, shared, reputationOwner));
        Assert.True(IsRetained(npcs, reputation, neighbour, reputationOwner));
        if (reputationOwner)
        {
            Assert.Equal(0, KillHandlers(retired.Combat, reputation));
            Assert.Equal(1, KillHandlers(neighbour.Combat, reputation));
        }

        Map replacement = world.GetMap(33, 101);
        Assert.NotSame(retired, replacement);
        Assert.True(IsRetained(npcs, reputation, replacement, reputationOwner));
        Assert.False(IsRetained(npcs, reputation, retired, reputationOwner));
        if (reputationOwner)
        {
            Assert.Equal(1, KillHandlers(replacement.Combat, reputation));
        }

        world.UnloadMap(replacement);
        world.RunTick(0);
        Assert.False(IsRetained(npcs, reputation, replacement, reputationOwner));
        Assert.Same(neighbour, world.FindMap(33, 102));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispose_ClearsOwnershipAndDoesNotSubscribeToLaterMaps(bool reputationOwner)
    {
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        IServiceScopeFactory scopes = services.GetRequiredService<IServiceScopeFactory>();
        var saves = new CharacterSaveQueue(scopes, NullLogger<CharacterSaveQueue>.Instance);
        using var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves,
            NullLogger<WorldRuntime>.Instance);
        var npcs = new NpcServicesFeature(services, NullLogger<NpcServicesFeature>.Instance);
        using IDisposable? npcLifetime = (object)npcs as IDisposable;
        await using var reputation = new ReputationFeature(services, scopes, NullLoggerFactory.Instance);
        npcs.Attach(world);
        reputation.Attach(world);
        Map previous = world.GetMap(33, 101);
        if (reputationOwner)
        {
            await reputation.DisposeAsync();
        }
        else
        {
            Assert.IsAssignableFrom<IDisposable>(npcs).Dispose();
        }

        Assert.False(IsRetained(npcs, reputation, previous, reputationOwner));
        Map later = world.GetMap(33, 102);
        Assert.False(IsRetained(npcs, reputation, later, reputationOwner));
        if (reputationOwner)
        {
            Assert.Equal(0, KillHandlers(previous.Combat, reputation));
            Assert.Equal(0, KillHandlers(later.Combat, reputation));
        }
        else
        {
            Assert.DoesNotContain(later.Updaters, u => u.GetType().DeclaringType == typeof(NpcServicesFeature));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Progression_ReleasesCombatOwnershipOnExactUnloadOrDisposal(bool dispose)
    {
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        var saves = new CharacterSaveQueue(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CharacterSaveQueue>.Instance);
        using var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves,
            NullLogger<WorldRuntime>.Instance);
        using var progression = new ProgressionFeature(services, NullLogger<ProgressionFeature>.Instance);
        progression.Attach(world);
        Map retired = world.GetMap(33, 101);
        Map neighbour = world.GetMap(33, 102);
        Assert.Contains(retired.Combat, OwnedSet<MapCombat>(progression, "_combat"));
        Assert.Equal(1, KillHandlers(retired.Combat, progression));

        if (dispose)
        {
            progression.Dispose();
        }
        else
        {
            world.UnloadMap(retired);
            world.RunTick(0);
            Assert.True(retired.IsUnloaded);
            Assert.Null(world.FindMap(33, 101));
        }

        Assert.DoesNotContain(retired.Combat, OwnedSet<MapCombat>(progression, "_combat"));
        Assert.Equal(0, KillHandlers(retired.Combat, progression));
        Assert.Equal(dispose ? 0 : 1, KillHandlers(neighbour.Combat, progression));
        Map later = world.GetMap(33, 103);
        Assert.Equal(dispose ? 0 : 1, KillHandlers(later.Combat, progression));
        if (!dispose)
        {
            Map replacement = world.GetMap(33, 101);
            Assert.NotSame(retired, replacement);
            Assert.Contains(replacement.Combat, OwnedSet<MapCombat>(progression, "_combat"));
            Assert.Equal(1, KillHandlers(replacement.Combat, progression));
        }
    }

    // The failure is retained ownership, not garbage-collector timing. Inspect the actual
    // roots and delegate after the public unload path; unrelated map owners may retain maps.
    private static bool IsRetained(NpcServicesFeature npcs, ReputationFeature reputation, Map map, bool reputationOwner)
        => reputationOwner
            ? OwnedSet<MapCombat>(reputation, "_combat").Contains(map.Combat)
            : OwnedSet<Map>(npcs, "_maps").Contains(map);

    private static HashSet<T> OwnedSet<T>(object owner, string name)
        => Assert.IsType<HashSet<T>>((owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"missing ownership field {name}")).GetValue(owner));

    private static int KillHandlers(MapCombat combat, object owner)
    {
        var handlers = (Delegate?)typeof(MapCombat)
            .GetField(nameof(MapCombat.UnitKilled), BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(combat);
        return handlers?.GetInvocationList().Count(handler => ReferenceEquals(handler.Target, owner)) ?? 0;
    }

    private static int WorldHandlers(WorldRuntime world, string eventName, MapCombat combat)
    {
        var handlers = (Delegate?)typeof(WorldRuntime)
            .GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(world);
        return handlers?.GetInvocationList().Count(handler => ReferenceEquals(handler.Target, combat)
            // The immutable baseline stores an anonymous logout delegate; its compiler
            // closure may own the combat object instead of using it as the direct target.
            || (handler.Target is { } closure && closure.GetType()
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Any(field => ReferenceEquals(field.GetValue(closure), combat)))) ?? 0;
    }
}

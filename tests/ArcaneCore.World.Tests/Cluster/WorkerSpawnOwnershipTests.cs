using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Creatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster;

/// <summary>S4a: creature spawns on a map the worker does not own (Cluster:Worker:OwnedMaps) are not installed, and the map is not created for them.</summary>
public sealed class WorkerSpawnOwnershipTests
{
    private const uint Entry = 900810;

    private sealed class Store : ICreatureDataStore
    {
        public Task<CreatureContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new CreatureContent(
            [new CreatureTemplate { Entry = Entry, Name = "Ownership probe", DisplayIds = [49] }],
            [
                new CreatureSpawn { Guid = 900811, Entry = Entry, MapId = 0, X = -8948.95f, Y = -132.49f, Z = 83.53f },
                new CreatureSpawn { Guid = 900812, Entry = Entry, MapId = 1, X = -618.5f, Y = -4251.7f, Z = 38.7f },
            ],
            [], [], []));
    }

    [Fact]
    public async Task CreatureSpawnsOnAnUnownedMap_AreSkipped_AndTheMapIsNotCreated()
    {
        using ServiceProvider services = new ServiceCollection().AddSingleton<ICreatureDataStore>(new Store()).BuildServiceProvider();
        using var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, new NullSaveQueue(), NullLogger<WorldRuntime>.Instance);
        Assert.True(MapOwnership.TryParse("0", allowInstanceMaps: true, out MapOwnership ownership, out string? error), error);
        world.Ownership = ownership;
        var feature = new CreatureWorldFeature(services, NullLogger<CreatureWorldFeature>.Instance);

        feature.Attach(world);
        world.RunTick(50);

        // The posted Install swallows a MapNotOwnedException into the fault ledger, so run it synchronously here (the test thread is the world
        // thread): without the Owns guard, GetOrCreateSystem(1) reaches world.GetMap(1) and throws.
        CreatureContent content = await new Store().LoadAsync();
        Assert.Null(Record.Exception(() => feature.Install(content)));

        Assert.NotNull(feature.FindSystem(0));
        Assert.Null(feature.FindSystem(1));
        Assert.Null(world.FindMap(1));
    }
}

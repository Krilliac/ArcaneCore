using ArcaneCore.Kernel.Characters.Pets;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Pets;

public sealed class PersistentPetWorldTests
{
    [Fact]
    public async Task Feature_WiresTheDurableStoreWithoutBlockingWorldConstruction()
    {
        var store = new MemoryPetStore();
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
            services.AddSingleton<IPersistentPetStore>(store));

        var pets = host.WorldServices.GetRequiredService<ArcaneCore.World.Pets.PetsFeature>();
        Assert.Null(pets.Service.Persistence);
        Assert.NotNull(pets.Service.LoadPersistence);
        Assert.NotNull(pets.Service.SavePersistence);
    }

    private sealed class MemoryPetStore : IPersistentPetStore
    {
        public Task<PersistentPetSnapshot?> LoadCurrentAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult<PersistentPetSnapshot?>(null);

        public Task SaveCurrentAsync(PersistentPetSnapshot snapshot, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeleteAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}

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

    [Fact]
    public async Task StopAsync_UnsubscribesTheLoginAndLogoutHandlers()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
            services.AddSingleton<IPersistentPetStore>(new MemoryPetStore()));

        var feature = host.WorldServices.GetRequiredService<ArcaneCore.World.Pets.PersistentPetFeature>();
        int loggedIn = Subscribers(host.World, "PlayerLoggedIn");
        int loggingOut = Subscribers(host.World, "PlayerLoggingOut");

        await feature.StopAsync();

        Assert.Equal(loggedIn - 1, Subscribers(host.World, "PlayerLoggedIn"));
        Assert.Equal(loggingOut - 1, Subscribers(host.World, "PlayerLoggingOut"));
    }

    // A field-like event's backing field carries the event's name; its invocation list is the subscriber count.
    private static int Subscribers(object owner, string eventName)
    {
        System.Reflection.FieldInfo field = owner.GetType().GetField(eventName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"no backing field for {eventName}");
        return (field.GetValue(owner) as Delegate)?.GetInvocationList().Length ?? 0;
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

using System.Runtime.CompilerServices;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Net;
using Microsoft.Extensions.Logging.Abstractions;
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

    [Fact]
    public async Task ALoginThatNeverEntersTheWorld_DoesNotPinItsPlayer()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
            services.AddSingleton<IPersistentPetStore>(new MemoryPetStore()));
        var feature = host.WorldServices.GetRequiredService<ArcaneCore.World.Pets.PersistentPetFeature>();
        await using AsyncServiceScope scope = host.WorldServices.CreateAsyncScope();
        using var stream = new MemoryStream();
        var session = new WorldSession(stream, "127.0.0.1:41100", scope.ServiceProvider, host.Opcodes, host.World, host.Registry,
            new WorldSessionOptions(), NullLogger.Instance);

        WeakReference weak = await LoadAndAbandonAsync(feature, session);   // the login aborts: PlayerLoggedIn never fires
        for (int i = 0; i < 5 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.False(weak.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> LoadAndAbandonAsync(ArcaneCore.World.Pets.PersistentPetFeature feature, WorldSession session)
    {
        var record = new CharacterRecord { Id = 77, AccountId = 1, Name = "Aborted", Race = 1, Class = (byte)Class.Hunter, Level = 10 };
        var player = new Player(record, new PlayerAppearance(49, 1, PowerType.Mana, 60, 0, 60, 100, 100, 100), new NullPlayerSession());
        await feature.OnPlayerLoadingAsync(session, record, player);
        return new WeakReference(player);
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

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
    public async Task Feature_WiresThePetWriteFenceToTheRetirementRegistry()
    {
        // PersistentPetFeature.Attach: WritesRetired asks the registry under the pet queue's name (counting each drop there), WritesSealed follows its seal.
        var registry = new ArcaneCore.World.Persistence.CharacterRetirementRegistry();
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<IPersistentPetStore>(new MemoryPetStore());
            services.AddSingleton(registry);
        });
        var pets = host.WorldServices.GetRequiredService<ArcaneCore.World.Pets.PetsFeature>();
        Func<int, bool> retired = Assert.IsAssignableFrom<Func<int, bool>>(pets.Service.WritesRetired);
        Func<bool> sealedNow = Assert.IsAssignableFrom<Func<bool>>(pets.Service.WritesSealed);

        Assert.False(retired(42));
        Assert.Equal(0, registry.DroppedCount("SummonService"));
        registry.Retire([42]);
        Assert.True(retired(42));
        Assert.False(retired(43));
        Assert.Equal(1, registry.DroppedCount("SummonService"));

        Assert.False(sealedNow());
        registry.Seal();
        Assert.True(sealedNow());
        registry.Unseal();
        Assert.False(sealedNow());
    }

    [Fact]
    public async Task Feature_WithNoRetirementRegistry_LeavesTheFenceSeamsNull()
    {
        // The test host does not register a registry (AddWorldDaemon does), so nothing is ever retired or sealed.
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
            services.AddSingleton<IPersistentPetStore>(new MemoryPetStore()));
        var pets = host.WorldServices.GetRequiredService<ArcaneCore.World.Pets.PetsFeature>();

        Assert.Null(pets.Service.WritesRetired);
        Assert.Null(pets.Service.WritesSealed);
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
    [Trait("Category", "Timing")] // GC reachability: collection timing under a loaded box
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

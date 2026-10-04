using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Npc;

/// <summary>
/// <see cref="NpcServicesFeature"/>'s taxi wiring over <see cref="TaxiFlightWriteQueue"/>: the logout save and the
/// landing delete reach the queue, a stored route is resumed and then cleared, a save that cannot be persisted returns the
/// character to the departure node and clears the route (what the synchronous save did), the login barrier refuses while a
/// write is not durable, a later logout retries it, and <see cref="WorldFeatures.StopWorldFeaturesAsync"/> drains the queue.
/// </summary>
public sealed class NpcServicesFeatureTaxiTests
{
    private const uint Gryphon = 3837;
    private const uint GryphonDisplay = 1147;
    private const float Z = 83.5f;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    // Node 1 (0,0) → node 2 (64,0) over path 10, with a midpoint waypoint; a second leg to node 3 over path 11.
    private static readonly NpcContent TaxiContent = NpcContent.Empty with
    {
        TaxiNodes =
        [
            new TaxiNode { Id = 1, MapId = 0, X = 0, Y = 0, Z = Z, MountAlliance = Gryphon, MountHorde = 2224 },
            new TaxiNode { Id = 2, MapId = 0, X = 64, Y = 0, Z = Z, MountAlliance = Gryphon, MountHorde = 2224 },
            new TaxiNode { Id = 3, MapId = 0, X = 64, Y = 64, Z = Z, MountAlliance = Gryphon, MountHorde = 2224 },
        ],
        TaxiPaths =
        [
            new TaxiPath { Id = 10, FromNode = 1, ToNode = 2, Price = 100 },
            new TaxiPath { Id = 11, FromNode = 2, ToNode = 3, Price = 50 },
        ],
    };

    private static readonly TaxiPathNodeCatalog PathNodes = new(
    [
        new TaxiPathNodeRecord(1, 10, 0, 0, 0, 0, Z, 0, 0),
        new TaxiPathNodeRecord(2, 10, 1, 0, 32, 0, 90, 0, 0),
        new TaxiPathNodeRecord(3, 10, 2, 0, 64, 0, Z, 0, 0),
        new TaxiPathNodeRecord(4, 11, 0, 0, 64, 0, Z, 0, 0),
        new TaxiPathNodeRecord(5, 11, 1, 0, 64, 64, Z, 0, 0),
    ]);

    [Fact]
    public async Task LogoutMidFlight_SavesTheRoute_AndTheResumedFlightClearsItWhenItLands()
    {
        await using var rig = await Rig.CreateAsync();
        Player flyer = await rig.LoginAsync();
        Assert.True(rig.Npcs.Flights!.StartFlight(flyer, [1, 2], [10], Gryphon));
        rig.World.RunTick(1000);
        float x = flyer.X;
        Assert.InRange(x, 20f, 44f);

        rig.World.RemovePlayer(flyer);
        Assert.False(rig.Npcs.Flights.IsFlying(flyer));

        // The login barrier waits for the save; the stored route is staged and resumed where the character was saved.
        Player relogged = await rig.LoadAsync();
        Assert.Equal([1u, 2u], Assert.IsType<TaxiFlightRoute>(rig.Store.Saved(rig.CharacterId)).Nodes);
        rig.Enter(relogged);
        Assert.True(rig.Npcs.Flights.IsFlying(relogged));
        Assert.InRange(relogged.X, x - 0.5f, x + 0.5f);

        rig.FlyUntilLanded(relogged);
        Assert.Equal((64f, 0f), (relogged.X, relogged.Y));
        await rig.Npcs.RouteWrites!.FlushAsync().WaitAsync(Budget);
        Assert.Null(rig.Store.Saved(rig.CharacterId));
        Assert.Equal(["save 1", "delete 1"], rig.Store.Calls);
    }

    [Fact]
    public async Task AFlightThatWasNeverPersisted_LandsWithoutADatabaseCall()
    {
        await using var rig = await Rig.CreateAsync();
        Player flyer = await rig.LoginAsync();
        Assert.True(rig.Npcs.Flights!.StartFlight(flyer, [1, 2], [10], Gryphon));
        rig.FlyUntilLanded(flyer);

        await rig.Npcs.RouteWrites!.FlushAsync().WaitAsync(Budget);
        Assert.Empty(rig.Store.Calls);
        Assert.Equal(0, rig.Store.Attempts(rig.CharacterId));
    }

    [Fact]
    public async Task LogoutMidFlight_WhenTheRouteCannotBeSaved_ReturnsTheCharacterToTheDepartureNode_AndClearsTheRoute()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Store.FailWrites = true;
        Player flyer = await rig.LoginAsync();
        Assert.True(rig.Npcs.Flights!.StartFlight(flyer, [1, 2], [10], Gryphon));
        rig.World.RunTick(1000);
        Assert.InRange(flyer.X, 20f, 44f);
        rig.World.RemovePlayer(flyer);

        // The save fails every attempt, so the route is given up: the row is deleted (itself retained while the store is
        // down, so the login barrier refuses) and the character is put back at the departure node behind the logout snapshot.
        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(rig.LoadAsync);
        Assert.Contains(rig.CharacterId.ToString(), refused.Message);
        Assert.IsType<IOException>(refused.InnerException);
        Assert.True(rig.Npcs.RouteWrites!.HasRetainedFailure(rig.CharacterId));
        await rig.Saves.FlushCharacterAsync(rig.CharacterId);
        CharacterRecord stored = Assert.IsType<CharacterRecord>(await rig.Characters.GetByIdAsync(rig.CharacterId));
        Assert.Equal((0u, 0f, 0f, Z), (stored.MapId, stored.X, stored.Y, stored.Z));
        Assert.Equal(flyer.Level, stored.Level);
        Assert.Null(rig.Store.Saved(rig.CharacterId));
        Assert.Empty(rig.Store.Calls);

        // The store recovers: the retained delete lands, nothing is staged, and the character stands at the departure node.
        rig.Store.FailWrites = false;
        Player relogged = await rig.LoadAsync();
        Assert.Equal(["delete 1"], rig.Store.Calls);
        Assert.False(rig.Npcs.RouteWrites.HasRetainedFailure(rig.CharacterId));
        rig.Enter(relogged);
        Assert.False(rig.Npcs.Flights.IsFlying(relogged));
        Assert.Equal((0f, 0f, Z), (relogged.X, relogged.Y, relogged.Z));
    }

    [Fact]
    public async Task ALaterLogout_RetriesTheRetainedWrite_WithoutWaitingForLoginOrShutdown()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Store.FailWrites = true;
        Player flyer = await rig.LoginAsync();
        Assert.True(rig.Npcs.Flights!.StartFlight(flyer, [1, 2], [10], Gryphon));
        rig.World.RunTick(1000);
        rig.World.RemovePlayer(flyer);
        await Assert.ThrowsAsync<InvalidOperationException>(rig.LoadAsync);
        Assert.True(rig.Npcs.RouteWrites!.HasRetainedFailure(rig.CharacterId));

        rig.Store.FailWrites = false;
        Player again = await rig.LoginAsync(); // enters without the barrier (an administrative path), not flying
        rig.World.RemovePlayer(again);

        await rig.Npcs.RouteWrites.FlushAsync().WaitAsync(Budget); // an ordered barrier: the retry was queued before it
        Assert.False(rig.Npcs.RouteWrites.HasRetainedFailure(rig.CharacterId));
        Assert.Equal(["delete 1"], rig.Store.Calls);
    }

    [Fact]
    public async Task StopWorldFeatures_DrainsTheRouteWrites_AndNamesACharacterWhoseWriteIsNotDurable()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Store.HoldNextWrite();
        Player flyer = await rig.LoginAsync();
        Assert.True(rig.Npcs.Flights!.StartFlight(flyer, [1, 2], [10], Gryphon));
        rig.World.RunTick(1000);
        rig.World.RemovePlayer(flyer);
        await rig.Store.Entered.Task.WaitAsync(Budget);

        Task stop = rig.Services.StopWorldFeaturesAsync();
        Assert.False(stop.IsCompleted);
        rig.Store.Release.TrySetResult();
        await stop.WaitAsync(Budget);
        Assert.Equal([1u, 2u], Assert.IsType<TaxiFlightRoute>(rig.Store.Saved(rig.CharacterId)).Nodes);

        await using var down = await Rig.CreateAsync();
        down.Store.FailWrites = true;
        Player stranded = await down.LoginAsync();
        Assert.True(down.Npcs.Flights!.StartFlight(stranded, [1, 2], [10], Gryphon));
        down.World.RunTick(1000);
        down.World.RemovePlayer(stranded);

        AggregateException failed = await Assert.ThrowsAsync<AggregateException>(down.Services.StopWorldFeaturesAsync);
        InvalidOperationException inner = Assert.IsType<InvalidOperationException>(Assert.Single(failed.InnerExceptions));
        Assert.Contains(down.CharacterId.ToString(), inner.Message);
        await down.Saves.FlushCharacterAsync(down.CharacterId);
        CharacterRecord stored = Assert.IsType<CharacterRecord>(await down.Characters.GetByIdAsync(down.CharacterId));
        Assert.Equal((0f, 0f), (stored.X, stored.Y));
    }

    /// <summary>
    /// The feature attached to a world whose character save queue is the one the feature resolves (as in the daemon), with
    /// a controllable route store, an in-memory character store, and the creature content that gives the gryphon a display.
    /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly WorldSession _session;

        private Rig(ServiceProvider services, FakeTaxiStore store, InMemoryCharacterStore characters, CharacterSaveQueue saves,
            WorldRuntime world, NpcServicesFeature npcs, int characterId)
        {
            Services = services;
            Store = store;
            Characters = characters;
            Saves = saves;
            World = world;
            Npcs = npcs;
            CharacterId = characterId;
            _session = new WorldSession(new MemoryStream(), "taxi-test", services, WorldServiceCollectionExtensions.BuildOpcodeTable(),
                world, new SessionRegistry(), new WorldSessionOptions(), NullLogger.Instance);
        }

        public ServiceProvider Services { get; }
        public FakeTaxiStore Store { get; }
        public InMemoryCharacterStore Characters { get; }
        public CharacterSaveQueue Saves { get; }
        public WorldRuntime World { get; }
        public NpcServicesFeature Npcs { get; }
        public int CharacterId { get; }

        public static async Task<Rig> CreateAsync()
        {
            var store = new FakeTaxiStore();
            var characters = new InMemoryCharacterStore();
            CharacterRecord record = await characters.CreateAsync(new CharacterRecord
            {
                AccountId = 1, Name = "Flyer", Race = (byte)Race.Human, Class = (byte)Class.Warrior, Gender = (byte)Gender.Male,
                Level = 1, MapId = 0, ZoneId = 12, X = 0, Y = 0, Z = Z,
            });
            ServiceProvider services = new ServiceCollection()
                .AddSingleton<ICharacterTaxiFlightStore>(store)
                .AddSingleton<ICharacterStore>(characters)
                .AddSingleton(PathNodes)
                .AddSingleton(sp => new CharacterSaveQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance))
                .AddSingleton<ICharacterSaveQueue>(sp => sp.GetRequiredService<CharacterSaveQueue>())
                .AddSingleton(sp =>
                {
                    var creatures = new CreatureWorldFeature(sp, NullLogger<CreatureWorldFeature>.Instance);
                    creatures.Install(new CreatureContent(
                        [new CreatureTemplate { Entry = Gryphon, Name = "Gryphon", DisplayIds = [GryphonDisplay] }], [], [], [], []));
                    return creatures;
                })
                .AddSingleton(sp => new NpcServicesFeature(sp, NullLogger<NpcServicesFeature>.Instance))
                .AddSingleton<IWorldFeature>(sp => sp.GetRequiredService<NpcServicesFeature>())
                .BuildServiceProvider();
            CharacterSaveQueue saves = services.GetRequiredService<CharacterSaveQueue>();
            saves.Start();
            var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves, NullLogger<WorldRuntime>.Instance);
            NpcServicesFeature npcs = services.GetRequiredService<NpcServicesFeature>();
            npcs.Attach(world);
            npcs.Extend(new QuestNpcDependencies(), new NpcStore(TaxiContent));
            return new Rig(services, store, characters, saves, world, npcs, record.Id);
        }

        /// <summary>A session enters the world straight from the stored record (no login barrier, nothing staged).</summary>
        public async Task<Player> LoginAsync()
        {
            Player player = await NewPlayerAsync();
            Enter(player);
            return player;
        }

        /// <summary>The login path's loading phase: the save barrier, the fresh record, then the feature's hook (which may fault).</summary>
        public async Task<Player> LoadAsync()
        {
            await Saves.FlushCharacterAsync(CharacterId);
            Player player = await NewPlayerAsync();
            await Npcs.OnPlayerLoadingAsync(_session, await RecordAsync(), player);
            return player;
        }

        public void Enter(Player player)
        {
            World.AddPlayer(player);
            World.RunTick(0);
            World.NotifyLoggedIn(player);
        }

        public void FlyUntilLanded(Player player)
        {
            for (int i = 0; i < 10 && Npcs.Flights!.IsFlying(player); i++)
            {
                World.RunTick(1000);
            }

            Assert.False(Npcs.Flights!.IsFlying(player));
        }

        public async ValueTask DisposeAsync()
        {
            Store.FailWrites = false;
            Store.Release.TrySetResult();
            try
            {
                await Npcs.StopAsync();
            }
            catch (InvalidOperationException)
            {
                // a test that proves the shutdown failure leaves the failure cached
            }
            catch (AggregateException)
            {
                // StopWorldFeaturesAsync already reported it
            }

            await Saves.StopAsync();
            Npcs.Dispose();
            World.Dispose();
            await Services.DisposeAsync();
        }

        private async Task<CharacterRecord> RecordAsync()
            => await Characters.GetByIdAsync(CharacterId) ?? throw new InvalidOperationException("the character row is gone");

        private async Task<Player> NewPlayerAsync()
        {
            var appearance = new PlayerAppearance(
                DisplayId: 49, FactionTemplate: 1, PowerType.Rage, BaseHealth: 60, BaseMana: 0,
                MaxHealth: 60, MaxPower: 1000, StartPower: 0, NextLevelXp: 400);
            return new Player(await RecordAsync(), appearance, new NullSession());
        }
    }

    private sealed class NullSession : IPlayerSession
    {
        public int AccountId => 1;

        public AccountSecurity Security => AccountSecurity.Player;

        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload)
        {
        }

        public void ProcessWorldPackets(Player player)
        {
        }

        public void Kick()
        {
        }

        public void OnLoggedOut()
        {
        }
    }

    private sealed class FakeTaxiStore : ICharacterTaxiFlightStore
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<int, TaxiFlightRoute> _rows = [];
        private readonly Dictionary<int, int> _attempts = [];
        private readonly List<string> _calls = [];
        private int _hold;

        public volatile bool FailWrites;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Successful writes in order.</summary>
        public string[] Calls
        {
            get
            {
                lock (_gate)
                {
                    return [.. _calls];
                }
            }
        }

        public void HoldNextWrite() => Volatile.Write(ref _hold, 1);

        public int Attempts(int id)
        {
            lock (_gate)
            {
                return _attempts.GetValueOrDefault(id);
            }
        }

        public TaxiFlightRoute? Saved(int id)
        {
            lock (_gate)
            {
                return _rows.GetValueOrDefault(id);
            }
        }

        public Task<TaxiFlightRoute?> LoadAsync(int characterId, CancellationToken cancellationToken = default) => Task.FromResult(Saved(characterId));

        public async Task SaveAsync(int characterId, TaxiFlightRoute route, CancellationToken cancellationToken = default)
        {
            await BeforeAsync(characterId);
            lock (_gate)
            {
                _rows[characterId] = route;
                _calls.Add($"save {characterId}");
            }
        }

        public async Task DeleteAsync(int characterId, CancellationToken cancellationToken = default)
        {
            await BeforeAsync(characterId);
            lock (_gate)
            {
                _rows.Remove(characterId);
                _calls.Add($"delete {characterId}");
            }
        }

        private async Task BeforeAsync(int id)
        {
            lock (_gate)
            {
                _attempts[id] = _attempts.GetValueOrDefault(id) + 1;
            }

            if (Interlocked.Exchange(ref _hold, 0) == 1)
            {
                Entered.TrySetResult();
                await Release.Task.ConfigureAwait(false);
            }

            if (FailWrites)
            {
                throw new IOException("controlled taxi route storage failure");
            }
        }
    }
}

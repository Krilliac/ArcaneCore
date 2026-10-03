using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using ArcaneCore.Data;
using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Items;
using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Data.Loot;
using ArcaneCore.Data.Stores;
using ArcaneCore.Data.World.GameObjects;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Loot;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using ArcaneCore.Realm.Net;
using ArcaneCore.World;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

/// <summary>
/// Durable chest loot of a dungeon instance end to end: real SRP/world sessions over loopback,
/// the real SQLite stores (characters, instances, chest loot, content tables) and a real world
/// restart. A partial take is committed with its award in one transaction, and the freshly
/// started world rebuilds the exact remaining contents of the bound instance's chest.
/// </summary>
public sealed class InstanceLootPersistenceTests : IDisposable
{
    private const string AccountName = "INSTLOOT";
    private const string Password = "PASSWORD";
    private const string CharacterName = "Lootrestart";
    private const uint Dungeon = 36;
    private const uint DungeonTrigger = 78;
    private const uint ChestEntry = 990301;
    private const uint ChestSpawn = 77201;
    private const uint Cloth = 2589;
    private const uint Silk = 4306;
    private readonly OwnedFixtureDirectory _directory = OwnedFixtureDirectory.Create();

    [Fact]
    public async Task PartialChest_SurvivesAWorldRestartOnRealSqlite_WithItsAwardStoredAtomically()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        CancellationToken token = deadline.Token;
        ClientFixtureResult fixture = await ClientFixture.PrepareAsync(
            new ClientFixtureOptions(Path.Combine(_directory.Path, "persisted"), AccountName, Password), token);
        await SeedContentAsync(fixture, token);

        ulong character;
        LootStateKey key;
        LootStateRecord afterFirstTake;
        await using (PersistentHost first = await PersistentHost.StartAsync(fixture, token))
        {
            await using WorldClient client = await AuthenticateAsync(first, token);
            var connection = new ScenarioConnection(client);
            await connection.CreateCharacterAsync(CharacterName, token);
            character = Assert.Single(await connection.EnumerateAsync(token)).Guid;
            await connection.LoginAsync(character, token);
            uint instance = await EnterDungeonAsync(first, connection, character, token);
            key = new LootStateKey(instance, ChestSpawn);

            await connection.SendAsync(WorldOpcode.CmsgGameobjUse, BitConverter.GetBytes(ChestGuid()), token);
            Assert.Equal([(0, Cloth, 2u), (1, Silk, 1u)], Window(await connection.ReadUntilAsync(WorldOpcode.SmsgLootResponse, token)));
            await connection.SendAsync(WorldOpcode.CmsgAutostoreLootItem, [0], token);
            Assert.Equal([0], await connection.ReadUntilAsync(WorldOpcode.SmsgLootRemoved, token));
            await connection.SendAsync(WorldOpcode.CmsgLootRelease, BitConverter.GetBytes(ChestGuid()), token);
            await connection.ReadUntilAsync(WorldOpcode.SmsgLootReleaseResponse, token);

            // Committed before the client saw the removal: the stored chest and the stored inventory agree.
            afterFirstTake = Assert.Single(await LoadChestsAsync(first, token));
            Assert.Equal(key, afterFirstTake.Key);
            Assert.Equal(1u, afterFirstTake.Generation);
            Assert.False(afterFirstTake.Consumed);
            Assert.True(afterFirstTake.Items.Single(i => i.Slot == 0).IsLooted);
            Assert.False(afterFirstTake.Items.Single(i => i.Slot == 1).IsLooted);
            Assert.Equal(2u, await StoredCountAsync(first, character, Cloth, token));
            Assert.Equal(0u, await StoredCountAsync(first, character, Silk, token));

            // A normal world stop saves the online character (inside the instance) and drains the queues.
            await first.StopWorldAsync(token);
        }

        await using PersistentHost restarted = await PersistentHost.StartAsync(fixture, token);
        Assert.Equal(afterFirstTake, Assert.Single(await LoadChestsAsync(restarted, token)));
        await using WorldClient freshClient = await AuthenticateAsync(restarted, token);
        var fresh = new ScenarioConnection(freshClient);
        Assert.Equal(character, Assert.Single(await fresh.EnumerateAsync(token)).Guid);
        await fresh.LoginAsync(character, token); // the saved position is inside the bound instance
        Assert.Equal((Dungeon, key.InstanceId), await restarted.World.InvokeAsync(() =>
        {
            Player player = restarted.World.FindOnlinePlayer(ObjectGuid.Player(checked((uint)character)))!;
            return (player.Map!.MapId, player.Map.InstanceId);
        }).WaitAsync(token));

        await fresh.SendAsync(WorldOpcode.CmsgGameobjUse, BitConverter.GetBytes(ChestGuid()), token);
        Assert.Equal([(1, Silk, 1u)], Window(await fresh.ReadUntilAsync(WorldOpcode.SmsgLootResponse, token)));
        await fresh.SendAsync(WorldOpcode.CmsgAutostoreLootItem, [0], token); // taken before the restart: nothing happens
        await fresh.SendAsync(WorldOpcode.CmsgAutostoreLootItem, [1], token);
        Assert.Equal([1], await fresh.ReadUntilAsync(WorldOpcode.SmsgLootRemoved, token));
        await fresh.SendAsync(WorldOpcode.CmsgLootRelease, BitConverter.GetBytes(ChestGuid()), token);
        await fresh.ReadUntilAsync(WorldOpcode.SmsgLootReleaseResponse, token);

        LootStateRecord consumed = Assert.Single(await LoadChestsAsync(restarted, token));
        Assert.True(consumed.Consumed);
        Assert.Equal(1u, consumed.Generation); // never generated twice
        Assert.True(consumed.RespawnAtUnix > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Assert.Equal(2u, await StoredCountAsync(restarted, character, Cloth, token));
        Assert.Equal(1u, await StoredCountAsync(restarted, character, Silk, token));
    }

    public void Dispose() => _directory.Delete();

    private static ulong ChestGuid() => ObjectGuid.WithEntry(HighGuid.GameObject, ChestEntry, ChestSpawn).Value;

    private static List<(int Slot, uint Item, uint Count)> Window(byte[] payload)
    {
        var reader = new PacketReader(payload);
        reader.ReadUInt64(); // source
        reader.ReadByte();   // type
        reader.ReadUInt32(); // gold
        byte count = reader.ReadByte();
        var items = new List<(int, uint, uint)>();
        for (int i = 0; i < count; i++)
        {
            byte slot = reader.ReadByte();
            uint item = reader.ReadUInt32();
            uint stack = reader.ReadUInt32();
            reader.Skip(4 + 4 + 4 + 1); // display, random suffix, random property, slot type
            items.Add((slot, item, stack));
        }

        return items;
    }

    /// <summary>The Deadmines area trigger, entered through the real teleport and worldport flow; returns the instance id.</summary>
    private static async Task<uint> EnterDungeonAsync(PersistentHost host, ScenarioConnection connection, ulong guid, CancellationToken token)
    {
        ObjectGuid player = ObjectGuid.Player(checked((uint)guid));
        await host.World.InvokeAsync(() =>
        {
            Player online = host.World.FindOnlinePlayer(player)!;
            online.Level = 20;
            online.Relocate(-8962f, -130f, 84f, online.Orientation, host.World.NowMs);
            return true;
        }).WaitAsync(token);
        byte[] trigger = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(trigger, DungeonTrigger);
        await connection.SendAsync(WorldOpcode.CmsgAreatrigger, trigger, token);
        Assert.Equal(Dungeon, BinaryPrimitives.ReadUInt32LittleEndian(await connection.ReadUntilAsync(WorldOpcode.SmsgTransferPending, token)));
        Assert.Equal(Dungeon, BinaryPrimitives.ReadUInt32LittleEndian(await connection.ReadUntilAsync(WorldOpcode.SmsgNewWorld, token)));
        await connection.SendAsync(WorldOpcode.MsgMoveWorldportAck, [], token);
        await connection.ReadUntilAsync(WorldOpcode.SmsgInitWorldStates, token);
        return await host.World.InvokeAsync(() => host.World.FindOnlinePlayer(player)!.Map is { MapId: Dungeon } map
            ? map.InstanceId : throw new InvalidOperationException("the player did not enter the dungeon")).WaitAsync(token);
    }

    private static async Task<IReadOnlyList<LootStateRecord>> LoadChestsAsync(PersistentHost host, CancellationToken token)
    {
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        return await new EfLootStateStore(scope.ServiceProvider.GetRequiredService<CharacterDbContext>()).LoadInstanceStatesAsync(token);
    }

    private static async Task<uint> StoredCountAsync(PersistentHost host, ulong character, uint entry, CancellationToken token)
    {
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        var items = new EfItemStore(scope.ServiceProvider.GetRequiredService<CharacterDbContext>());
        return (uint)(await items.GetInventoryAsync(checked((int)character), token)).Where(r => r.Item.Entry == entry).Sum(r => (long)r.Item.Count);
    }

    private static async Task<WorldClient> AuthenticateAsync(PersistentHost host, CancellationToken token)
    {
        LogonResult logon = await LogonClient.AuthenticateAsync(host.RealmEndpoint, AccountName, Password, token);
        WorldClient client = await WorldClient.ConnectAsync(host.WorldEndpoint, token);
        try
        {
            Assert.Equal(0x0C, await client.AuthenticateAsync(AccountName, logon.SessionKey, token));
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    /// <summary>The dungeon, its entrance trigger, the chest and its loot, and the two item templates, in the world database.</summary>
    private static async Task SeedContentAsync(ClientFixtureResult fixture, CancellationToken token)
    {
        IConfigurationRoot config = new ConfigurationBuilder().AddJsonFile(fixture.ConfigurationPath).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddWorldDatabase(config);
        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        WorldDbContext db = scope.ServiceProvider.GetRequiredService<WorldDbContext>();
        db.Set<MapTemplateRow>().AddRange(
            new MapTemplateRow { Entry = 0, MapType = (byte)MapType.Common, MapName = "Eastern Kingdoms" },
            new MapTemplateRow { Entry = Dungeon, MapType = (byte)MapType.Instance, PlayerLimit = 10, MapName = "Deadmines" });
        db.Set<AreaTriggerTemplateRow>().Add(new AreaTriggerTemplateRow
        {
            Id = DungeonTrigger, Name = "Test dungeon entrance", MapId = 0, X = -8960f, Y = -132.5f, Z = 83.5f,
            BoxX = 10, BoxY = 10, BoxZ = 10,
        });
        db.Set<AreaTriggerTeleportRow>().Add(new AreaTriggerTeleportRow
        {
            Id = DungeonTrigger, Name = "Deadmines Entrance", RequiredLevel = 10, TargetMap = Dungeon,
            TargetPositionX = -16.4f, TargetPositionY = -383.07f, TargetPositionZ = 61.78f, TargetOrientation = 1.86f,
        });
        db.Set<GameObjectTemplateRow>().Add(new GameObjectTemplateRow
        {
            Entry = ChestEntry, Type = (uint)ArcaneCore.Game.GameObjects.GameObjectType.Chest, DisplayId = 10, Name = "Durable chest", Data1 = ChestEntry,
        });
        db.Set<GameObjectSpawnRow>().Add(new GameObjectSpawnRow
        {
            Guid = ChestSpawn, Entry = ChestEntry, MapId = Dungeon, X = -14f, Y = -383.07f, Z = 61.78f, SpawnTimeSeconds = 600,
        });
        db.Set<GameObjectLootTemplateRow>().AddRange(
            new GameObjectLootTemplateRow { Entry = ChestEntry, Item = Cloth, ChanceOrQuestChance = 100, MinCountOrRef = 2, MaxCount = 2 },
            new GameObjectLootTemplateRow { Entry = ChestEntry, Item = Silk, ChanceOrQuestChance = 100, MinCountOrRef = 1, MaxCount = 1 });
        db.Set<ItemTemplateRow>().AddRange(
            new ItemTemplateRow { Entry = Cloth, Class = 7, Name = "Linen Cloth", DisplayId = 3776, Quality = 1, AllowableClass = -1, AllowableRace = -1, Stackable = 20 },
            new ItemTemplateRow { Entry = Silk, Class = 7, Name = "Silk Cloth", DisplayId = 3777, Quality = 1, AllowableClass = -1, AllowableRace = -1, Stackable = 20 });
        await db.SaveChangesAsync(token);
    }

    /// <summary>A fresh normal daemon composition over the fixture's SQLite files; only its listeners are owned ephemeral sockets.</summary>
    private sealed class PersistentHost : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly WorldHost _host;
        private readonly TcpListener _realm;
        private readonly TcpListener _world;
        private readonly CancellationTokenSource _stop = new();
        private readonly object _gate = new();
        private readonly HashSet<TcpClient> _clients = [];
        private readonly List<Task> _sessions = [];
        private Task _realmAccept = Task.CompletedTask;
        private Task _worldAccept = Task.CompletedTask;
        private Task? _worldStop;

        private PersistentHost(ServiceProvider services, TcpListener realm, TcpListener world)
        {
            _services = services;
            _realm = realm;
            _world = world;
            World = services.GetRequiredService<WorldRuntime>();
            _host = services.GetServices<IHostedService>().OfType<WorldHost>().Single();
        }

        internal IServiceProvider Services => _services;
        internal WorldRuntime World { get; }
        internal IPEndPoint RealmEndpoint => (IPEndPoint)_realm.LocalEndpoint;
        internal IPEndPoint WorldEndpoint => (IPEndPoint)_world.LocalEndpoint;

        internal static async Task<PersistentHost> StartAsync(ClientFixtureResult fixture, CancellationToken token)
        {
            var realm = new TcpListener(IPAddress.Loopback, 0);
            var world = new TcpListener(IPAddress.Loopback, 0);
            ServiceProvider? provider = null;
            PersistentHost? result = null;
            try
            {
                realm.Start();
                world.Start();
                IConfigurationRoot config = new ConfigurationBuilder().AddJsonFile(fixture.ConfigurationPath)
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["World:TickIntervalMs"] = "5", ["World:AutosaveIntervalMs"] = "0",
                        ["World:UpdateCompressionThreshold"] = "0", ["World:InstantLogoutSecurity"] = "Moderator",
                        ["World:LogoutDelayMs"] = "250",
                    }).Build();
                var services = new ServiceCollection();
                services.AddLogging();
                services.AddSingleton<IConfiguration>(_ => config);
                services.Configure<AuthOptions>(config.GetSection(AuthOptions.SectionName));
                services.AddAuthDatabase(config).AddCharacterDatabase(config).AddWorldDatabase(config);
                services.AddWorldDaemon(config);
                services.Remove(services.Single(descriptor => descriptor.ServiceType == typeof(IHostedService)
                    && descriptor.ImplementationType == typeof(WorldServer)));
                provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
                result = new PersistentHost(provider, realm, world);
                await using (AsyncServiceScope scope = provider.CreateAsyncScope())
                {
                    // Change only this owned synthetic database's advertised test listener.
                    AuthDbContext auth = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
                    (await auth.Realms.SingleAsync(token)).Address = result.WorldEndpoint.ToString();
                    await auth.SaveChangesAsync(token);
                }

                await result._host.StartAsync(token);
                await provider.GetRequiredService<SocialFeature>().GuildsLoaded.WaitAsync(token);
                await result.World.InvokeAsync(() => true).WaitAsync(token);
                result._realmAccept = result.AcceptAsync(realm, isRealm: true);
                result._worldAccept = result.AcceptAsync(world, isRealm: false);
                return result;
            }
            catch
            {
                if (result is not null)
                {
                    await result.DisposeAsync();
                }
                else
                {
                    realm.Stop();
                    world.Stop();
                    if (provider is not null)
                    {
                        await provider.DisposeAsync();
                    }
                }

                throw;
            }
        }

        internal async Task StopWorldAsync(CancellationToken token)
        {
            _worldStop ??= _host.StopAsync(CancellationToken.None);
            await _worldStop.WaitAsync(token);
        }

        private async Task AcceptAsync(TcpListener listener, bool isRealm)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client = await listener.AcceptTcpClientAsync(_stop.Token);
                    lock (_gate)
                    {
                        if (_stop.IsCancellationRequested)
                        {
                            client.Dispose();
                            return;
                        }

                        _clients.Add(client);
                        _sessions.Add(Task.Run(() => RunSessionAsync(client, isRealm), CancellationToken.None));
                    }
                }
            }
            catch (Exception ex) when (_stop.IsCancellationRequested
                && ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }

        private async Task RunSessionAsync(TcpClient client, bool isRealm)
        {
            try
            {
                client.NoDelay = true;
                string endpoint = client.Client.RemoteEndPoint?.ToString() ?? "owned-loopback";
                using (client)
                await using (NetworkStream stream = client.GetStream())
                await using (AsyncServiceScope scope = _services.CreateAsyncScope())
                {
                    IServiceProvider services = scope.ServiceProvider;
                    if (isRealm)
                    {
                        var session = new LogonSession(stream, services.GetRequiredService<IAccountStore>(),
                            services.GetRequiredService<IRealmStore>(), services.GetRequiredService<IOptions<AuthOptions>>().Value,
                            NullLogger<LogonSession>.Instance, endpoint);
                        await session.RunAsync(_stop.Token);
                    }
                    else
                    {
                        var session = new WorldSession(stream, endpoint, services, services.GetRequiredService<OpcodeTable>(),
                            World, services.GetRequiredService<SessionRegistry>(), services.GetRequiredService<IOptions<WorldSessionOptions>>().Value,
                            NullLogger<WorldSession>.Instance);
                        await session.RunAsync(_stop.Token);
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or EndOfStreamException or IOException or SocketException
                || (_stop.IsCancellationRequested && ex is ObjectDisposedException))
            {
            }
            finally
            {
                client.Dispose();
                lock (_gate)
                {
                    _clients.Remove(client);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _realm.Stop();
            _world.Stop();
            TcpClient[] clients;
            lock (_gate)
            {
                clients = [.. _clients];
            }

            foreach (TcpClient client in clients)
            {
                client.Dispose();
            }

            try
            {
                await Task.WhenAll(_realmAccept, _worldAccept);
                Task[] sessions;
                lock (_gate)
                {
                    sessions = [.. _sessions];
                }

                await Task.WhenAll(sessions);
            }
            finally
            {
                try
                {
                    await StopWorldAsync(CancellationToken.None);
                }
                finally
                {
                    await _services.DisposeAsync();
                    _stop.Dispose();
                }
            }
        }
    }
}

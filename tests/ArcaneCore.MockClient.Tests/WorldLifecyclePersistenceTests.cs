using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using ArcaneCore.Data;
using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Stores;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using ArcaneCore.Realm.Net;
using ArcaneCore.World;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Spells;

namespace ArcaneCore.MockClient.Tests;

/// <summary>
/// Actual SRP/world sessions and persistent SQLite stores exercise logout and cold world startup.
/// The ordinary logout delay is shortened only in these tests; this is not real-client acceptance.
/// </summary>
public sealed partial class WorldLifecyclePersistenceTests : IDisposable
{
    private const string AccountName = "LIFECYCLE";
    private const string Password = "PASSWORD";
    private const string CharacterName = "Lifehero";
    private const byte ActionSlot = 7;
    private const uint Action = 117;
    private readonly OwnedFixtureDirectory _directory = OwnedFixtureDirectory.Create();

    [Fact]
    public async Task CountdownLogout_FreshSessionWaitsForRealCoreSaveAndReloadsPositionIdentityAndActionBar()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        CancellationToken token = deadline.Token;
        ClientFixtureResult fixture = await PrepareAsync(token);
        var saves = new SaveControl();
        await using PersistentHost host = await PersistentHost.StartAsync(fixture, saves, token);
        await using WorldClient original = await AuthenticateAsync(host, token);
        var first = new ScenarioConnection(original);
        await first.CreateCharacterAsync(CharacterName, token);
        MockCharacter character = Assert.Single(await first.EnumerateAsync(token));
        MockLogin initial = await first.LoginAsync(character.Guid, token);
        Player oldPlayer = await host.World.InvokeAsync(() => host.World.FindOnlinePlayer(GuidOf(character.Guid))!).WaitAsync(token);
        MockLocation destination = Destination(character);
        await ChangeStateThroughWireAsync(host, first, character.Guid, destination, token);
        int id = checked((int)character.Guid);
        saves.ArmSave(id);
        Task<MockLogin>? login = null;
        try
        {
            await first.SendAsync(WorldOpcode.CmsgLogoutRequest, [], token);
            // Last byte zero distinguishes a real ordinary countdown from the instant path.
            Assert.Equal(new byte[] { 0, 0, 0, 0, 0 }, await first.ReadUntilAsync(WorldOpcode.SmsgLogoutResponse, token));
            Assert.Empty(await first.ReadUntilAsync(WorldOpcode.SmsgLogoutComplete, token));
            CharacterState snapshot = await saves.SaveEntered.Task.WaitAsync(token);
            AssertSnapshot(snapshot, id, destination);
            Assert.False(host.World.IsOnline(GuidOf(character.Guid)));
            Assert.Equal(0, saves.CompletedSaves);
            await AssertStoredAsync(host, character, initial.Location, changedAction: false, token);
            await original.DisposeAsync();

            await using WorldClient replacement = await AuthenticateAsync(host, token);
            var next = new ScenarioConnection(replacement);
            Assert.Equal(character.Guid, Assert.Single(await next.EnumerateAsync(token)).Guid);
            saves.ObserveLoginReads();
            login = next.LoginAsync(character.Guid, token);
            await saves.OwnershipRead.Task.WaitAsync(token);
            // The handler has fetched the ownership row. Its fresh row/read and publication
            // must remain behind the queued logout write, rather than using this stale row.
            await Task.Delay(150, token);
            Assert.False(login.IsCompleted);
            Assert.Equal(1, saves.LoginReads);
            Assert.Equal(0, host.World.OnlinePlayerCount);
            saves.Release();

            MockLogin reloaded = await login.WaitAsync(token);
            await host.Services.GetRequiredService<CharacterSaveQueue>().FlushCharacterAsync(id, token);
            Assert.Equal(2, saves.LoginReads);
            Assert.Equal(1, saves.CompletedSaves);
            AssertLogin(reloaded, character.Guid, destination);
            await AssertLiveAsync(host, character, destination, oldPlayer, token);
            await AssertStoredAsync(host, character, destination, changedAction: true, token);
        }
        finally
        {
            saves.Release();
            if (login is not null)
            {
                // Observe the owned reader even if an assertion fails while the save is held.
                try { await login.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException) { }
            }
        }
    }

    [Fact]
    public async Task NormalWorldStop_NewServiceProviderAndRuntimeReloadSavedPositionIdentityAndActionBar()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        CancellationToken token = deadline.Token;
        ClientFixtureResult fixture = await PrepareAsync(token);
        var saves = new SaveControl();
        MockCharacter character;
        MockLocation destination;
        WorldRuntime oldWorld;
        Player oldPlayer;
        await using (PersistentHost firstHost = await PersistentHost.StartAsync(fixture, saves, token))
        {
            oldWorld = firstHost.World;
            await using WorldClient client = await AuthenticateAsync(firstHost, token);
            var connection = new ScenarioConnection(client);
            await connection.CreateCharacterAsync(CharacterName, token);
            character = Assert.Single(await connection.EnumerateAsync(token));
            await connection.LoginAsync(character.Guid, token);
            oldPlayer = await firstHost.World.InvokeAsync(() => firstHost.World.FindOnlinePlayer(GuidOf(character.Guid))!).WaitAsync(token);
            destination = Destination(character);
            await ChangeStateThroughWireAsync(firstHost, connection, character.Guid, destination, token);
            Assert.Equal(0, saves.CompletedSaves);
            // Keep the session connected: WorldHost.StopAsync captures online state and
            // drains its real save queue before any provider or session is disposed.
            await firstHost.StopWorldAsync(token);
            Assert.Equal(1, saves.CompletedSaves);
            await AssertStoredAsync(firstHost, character, destination, changedAction: true, token);
        }

        await using PersistentHost restarted = await PersistentHost.StartAsync(fixture, new SaveControl(), token);
        Assert.NotSame(oldWorld, restarted.World);
        Assert.Equal(0, restarted.World.OnlinePlayerCount);
        await using WorldClient freshClient = await AuthenticateAsync(restarted, token);
        var fresh = new ScenarioConnection(freshClient);
        MockCharacter persisted = Assert.Single(await fresh.EnumerateAsync(token));
        Assert.Equal(character.Guid, persisted.Guid);
        Assert.Equal((character.Name, character.Race, character.Class, character.Gender),
            (persisted.Name, persisted.Race, persisted.Class, persisted.Gender));
        Assert.Equal((destination.Map, destination.X, destination.Y, destination.Z),
            (persisted.Map, persisted.X, persisted.Y, persisted.Z));
        MockLogin login = await fresh.LoginAsync(character.Guid, token);
        AssertLogin(login, character.Guid, destination);
        await AssertLiveAsync(restarted, character, destination, oldPlayer, token);
        await AssertStoredAsync(restarted, character, destination, changedAction: true, token);
    }

    public void Dispose() => _directory.Delete();

    [Fact]
    public async Task ColdStartupPreservesGhostBodyButDoesNotRestoreThePreviousSelfResOffer()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        CancellationToken token = deadline.Token;
        ClientFixtureResult fixture = await PrepareAsync(token);
        MockCharacter character;
        await using (PersistentHost firstHost = await PersistentHost.StartAsync(fixture, new SaveControl(), token))
        {
            await using WorldClient client = await AuthenticateAsync(firstHost, token);
            var first = new ScenarioConnection(client);
            await first.CreateCharacterAsync(CharacterName, token);
            character = Assert.Single(await first.EnumerateAsync(token));
            await first.LoginAsync(character.Guid, token);
            await firstHost.World.InvokeAsync(() =>
            {
                Player player = firstHost.World.FindOnlinePlayer(GuidOf(character.Guid))!;
                player.Map!.Combat.Kill(null, player);
                player.SetUInt32(ArcaneCore.Game.UpdateFields.PlayerSelfResSpell, 23700);
                Assert.True(player.Map!.Combat.RepopPlayer(player));
                return true;
            }).WaitAsync(token);
            await firstHost.StopWorldAsync(token);
        }

        await using PersistentHost restarted = await PersistentHost.StartAsync(fixture, new SaveControl(), token);
        await using WorldClient freshClient = await AuthenticateAsync(restarted, token);
        var next = new ScenarioConnection(freshClient);
        await next.LoginAsync(character.Guid, token);
        await restarted.World.InvokeAsync(() =>
        {
            Player player = restarted.World.FindOnlinePlayer(GuidOf(character.Guid))!;
            Assert.False(player.IsAlive);
            Assert.NotNull(player.Combat.Corpse);
            Assert.Equal(0u, player.GetUInt32(ArcaneCore.Game.UpdateFields.PlayerSelfResSpell));
            Assert.False(restarted.Services.GetRequiredService<SpellFeature>().System.TrySelfResurrect(player));
            return true;
        }).WaitAsync(token);
    }

    private Task<ClientFixtureResult> PrepareAsync(CancellationToken token)
        => ClientFixture.PrepareAsync(new ClientFixtureOptions(Path.Combine(_directory.Path, "persisted"), AccountName, Password), token);

    private static ObjectGuid GuidOf(ulong guid) => ObjectGuid.Player(checked((uint)guid));

    private static MockLocation Destination(MockCharacter character)
        => new(character.Map, character.X + 32f, character.Y - 17f, character.Z + 1f, 1.25f);

    private static async Task<WorldClient> AuthenticateAsync(PersistentHost host, CancellationToken token)
    {
        LogonResult logon = await LogonClient.AuthenticateAsync(host.RealmEndpoint, AccountName, Password, token);
        Assert.Equal(host.WorldEndpoint.ToString(), Assert.Single(logon.Realms).Address);
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

    private static async Task ChangeStateThroughWireAsync(PersistentHost host, ScenarioConnection connection,
        ulong guid, MockLocation location, CancellationToken token)
    {
        // Unflagged vanilla MovementInfo: flags, time, four floats, fall time.
        byte[] movement = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(movement.AsSpan(4), 100);
        BinaryPrimitives.WriteSingleLittleEndian(movement.AsSpan(8), location.X);
        BinaryPrimitives.WriteSingleLittleEndian(movement.AsSpan(12), location.Y);
        BinaryPrimitives.WriteSingleLittleEndian(movement.AsSpan(16), location.Z);
        BinaryPrimitives.WriteSingleLittleEndian(movement.AsSpan(20), location.Orientation);
        await connection.SendAsync(WorldOpcode.MsgMoveStop, movement, token);
        byte[] action = new byte[5];
        action[0] = ActionSlot;
        BinaryPrimitives.WriteUInt32LittleEndian(action.AsSpan(1), Action);
        await connection.SendAsync(WorldOpcode.CmsgSetActionButton, action, token);
        while (!await host.World.InvokeAsync(() =>
        {
            Player? player = host.World.FindOnlinePlayer(GuidOf(guid));
            return player is not null && player.X == location.X && player.Y == location.Y
                && player.Z == location.Z && player.Orientation == location.Orientation
                && player.ActionButtons[ActionSlot] == Action;
        }).WaitAsync(token))
        {
            await Task.Delay(10, token);
        }
    }

    private static void AssertSnapshot(CharacterState state, int id, MockLocation expected)
    {
        Assert.Equal(id, state.Id);
        Assert.Equal((expected.Map, expected.X, expected.Y, expected.Z, expected.Orientation),
            (state.MapId, state.X, state.Y, state.Z, state.Orientation));
        Assert.NotNull(state.ActionButtons);
        Assert.Contains(new ActionButton(ActionSlot, Action, 0), state.ActionButtons!);
    }

    private static void AssertLogin(MockLogin login, ulong guid, MockLocation expected)
    {
        Assert.Equal(expected, login.Location);
        Assert.Equal(guid, login.Self.Guid);
        Assert.Equal((expected.X, expected.Y, expected.Z, expected.Orientation),
            (login.Self.X, login.Self.Y, login.Self.Z, login.Self.Orientation));
    }

    private static async Task AssertLiveAsync(PersistentHost host, MockCharacter character, MockLocation expected,
        Player oldPlayer, CancellationToken token)
    {
        await host.World.InvokeAsync(() =>
        {
            Player player = Assert.IsType<Player>(host.World.FindOnlinePlayer(GuidOf(character.Guid)));
            Assert.NotSame(oldPlayer, player);
            Assert.Equal(character.Name, player.Name);
            Assert.Equal(oldPlayer.AccountId, player.AccountId);
            Assert.Equal((expected.Map, expected.X, expected.Y, expected.Z, expected.Orientation),
                (player.MapId, player.X, player.Y, player.Z, player.Orientation));
            Assert.Equal(Action, player.ActionButtons[ActionSlot]);
            Assert.False(player.IsLoggingOut);
            return true;
        }).WaitAsync(token);
    }

    private static async Task AssertStoredAsync(PersistentHost host, MockCharacter character, MockLocation expected,
        bool changedAction, CancellationToken token)
    {
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        // Bypass the decorator: every durability assertion is a fresh provider database read.
        var actual = new EfCharacterStore(db);
        CharacterRecord row = Assert.IsType<CharacterRecord>(await actual.GetByIdAsync(checked((int)character.Guid), token));
        Assert.Equal((character.Name, character.Race, character.Class, character.Gender),
            (row.Name, row.Race, row.Class, row.Gender));
        Assert.Equal((expected.Map, expected.X, expected.Y, expected.Z, expected.Orientation),
            (row.MapId, row.X, row.Y, row.Z, row.Orientation));
        IReadOnlyList<ActionButton> actions = await actual.GetActionButtonsAsync(row.Id, token);
        if (changedAction) Assert.Contains(new ActionButton(ActionSlot, Action, 0), actions);
        else Assert.DoesNotContain(actions, action => action.Button == ActionSlot && action.Action == Action);
    }

    private sealed class SaveControl
    {
        private int _heldCharacter;
        private int _observeReads;
        private int _completedSaves;
        private int _loginReads;
        internal readonly TaskCompletionSource<CharacterState> SaveEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource OwnershipRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int CompletedSaves => Volatile.Read(ref _completedSaves);
        internal int LoginReads => Volatile.Read(ref _loginReads);
        internal void ArmSave(int id) => Volatile.Write(ref _heldCharacter, id);
        internal void ObserveLoginReads() => Volatile.Write(ref _observeReads, 1);
        internal void Release() => _release.TrySetResult();

        internal async Task SaveAsync(ICharacterStore inner, CharacterState state, CancellationToken token)
        {
            if (Interlocked.CompareExchange(ref _heldCharacter, 0, state.Id) == state.Id)
            {
                SaveEntered.TrySetResult(state);
                await _release.Task.WaitAsync(token);
            }
            await inner.SaveStateAsync(state, token);
            Interlocked.Increment(ref _completedSaves);
        }

        internal void ReadObserved()
        {
            if (Volatile.Read(ref _observeReads) == 1)
            {
                Interlocked.Increment(ref _loginReads);
                OwnershipRead.TrySetResult();
            }
        }
    }

    private sealed class ControlledCharacterStore(ICharacterStore inner, SaveControl saves) : ICharacterStore
    {
        public async Task<CharacterRecord?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
        {
            CharacterRecord? result = await inner.GetByIdAsync(id, cancellationToken);
            saves.ReadObserved();
            return result;
        }
        public Task SaveStateAsync(CharacterState state, CancellationToken cancellationToken = default)
            => saves.SaveAsync(inner, state, cancellationToken);
        public Task<IReadOnlyList<CharacterRecord>> GetByAccountAsync(int accountId, CancellationToken cancellationToken = default)
            => inner.GetByAccountAsync(accountId, cancellationToken);
        public Task<bool> IsNameTakenAsync(string name, CancellationToken cancellationToken = default)
            => inner.IsNameTakenAsync(name, cancellationToken);
        public Task<int> CountByAccountAsync(int accountId, CancellationToken cancellationToken = default)
            => inner.CountByAccountAsync(accountId, cancellationToken);
        public Task<CharacterRecord> CreateAsync(CharacterRecord character, CancellationToken cancellationToken = default)
            => inner.CreateAsync(character, cancellationToken);
        public Task<bool> DeleteAsync(int id, int accountId, CancellationToken cancellationToken = default)
            => inner.DeleteAsync(id, accountId, cancellationToken);
        public Task<IReadOnlyList<ActionButton>> GetActionButtonsAsync(int characterId, CancellationToken cancellationToken = default)
            => inner.GetActionButtonsAsync(characterId, cancellationToken);
        public Task<IReadOnlyList<CharacterIdentity>> GetAllIdentitiesAsync(CancellationToken cancellationToken = default)
            => inner.GetAllIdentitiesAsync(cancellationToken);
        public Task<IReadOnlyList<int>> FindAccountIdsByNamePrefixAsync(string prefix, int limit, CancellationToken cancellationToken = default) => inner.FindAccountIdsByNamePrefixAsync(prefix, limit, cancellationToken);
    }

    /// <summary>Fresh normal daemon composition; only its listener is replaced by owned ephemeral sockets.</summary>
    private sealed class PersistentHost : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly WorldHost _host;
        private readonly TcpListener _realm;
        private readonly TcpListener _world;
        private readonly SaveControl _saves;
        private readonly CancellationTokenSource _stop = new();
        private readonly object _gate = new();
        private readonly HashSet<TcpClient> _clients = [];
        private readonly List<Task> _sessions = [];
        private Task _realmAccept = Task.CompletedTask;
        private Task _worldAccept = Task.CompletedTask;
        private Task? _worldStop;

        private PersistentHost(ServiceProvider services, TcpListener realm, TcpListener world, SaveControl saves)
        {
            _services = services;
            _realm = realm;
            _world = world;
            _saves = saves;
            World = services.GetRequiredService<WorldRuntime>();
            _host = services.GetServices<IHostedService>().OfType<WorldHost>().Single();
        }

        internal IServiceProvider Services => _services;
        internal WorldRuntime World { get; }
        internal IPEndPoint RealmEndpoint => (IPEndPoint)_realm.LocalEndpoint;
        internal IPEndPoint WorldEndpoint => (IPEndPoint)_world.LocalEndpoint;

        internal static async Task<PersistentHost> StartAsync(ClientFixtureResult fixture, SaveControl saves, CancellationToken token)
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
                services.AddScoped<ICharacterStore>(scope => new ControlledCharacterStore(
                    new EfCharacterStore(scope.GetRequiredService<CharacterDbContext>()), saves));
                provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
                result = new PersistentHost(provider, realm, world, saves);
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
                if (result is not null) await result.DisposeAsync();
                else
                {
                    realm.Stop();
                    world.Stop();
                    if (provider is not null) await provider.DisposeAsync();
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
                        if (_stop.IsCancellationRequested) { client.Dispose(); return; }
                        _clients.Add(client);
                        _sessions.Add(Task.Run(() => RunSessionAsync(client, isRealm), CancellationToken.None));
                    }
                }
            }
            catch (Exception ex) when (_stop.IsCancellationRequested
                && ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
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
                || (_stop.IsCancellationRequested && ex is ObjectDisposedException)) { }
            finally
            {
                client.Dispose();
                lock (_gate) _clients.Remove(client);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _saves.Release();
            _stop.Cancel();
            _realm.Stop();
            _world.Stop();
            TcpClient[] clients;
            lock (_gate) clients = [.. _clients];
            foreach (TcpClient client in clients) client.Dispose();
            try
            {
                await Task.WhenAll(_realmAccept, _worldAccept);
                Task[] sessions;
                lock (_gate) sessions = [.. _sessions];
                await Task.WhenAll(sessions);
            }
            finally
            {
                try { await StopWorldAsync(CancellationToken.None); }
                finally
                {
                    await _services.DisposeAsync();
                    _stop.Dispose();
                }
            }
        }
    }
}

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ArcaneCore.Cryptography;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests;

public sealed class WorldServerShutdownTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_WaitsForLoginReadAndAsyncScopeDisposalBeforeWorldFinalDrain(bool canceledStopToken)
    {
        var probe = new ScopeProbe();
        var characters = new InMemoryCharacterStore();
        var accounts = new InMemoryAccountStore();
        byte[] key = RandomNumberGenerator.GetBytes(WowSrp6.SessionKeyLength);
        Account account = await accounts.CreateAsync(new Account
        {
            Username = "SHUTDOWN", Salt = new byte[32], Verifier = new byte[32], SessionKey = key,
        });
        CharacterRecord character = await characters.CreateAsync(new CharacterRecord
        {
            AccountId = account.Id, Name = "Heldlogin", Race = 1, Class = 1, Level = 1,
            MapId = 0, ZoneId = 12, X = -8949.95f, Y = -132.493f, Z = 83.5312f,
            HomeMapId = 0, HomeZoneId = 12, HomeX = -8949.95f, HomeY = -132.493f, HomeZ = 83.5312f,
        });

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IAccountStore>(accounts);
        services.AddSingleton<IAccountDataStore>(new InMemoryAccountDataStore());
        services.AddSingleton<IWorldDataStore>(new InMemoryWorldDataStore());
        services.AddScoped<ICharacterStore>(_ => new ScopedCharacterStore(characters, probe));
        services.AddSingleton<CharacterSaveQueue>();
        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        IServiceScopeFactory scopes = provider.GetRequiredService<IServiceScopeFactory>();
        CharacterSaveQueue saves = provider.GetRequiredService<CharacterSaveQueue>();
        using var world = new WorldRuntime(new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 },
            saves, NullLogger<WorldRuntime>.Instance);
        probe.PostCleanup = () => world.Post(() => probe.WorldCleanup.TrySetResult());
        var host = new WorldHost(world, saves, new CharacterDirectory(), scopes, NullLogger<WorldHost>.Instance);
        var registry = new SessionRegistry();
        var logger = new ListenerLogger();
        int port = AvailablePort();
        var opcodes = new OpcodeTable();
        new CharacterHandlers().Register(opcodes);
        using var server = new WorldServer(scopes,
            Options.Create(new WorldOptions { BindAddress = IPAddress.Loopback.ToString(), Port = port }),
            Options.Create(new WorldSessionOptions()), opcodes, world, registry, NullLoggerFactory.Instance, logger);
        using var stopBudget = new CancellationTokenSource();
        WorldTestClient? client = null;
        Task? stopping = null;
        try
        {
            await host.StartAsync(CancellationToken.None).WaitAsync(Deadline);
            await server.StartAsync(CancellationToken.None).WaitAsync(Deadline);
            await logger.Listening.Task.WaitAsync(Deadline);
            using var socket = new TcpClient();
            await socket.ConnectAsync(IPAddress.Loopback, port).WaitAsync(Deadline);
            client = new WorldTestClient(socket);
            await client.AuthenticateAsync(account.Username, key).WaitAsync(Deadline);
            var login = new PacketWriter(8);
            login.WriteUInt64((ulong)character.Id);
            await client.SendAsync(WorldOpcode.CmsgPlayerLogin, login.ToArray()).WaitAsync(Deadline);
            await probe.ReadStarted.Task.WaitAsync(Deadline);
            Assert.Equal(1, registry.Count);
            WorldSession session = Assert.IsType<WorldSession>(registry.Find(account.Id));

            if (canceledStopToken)
            {
                stopBudget.Cancel();
            }

            stopping = StopServerAsync(server, stopBudget.Token);
            // The actual login read ignores cancellation until its owner releases it.
            // The old listener completed shutdown here while leaving this scope alive.
            await Assert.ThrowsAsync<TimeoutException>(() => stopping.WaitAsync(TimeSpan.FromMilliseconds(150)));
            Assert.False(probe.ScopeDisposed.Task.IsCompleted);
            probe.ReleaseRead.TrySetResult();
            await probe.DisposalStarted.Task.WaitAsync(Deadline);
            Assert.Equal(0, registry.Count); // RunAsync has closed the real session before scope disposal.
            Assert.Equal(SessionState.Closed, session.State);
            await Assert.ThrowsAsync<TimeoutException>(() => stopping.WaitAsync(TimeSpan.FromMilliseconds(150)));

            probe.ReleaseDisposal.TrySetResult();
            await stopping.WaitAsync(Deadline);
            Assert.True(probe.ScopeDisposed.Task.IsCompletedSuccessfully);
            await host.StopAsync(CancellationToken.None).WaitAsync(Deadline);
            Assert.True(probe.WorldCleanup.Task.IsCompletedSuccessfully);
            Assert.False(world.IsOnline(ArcaneCore.Game.ObjectGuid.Player((uint)character.Id)));
            Assert.Null(session.Player);
        }
        finally
        {
            probe.ReleaseRead.TrySetResult();
            probe.ReleaseDisposal.TrySetResult();
            if (client is not null)
            {
                await client.DisposeAsync();
            }

            if (probe.ReadStarted.Task.IsCompletedSuccessfully)
            {
                // Also drain the probe when this regression runs against the old listener.
                await probe.ScopeDisposed.Task.WaitAsync(Deadline);
            }

            await (stopping ?? StopServerAsync(server, CancellationToken.None)).WaitAsync(Deadline);
            await host.StopAsync(CancellationToken.None).WaitAsync(Deadline);
        }
    }

    private static async Task StopServerAsync(WorldServer server, CancellationToken cancellationToken)
    {
        try
        {
            await server.StopAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A host may report its expired deadline after all owned work has drained.
        }
    }

    private static int AvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class ScopeProbe
    {
        public int ClaimedRead;
        public Action? PostCleanup { get; set; }
        public TaskCompletionSource ReadStarted { get; } = NewSignal();
        public TaskCompletionSource ReleaseRead { get; } = NewSignal();
        public TaskCompletionSource DisposalStarted { get; } = NewSignal();
        public TaskCompletionSource ReleaseDisposal { get; } = NewSignal();
        public TaskCompletionSource ScopeDisposed { get; } = NewSignal();
        public TaskCompletionSource WorldCleanup { get; } = NewSignal();
    }

    private sealed class ScopedCharacterStore(InMemoryCharacterStore inner, ScopeProbe probe) : ICharacterStore, IAsyncDisposable
    {
        private bool _heldScope;

        public async Task<CharacterRecord?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
        {
            if (Interlocked.CompareExchange(ref probe.ClaimedRead, 1, 0) == 0)
            {
                _heldScope = true;
                probe.ReadStarted.TrySetResult();
                await probe.ReleaseRead.Task.ConfigureAwait(false);
            }

            return await inner.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (_heldScope)
            {
                probe.DisposalStarted.TrySetResult();
                await probe.ReleaseDisposal.Task.ConfigureAwait(false);
                probe.PostCleanup?.Invoke();
                probe.ScopeDisposed.TrySetResult();
            }
        }

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
        public Task SaveStateAsync(CharacterState state, CancellationToken cancellationToken = default)
            => inner.SaveStateAsync(state, cancellationToken);
        public Task<IReadOnlyList<ActionButton>> GetActionButtonsAsync(int characterId, CancellationToken cancellationToken = default)
            => inner.GetActionButtonsAsync(characterId, cancellationToken);
        public Task<IReadOnlyList<CharacterIdentity>> GetAllIdentitiesAsync(CancellationToken cancellationToken = default)
            => inner.GetAllIdentitiesAsync(cancellationToken);
        public Task<IReadOnlyList<int>> FindAccountIdsByNamePrefixAsync(string prefix, int limit, CancellationToken cancellationToken = default) => inner.FindAccountIdsByNamePrefixAsync(prefix, limit, cancellationToken);
    }

    private sealed class ListenerLogger : ILogger<WorldServer>
    {
        public TaskCompletionSource Listening { get; } = NewSignal();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).StartsWith("World daemon listening on ", StringComparison.Ordinal))
            {
                Listening.TrySetResult();
            }
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

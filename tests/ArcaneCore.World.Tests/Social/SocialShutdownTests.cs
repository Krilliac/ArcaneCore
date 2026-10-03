using System.Collections.Concurrent;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Social;

public sealed class SocialShutdownTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task StopAfterWorldStops_CancelsGuildPreloadAndDisposesItsScope()
    {
        var probe = new StoreProbe { BlockGuildRead = true };
        await using ServiceProvider provider = CreateProvider(probe);
        using WorldRuntime world = CreateWorld();
        await using SocialFeature feature = CreateFeature(provider);
        feature.Attach(world);
        world.Start();

        try
        {
            await probe.GuildReadStarted.Task.WaitAsync(Deadline);
            world.Stop();
            await feature.StopAsync().WaitAsync(Deadline);

            Assert.True(probe.GuildReadCanceled.Task.IsCompletedSuccessfully);
            Assert.True(probe.GuildScopeDisposed.Task.IsCompletedSuccessfully);
            Assert.True(feature.GuildsLoaded.IsCompletedSuccessfully);
            Assert.False(probe.ReleaseGuildRead.Task.IsCompleted);
            Assert.Equal(1, probe.DisposedScopes);

            // Run commands left on the stopped world: a late preload must not install guilds.
            world.RunTick(0);
            Assert.False(feature.Context.Guilds.IsLoaded);
        }
        finally
        {
            probe.ReleaseBlockedOperations();
        }
    }

    [Fact]
    public async Task StopAsync_WaitsForLoginReadAndAsyncScopeDisposal_WithoutDeferredLogin()
    {
        var probe = new StoreProbe { BlockSocialRead = true, HoldSocialScopeDisposal = true };
        await using ServiceProvider provider = CreateProvider(probe);
        using WorldRuntime world = CreateWorld();
        await using SocialFeature feature = CreateFeature(provider);
        var session = new RecordingSession(1);
        Player player = CreatePlayer(session);
        feature.Attach(world);
        world.Start();

        try
        {
            await feature.GuildsLoaded.WaitAsync(Deadline);
            await world.InvokeAsync(() =>
            {
                world.AddPlayer(player);
                world.NotifyLoggedIn(player);
                return true;
            }).WaitAsync(Deadline);
            await probe.SocialReadStarted.Task.WaitAsync(Deadline);
            world.Stop();

            Task stopped = feature.StopAsync();
            await probe.SocialReadCanceled.Task.WaitAsync(Deadline);
            Assert.False(stopped.IsCompleted);
            Assert.False(probe.SocialScopeDisposed.Task.IsCompleted);

            // This collaborator observes cancellation but finishes its read explicitly.
            // Shutdown must retain its scope until both the read and async disposal finish.
            probe.ReleaseSocialRead.TrySetResult([new SocialEntry(2, SocialFlags.Friend)]);
            await probe.SocialScopeDisposalStarted.Task.WaitAsync(Deadline);
            Assert.False(stopped.IsCompleted);
            Assert.False(probe.SocialScopeDisposed.Task.IsCompleted);
            probe.ReleaseSocialScopeDisposal.TrySetResult();
            await stopped.WaitAsync(Deadline);

            Assert.True(probe.SocialScopeDisposed.Task.IsCompletedSuccessfully);
            Assert.Equal(2, probe.DisposedScopes);
            Assert.Same(player, world.FindOnlinePlayer(player.Guid));
            // The player remains online, so a stale CompleteLogin would load these rows.
            // Flush every queued world command after stop and observe the public result.
            world.RunTick(0);
            Assert.False(feature.Context.Friends.IsLoaded(player));
            Assert.Empty(session.SocialPackets);
        }
        finally
        {
            probe.ReleaseBlockedOperations();
        }
    }

    [Fact]
    public async Task StopAsync_CanBeCalledTwice_AndRejectsSubsequentLoginEvents()
    {
        var probe = new StoreProbe();
        await using ServiceProvider provider = CreateProvider(probe);
        using WorldRuntime world = CreateWorld();
        await using SocialFeature feature = CreateFeature(provider);
        feature.Attach(world);
        world.Start();
        await feature.GuildsLoaded.WaitAsync(Deadline);
        world.Stop();

        await feature.StopAsync().WaitAsync(Deadline);
        await feature.StopAsync().WaitAsync(Deadline);

        var session = new RecordingSession(1);
        Player player = CreatePlayer(session);
        world.AddPlayer(player);
        world.NotifyLoggedIn(player);
        world.RunTick(0);
        Assert.Equal(1, probe.DisposedScopes);
        Assert.Equal(0, probe.SocialReads);
        Assert.False(feature.Context.Friends.IsLoaded(player));
        Assert.Empty(session.SocialPackets);
    }

    private static ServiceProvider CreateProvider(StoreProbe probe)
    {
        var services = new ServiceCollection();
        services.AddScoped<ISocialStore>(_ => new ScopedSocialStore(probe));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static WorldRuntime CreateWorld() => new(
        new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 },
        new NoopSaveQueue(), NullLogger<WorldRuntime>.Instance);

    private static SocialFeature CreateFeature(ServiceProvider provider)
    {
        var directory = new CharacterDirectory();
        directory.Add(new CharacterIdentity(1, 1, "Alice", 1, 0, 1));
        directory.Add(new CharacterIdentity(2, 2, "Bob", 1, 0, 1));
        return new SocialFeature(directory, provider.GetRequiredService<IServiceScopeFactory>(), NullLoggerFactory.Instance);
    }

    private static Player CreatePlayer(IPlayerSession session)
    {
        var record = new CharacterRecord
        {
            Id = 1,
            AccountId = session.AccountId,
            Name = "Alice",
            Race = (byte)Race.Human,
            Class = (byte)Class.Warrior,
            Gender = (byte)Gender.Male,
            Level = 1,
            ZoneId = 12,
        };
        return new Player(record, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), session);
    }

    private sealed class StoreProbe
    {
        private int _disposedScopes;
        private int _socialReads;

        public bool BlockGuildRead { get; init; }
        public bool BlockSocialRead { get; init; }
        public bool HoldSocialScopeDisposal { get; init; }
        public int DisposedScopes => Volatile.Read(ref _disposedScopes);
        public int SocialReads => Volatile.Read(ref _socialReads);
        public TaskCompletionSource GuildReadStarted { get; } = NewSignal();
        public TaskCompletionSource GuildReadCanceled { get; } = NewSignal();
        public TaskCompletionSource GuildScopeDisposed { get; } = NewSignal();
        public TaskCompletionSource<IReadOnlyList<GuildData>> ReleaseGuildRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SocialReadStarted { get; } = NewSignal();
        public TaskCompletionSource SocialReadCanceled { get; } = NewSignal();
        public TaskCompletionSource<IReadOnlyList<SocialEntry>> ReleaseSocialRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SocialScopeDisposalStarted { get; } = NewSignal();
        public TaskCompletionSource ReleaseSocialScopeDisposal { get; } = NewSignal();
        public TaskCompletionSource SocialScopeDisposed { get; } = NewSignal();

        public void RecordSocialRead() => Interlocked.Increment(ref _socialReads);

        public void RecordDisposedScope() => Interlocked.Increment(ref _disposedScopes);

        public void ReleaseBlockedOperations()
        {
            ReleaseGuildRead.TrySetResult([]);
            ReleaseSocialRead.TrySetResult([]);
            ReleaseSocialScopeDisposal.TrySetResult();
        }

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ScopedSocialStore(StoreProbe probe) : ISocialStore, IAsyncDisposable
    {
        private bool _guildRead;
        private bool _socialRead;

        public async Task<IReadOnlyList<GuildData>> GetGuildsAsync(CancellationToken cancellationToken = default)
        {
            _guildRead = true;
            probe.GuildReadStarted.TrySetResult();
            if (!probe.BlockGuildRead)
            {
                return [];
            }

            using CancellationTokenRegistration canceled = cancellationToken.Register(() => probe.GuildReadCanceled.TrySetResult());
            return await probe.ReleaseGuildRead.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<SocialEntry>> GetSocialAsync(int characterId, CancellationToken cancellationToken = default)
        {
            Assert.Equal(1, characterId);
            _socialRead = true;
            probe.RecordSocialRead();
            probe.SocialReadStarted.TrySetResult();
            if (!probe.BlockSocialRead)
            {
                return [];
            }

            using CancellationTokenRegistration canceled = cancellationToken.Register(() => probe.SocialReadCanceled.TrySetResult());
            return await probe.ReleaseSocialRead.Task.ConfigureAwait(false);
        }

        public Task SetSocialAsync(int characterId, int otherId, SocialFlags flags, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SaveGuildAsync(GuildData guild, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteGuildAsync(int guildId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async ValueTask DisposeAsync()
        {
            if (_socialRead)
            {
                probe.SocialScopeDisposalStarted.TrySetResult();
                if (probe.HoldSocialScopeDisposal)
                {
                    await probe.ReleaseSocialScopeDisposal.Task.ConfigureAwait(false);
                }
            }

            probe.RecordDisposedScope();
            if (_guildRead)
            {
                probe.GuildScopeDisposed.TrySetResult();
            }
            if (_socialRead)
            {
                probe.SocialScopeDisposed.TrySetResult();
            }
        }
    }

    private sealed class RecordingSession(int accountId) : IPlayerSession
    {
        public ConcurrentQueue<WorldOpcode> SocialPackets { get; } = new();
        public int AccountId => accountId;
        public AccountSecurity Security => AccountSecurity.Player;

        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload)
        {
            if (opcode is WorldOpcode.SmsgFriendList or WorldOpcode.SmsgIgnoreList or WorldOpcode.SmsgFriendStatus)
            {
                SocialPackets.Enqueue(opcode);
            }
        }

        public void ProcessWorldPackets(Player player) { }
        public void Kick() { }
        public void OnLoggedOut() { }
    }

    private sealed class NoopSaveQueue : ICharacterSaveQueue
    {
        public void Enqueue(CharacterState state) { }
    }
}

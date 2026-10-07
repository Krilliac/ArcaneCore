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

/// <summary>
/// A friend/ignore list that cannot be read must not be replaced by an empty one: an empty ignore list lets ignored
/// players invite and be heard, and later writes of the player would be based on the empty list. The read is retried;
/// when it keeps failing the player is never marked ready (deferred commands do not run) and is disconnected.
/// </summary>
public sealed class SocialLoadFailureTests
{
    [Fact]
    public async Task PersistentReadFailure_DoesNotInstallAnEmptyList_DropsDeferredCommands_AndDisconnects()
    {
        var store = new FailingStore(failures: int.MaxValue);
        await using var kit = new Kit(store);
        int executed = 0;
        await kit.World.InvokeAsync(() =>
        {
            kit.World.AddPlayer(kit.Alice);
            kit.World.NotifyLoggedIn(kit.Alice);
            kit.Feature.ExecuteWhenReady(kit.Alice, _ => executed++);
            return true;
        });

        await WorldTestHost.WaitForAsync(() => kit.AliceSession.Kicked, "the session to be disconnected");
        Assert.True(store.Reads >= 2, "the read is retried before giving up");
        Assert.False(await kit.World.InvokeAsync(() => kit.Feature.Context.Friends.IsLoaded(kit.Alice)));
        Assert.Equal(0, executed);
    }

    [Fact]
    public async Task TransientReadFailure_IsRetried_AndTheStoredListIsInstalled()
    {
        var store = new FailingStore(failures: 1);
        store.Rows.Add(new SocialEntry(2, SocialFlags.Ignored));
        await using var kit = new Kit(store);
        await kit.World.InvokeAsync(() =>
        {
            kit.World.AddPlayer(kit.Alice);
            kit.World.NotifyLoggedIn(kit.Alice);
            return true;
        });

        await WorldTestHost.WaitForAsync(
            () => kit.World.InvokeAsync(() => kit.Feature.Context.Friends.IsLoaded(kit.Alice)).GetAwaiter().GetResult(),
            "the social list to be installed");
        Assert.True(await kit.World.InvokeAsync(() => kit.Feature.Context.Friends.HasIgnore(kit.Alice, ObjectGuid.Player(2))));
        Assert.False(kit.AliceSession.Kicked);
    }

    private sealed class Kit : IAsyncDisposable
    {
        private readonly ServiceProvider _services;

        public Kit(ISocialStore store)
        {
            _services = new ServiceCollection().AddSingleton(store).BuildServiceProvider();
            World = new WorldRuntime(new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 },
                new NoopSaveQueue(), NullLogger<WorldRuntime>.Instance);
            var directory = new CharacterDirectory();
            directory.Add(new CharacterIdentity(1, 1, "Alice", 1, 0, 1));
            directory.Add(new CharacterIdentity(2, 2, "Bob", 1, 0, 1));
            Feature = new SocialFeature(directory, _services.GetRequiredService<IServiceScopeFactory>(), NullLoggerFactory.Instance);
            Alice = new Player(new CharacterRecord
            {
                Id = 1, AccountId = 1, Name = "Alice", Race = (byte)Race.Human,
                Class = (byte)Class.Warrior, Gender = (byte)Gender.Male, Level = 1, ZoneId = 12,
            }, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), AliceSession);
            Feature.Attach(World);
            World.Start();
        }

        public WorldRuntime World { get; }

        public SocialFeature Feature { get; }

        public RecordingSession AliceSession { get; } = new();

        public Player Alice { get; }

        public async ValueTask DisposeAsync()
        {
            await Feature.StopAsync();
            World.Dispose();
            await _services.DisposeAsync();
        }
    }

    private sealed class RecordingSession : IPlayerSession
    {
        private volatile bool _kicked;
        public bool Kicked => _kicked;
        public int AccountId => 1;
        public AccountSecurity Security => AccountSecurity.Player;
        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload) { }
        public void ProcessWorldPackets(Player player) { }
        public void Kick() => _kicked = true;
        public void OnLoggedOut() { }
    }

    private sealed class NoopSaveQueue : ICharacterSaveQueue
    {
        public void Enqueue(CharacterState state) { }
    }

    private sealed class FailingStore(int failures) : ISocialStore
    {
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);
        public ConcurrentBag<SocialEntry> Rows { get; } = [];

        public Task<IReadOnlyList<SocialEntry>> GetSocialAsync(int characterId, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _reads) <= failures)
            {
                throw new InvalidOperationException("injected social read failure");
            }

            return Task.FromResult<IReadOnlyList<SocialEntry>>([.. Rows]);
        }

        public Task SetSocialAsync(int characterId, int otherId, SocialFlags flags, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<GuildData>> GetGuildsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GuildData>>([]);
        public Task SaveGuildAsync(GuildData guild, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteGuildAsync(int guildId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

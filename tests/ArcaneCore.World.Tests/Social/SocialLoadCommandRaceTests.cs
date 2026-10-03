using System.Collections.Concurrent;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Social;
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

public sealed class SocialLoadCommandRaceTests
{
    [Fact]
    public async Task FriendAddedDuringObserversOldEmptyLoad_IsDeferredAndStillReceivesOfflinePresence()
    {
        await using var kit = new Kit();
        await kit.BeginAsync();
        await kit.World.InvokeAsync(() =>
        {
            kit.Feature.ExecuteWhenReady(kit.Alice, context => context.Friends.AddFriend(kit.Alice, "Bob"));
            Assert.False(kit.Feature.Context.Friends.IsLoaded(kit.Alice));
            return true;
        });
        Assert.Empty(kit.AliceSession.Statuses);
        Assert.Empty(kit.Store.Writes);
        kit.Store.ReleaseFirstRead.TrySetResult();
        await kit.WaitLoadedAsync(kit.Alice);
        byte[] added = Assert.Single(kit.AliceSession.Statuses);
        Assert.Equal((byte)FriendsResult.AddedOnline, added[0]);
        Assert.Equal((byte)FriendStatus.Online, added[9]);
        await WorldTestHost.WaitForAsync(() => kit.Store.Writes.Count == 1, "the deferred friend write");
        Assert.Equal(SocialFlags.Friend, Assert.Single(kit.Store.Writes).Flags);

        await kit.World.InvokeAsync(() =>
        {
            Assert.True(kit.Feature.Context.Friends.Get(kit.Alice).Has(2, SocialFlags.Friend));
            kit.World.RemovePlayer(kit.Bob);
            return true;
        });
        byte[][] statuses = kit.AliceSession.Statuses.ToArray();
        Assert.Equal(2, statuses.Length);
        Assert.Equal(new byte[] { (byte)FriendsResult.Offline, 2, 0, 0, 0, 0, 0, 0, 0 }, statuses[1]);
    }

    [Fact]
    public async Task ParsedMutationsReplayInOrderAfterLoading_AndOneFailureDoesNotDropTheRest()
    {
        await using var kit = new Kit();
        await kit.BeginAsync();
        await kit.World.InvokeAsync(() =>
        {
            kit.Feature.ExecuteWhenReady(kit.Alice, _ => throw new InvalidOperationException("injected deferred command failure"));
            kit.Feature.ExecuteWhenReady(kit.Alice, context => context.Friends.AddFriend(kit.Alice, "Bob"));
            kit.Feature.ExecuteWhenReady(kit.Alice, context => context.Friends.RemoveFriend(kit.Alice, kit.Bob.Guid));
            kit.Feature.ExecuteWhenReady(kit.Alice, context => context.Friends.AddIgnore(kit.Alice, "Bob"));
            kit.Feature.ExecuteWhenReady(kit.Alice, context => context.Friends.RemoveIgnore(kit.Alice, kit.Bob.Guid));
            return true;
        });
        Assert.Empty(kit.AliceSession.Statuses);
        kit.Store.ReleaseFirstRead.TrySetResult();
        await kit.WaitLoadedAsync(kit.Alice);
        Assert.Equal(new[] { FriendsResult.AddedOnline, FriendsResult.Removed, FriendsResult.IgnoreAdded, FriendsResult.IgnoreRemoved },
            kit.AliceSession.Statuses.Select(body => (FriendsResult)body[0]));
        Assert.True(await kit.World.InvokeAsync(() => kit.Feature.Context.Friends.Get(kit.Alice).Entries.Count == 0));
    }

    [Fact]
    public async Task DepartedPlayerCannotReplayQueuedOrNewCommandsIntoItsReplacement()
    {
        await using var kit = new Kit();
        await kit.BeginAsync();
        var replacementSession = new RecordingSession(1);
        Player replacement = CreatePlayer(1, "Alice", replacementSession);
        int executed = 0;
        await kit.World.InvokeAsync(() =>
        {
            kit.Feature.ExecuteWhenReady(kit.Alice, _ => executed++);
            kit.World.RemovePlayer(kit.Alice);
            kit.World.AddPlayer(replacement);
            kit.World.NotifyLoggedIn(replacement);
            return true;
        });
        await kit.WaitLoadedAsync(replacement);
        await kit.World.InvokeAsync(() =>
        {
            kit.Feature.ExecuteWhenReady(kit.Alice, context =>
            {
                executed++;
                context.Friends.AddFriend(kit.Alice, "Bob");
            });
            Assert.Empty(kit.Feature.Context.Friends.Get(replacement).Entries);
            return true;
        });
        kit.Store.ReleaseFirstRead.TrySetResult();
        await kit.Store.FirstReadReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await kit.World.InvokeAsync(() =>
        {
            kit.Feature.ExecuteWhenReady(kit.Alice, _ => executed++);
            Assert.Empty(kit.Feature.Context.Friends.Get(replacement).Entries);
            return true;
        });
        Assert.Equal(0, executed);
        Assert.Empty(kit.Store.Writes);
        Assert.Empty(replacementSession.Statuses);
    }

    [Fact]
    public async Task DeferredLimitKicksAndDropsTheExactPlayersPendingCommands()
    {
        await using var kit = new Kit();
        await kit.BeginAsync();
        int executed = 0;
        await kit.World.InvokeAsync(() =>
        {
            for (int i = 0; i < 129; i++)
            {
                kit.Feature.ExecuteWhenReady(kit.Alice, _ => executed++);
            }

            Assert.True(kit.AliceSession.Kicked);
            kit.Feature.ExecuteWhenReady(kit.Alice, _ => executed++);
            return true;
        });
        kit.Store.ReleaseFirstRead.TrySetResult();
        await kit.WaitLoadedAsync(kit.Alice);
        Assert.Equal(0, executed);
    }

    private static Player CreatePlayer(int id, string name, IPlayerSession session) => new(new CharacterRecord
    {
        Id = id, AccountId = session.AccountId, Name = name, Race = (byte)Race.Human,
        Class = (byte)Class.Warrior, Gender = (byte)Gender.Male, Level = 1, ZoneId = 12,
    }, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), session);

    private sealed class Kit : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        public Kit()
        {
            _services = new ServiceCollection().AddSingleton<ISocialStore>(Store).BuildServiceProvider();
            World = new WorldRuntime(new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 },
                new NoopSaveQueue(), NullLogger<WorldRuntime>.Instance);
            var directory = new CharacterDirectory();
            directory.Add(new CharacterIdentity(1, 1, "Alice", 1, 0, 1));
            directory.Add(new CharacterIdentity(2, 2, "Bob", 1, 0, 1));
            Feature = new SocialFeature(directory, _services.GetRequiredService<IServiceScopeFactory>(), NullLoggerFactory.Instance);
            Alice = CreatePlayer(1, "Alice", AliceSession);
            Bob = CreatePlayer(2, "Bob", new RecordingSession(2));
            Feature.Attach(World);
            World.Start();
        }

        public BlockingStore Store { get; } = new();
        public WorldRuntime World { get; }
        public SocialFeature Feature { get; }
        public RecordingSession AliceSession { get; } = new(1);
        public Player Alice { get; }
        public Player Bob { get; }
        public async Task BeginAsync()
        {
            await Feature.GuildsLoaded.WaitAsync(TimeSpan.FromSeconds(5));
            await World.InvokeAsync(() =>
            {
                World.AddPlayer(Alice);
                World.AddPlayer(Bob);
                World.NotifyLoggedIn(Alice);
                return true;
            });
            await Store.FirstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public async Task WaitLoadedAsync(Player player)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (!await World.InvokeAsync(() => Feature.Context.Friends.IsLoaded(player)))
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException("the social list did not finish loading");
                }

                await Task.Delay(10);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Store.ReleaseFirstRead.TrySetResult();
            await Feature.StopAsync();
            World.Dispose();
            await _services.DisposeAsync();
        }
    }

    private sealed class RecordingSession(int accountId) : IPlayerSession
    {
        public ConcurrentQueue<byte[]> Statuses { get; } = new();
        public bool Kicked { get; private set; }
        public int AccountId => accountId;
        public AccountSecurity Security => AccountSecurity.Player;
        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload)
        {
            if (opcode == WorldOpcode.SmsgFriendStatus)
            {
                Statuses.Enqueue(payload.ToArray());
            }
        }

        public void ProcessWorldPackets(Player player) { }
        public void Kick() => Kicked = true;
        public void OnLoggedOut() { }
    }

    private sealed class NoopSaveQueue : ICharacterSaveQueue
    {
        public void Enqueue(CharacterState state) { }
    }

    private sealed class BlockingStore : ISocialStore
    {
        private int _aliceReads;
        public TaskCompletionSource FirstReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstReadReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<(int Character, int Other, SocialFlags Flags)> Writes { get; } = new();
        public async Task<IReadOnlyList<SocialEntry>> GetSocialAsync(int characterId, CancellationToken cancellationToken = default)
        {
            if (characterId == 1 && Interlocked.Increment(ref _aliceReads) == 1)
            {
                // This read owns an old empty snapshot even if later writes occur.
                FirstReadStarted.TrySetResult();
                await ReleaseFirstRead.Task.WaitAsync(cancellationToken);
                FirstReadReturned.TrySetResult();
            }

            return [];
        }

        public Task SetSocialAsync(int characterId, int otherId, SocialFlags flags, CancellationToken cancellationToken = default)
        {
            Writes.Enqueue((characterId, otherId, flags));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<GuildData>> GetGuildsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GuildData>>([]);
        public Task SaveGuildAsync(GuildData guild, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteGuildAsync(int guildId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

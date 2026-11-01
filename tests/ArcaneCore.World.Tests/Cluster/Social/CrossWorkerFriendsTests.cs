using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Cluster.Social;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Social;

/// <summary>
/// Friends and ignore across workers (S3c): friend status of a character online on another worker comes from realm presence, an ignored whisper routed to the
/// recipient's worker is refused there, and a social write that keeps failing stays retained and is retried (it is never dropped).
/// </summary>
public sealed class CrossWorkerFriendsTests
{
    private const int AliceId = 41;
    private const int BobId = 42;
    private const int CarolId = 43;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);

    private static RealmPresenceEntry AliceOnWorkerA(byte level = 10, uint zone = 12)
        => new(AliceId, "Alice", 77, "worker-a", "gw", 0, zone, 0, level, (byte)Class.Warrior, (byte)Race.Human, 0, 0, 0, 1, 1, DateTimeOffset.UtcNow);

    private static Player CreatePlayer(int id, string name, Race race, RecordingSession session)
    {
        var record = new CharacterRecord
        {
            Id = id, AccountId = 100 + id, Name = name,
            Race = (byte)race, Class = (byte)Class.Warrior,
            Gender = (byte)Gender.Male, Level = 10, ZoneId = 12,
        };
        return new Player(record, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), session);
    }

    private static (WorldRuntime World, SocialContext Context) CreateWorld()
    {
        var world = new WorldRuntime(
            new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 },
            new NullSaveQueue(), NullLogger<WorldRuntime>.Instance);
        var lookup = new FixedLookup(
            new CharacterInfo(AliceId, 77, "Alice", Race.Human, Class.Warrior),
            new CharacterInfo(BobId, 142, "Bob", Race.Human, Class.Warrior),
            new CharacterInfo(CarolId, 143, "Carol", Race.Orc, Class.Warrior));
        return (world, new SocialContext(world, lookup, new NullSocialPersistence()));
    }

    [Fact]
    public async Task FriendOnAnotherWorker_ComesOnlineAndGoesOffline_FromRealmPresence()
    {
        var directory = new InMemoryRealmPresenceDirectory();
        (WorldRuntime world, SocialContext context) = CreateWorld();
        using (world)
        {
            var bobSession = new RecordingSession();
            var carolSession = new RecordingSession();
            Player bob = CreatePlayer(BobId, "Bob", Race.Human, bobSession);
            Player carol = CreatePlayer(CarolId, "Carol", Race.Orc, carolSession);
            var cross = new CrossWorkerSocial(directory);
            world.Start();
            await world.InvokeAsync(() =>
            {
                world.AddPlayer(bob);
                world.AddPlayer(carol);
                context.Friends.Load(bob, [new SocialEntry(AliceId, SocialFlags.Friend)]);
                context.Friends.Load(carol, [new SocialEntry(AliceId, SocialFlags.Friend)]);
                context.Friends.Remote = cross;
                return true;
            });

            // Nothing is online yet: nobody is a candidate that the directory lists, and the list shows Alice offline.
            ulong[] wanted = await world.InvokeAsync(() => context.Friends.RemoteFriendCandidates());
            Assert.Equal([(ulong)AliceId], wanted);
            Assert.Empty(await cross.RefreshAsync(wanted));
            Assert.False(cross.TryGet(ObjectGuid.Player(AliceId), out _));

            // Alice comes online on worker A: the refresh reports her, and Bob (same faction) is told FRIEND_ONLINE with her zone, level and class.
            Assert.Equal(PresenceMutationResult.Applied, directory.TryUpsert(AliceOnWorkerA(), out _));
            RemotePresenceChange cameOnline = Assert.Single(await cross.RefreshAsync(wanted));
            Assert.True(cameOnline.Online);
            Assert.Equal((ulong)AliceId, cameOnline.Guid);
            bobSession.Clear();
            carolSession.Clear();
            await world.InvokeAsync(() =>
            {
                context.Friends.NotifyRemotePresence(cameOnline);
                return true;
            });

            byte[] online = Assert.Single(bobSession.Of(WorldOpcode.SmsgFriendStatus));
            Assert.Equal((byte)FriendsResult.Online, online[0]);
            Assert.Equal(ObjectGuid.Player(AliceId).Value, BitConverter.ToUInt64(online, 1));
            Assert.Equal((byte)FriendStatus.Online, online[9]);
            Assert.Equal(12u, BitConverter.ToUInt32(online, 10));
            Assert.Equal(10u, BitConverter.ToUInt32(online, 14));
            Assert.Equal((uint)Class.Warrior, BitConverter.ToUInt32(online, 18));

            // The other faction does not see her (SocialContext.CanSeeOnline rule), so Carol is told nothing.
            Assert.Empty(carolSession.Of(WorldOpcode.SmsgFriendStatus));

            // The friend list now reports her online with the presence-directory status.
            bobSession.Clear();
            await world.InvokeAsync(() =>
            {
                context.Friends.SendFriendList(bob);
                return true;
            });
            byte[] list = Assert.Single(bobSession.Of(WorldOpcode.SmsgFriendList));
            Assert.Equal(1, list[0]);
            Assert.Contains((byte)FriendStatus.Online, list.Skip(9).Take(1));

            // A level-up or zone change on worker A is not an online/offline transition: the cache follows silently and no SMSG_FRIEND_STATUS is sent.
            Assert.Equal(PresenceMutationResult.Applied, directory.TryUpsert(AliceOnWorkerA(level: 11, zone: 14) with { PresenceVersion = 2 }, out _));
            Assert.Empty(await cross.RefreshAsync(wanted));
            Assert.True(cross.TryGet(ObjectGuid.Player(AliceId), out RemoteFriendInfo moved));
            Assert.Equal(11u, moved.Level);
            Assert.Equal(14u, moved.Area);

            // She logs out on worker A: FRIEND_OFFLINE to Bob, nothing to the other faction (Carol never saw her online).
            Assert.Equal(PresenceMutationResult.Applied, directory.TryRemove(AliceId, 2, 1, "worker-a"));
            RemotePresenceChange wentOffline = Assert.Single(await cross.RefreshAsync(wanted));
            Assert.False(wentOffline.Online);
            Assert.Equal(11u, wentOffline.Info.Level);
            bobSession.Clear();
            carolSession.Clear();
            await world.InvokeAsync(() =>
            {
                context.Friends.NotifyRemotePresence(wentOffline);
                return true;
            });
            byte[] offline = Assert.Single(bobSession.Of(WorldOpcode.SmsgFriendStatus));
            Assert.Equal((byte)FriendsResult.Offline, offline[0]);
            Assert.Equal(ObjectGuid.Player(AliceId).Value, BitConverter.ToUInt64(offline, 1));
            Assert.Empty(carolSession.Of(WorldOpcode.SmsgFriendStatus));
        }
    }

    [Fact]
    public async Task FriendAlreadyOnlineOnAnotherWorker_AfterLogin_ShowsOnlineWithoutComeOnlineLine()
    {
        var directory = new InMemoryRealmPresenceDirectory();
        Assert.Equal(PresenceMutationResult.Applied, directory.TryUpsert(AliceOnWorkerA(), out _));
        (WorldRuntime world, SocialContext context) = CreateWorld();
        using (world)
        {
            var bobSession = new RecordingSession();
            Player bob = CreatePlayer(BobId, "Bob", Race.Human, bobSession);
            var cross = new CrossWorkerSocial(directory);
            world.Start();

            // Login order: Load, then SendFriendList from a cache that does not know Alice yet, so the list shows her offline.
            await world.InvokeAsync(() =>
            {
                world.AddPlayer(bob);
                context.Friends.Remote = cross;
                context.Friends.Load(bob, [new SocialEntry(AliceId, SocialFlags.Friend)]);
                context.Friends.SendFriendList(bob);
                return true;
            });
            byte[] first = Assert.Single(bobSession.Of(WorldOpcode.SmsgFriendList));
            Assert.Equal(1, first[0]);
            Assert.Equal((byte)FriendStatus.Offline, first[9]);

            // The first refresh finds her online: a silent change, and Bob gets a corrected friend list and no FRIEND_STATUS.
            ulong[] wanted = await world.InvokeAsync(() => context.Friends.RemoteFriendCandidates());
            bobSession.Clear();
            IReadOnlyList<RemotePresenceChange> changes = await cross.RefreshAsync(wanted);
            RemotePresenceChange change = Assert.Single(changes);
            Assert.True(change.Online);
            Assert.False(change.Announce);
            await world.InvokeAsync(() =>
            {
                context.Friends.NotifyRemotePresence(changes);
                return true;
            });
            Assert.Empty(bobSession.Of(WorldOpcode.SmsgFriendStatus));
            byte[] second = Assert.Single(bobSession.Of(WorldOpcode.SmsgFriendList));
            Assert.Equal((byte)FriendStatus.Online, second[9]);
            Assert.Equal(12u, BitConverter.ToUInt32(second, 10));
            Assert.Equal(10u, BitConverter.ToUInt32(second, 14));

            // A later refresh with nothing new sends nothing.
            bobSession.Clear();
            Assert.Empty(await cross.RefreshAsync(wanted));
            Assert.Empty(bobSession.Of(WorldOpcode.SmsgFriendList));
        }
    }

    [Fact]
    public async Task WhisperRoutedToTheRecipientsWorker_IsRefusedWhenTheRecipientIgnoresTheSender()
    {
        (WorldRuntime world, SocialContext context) = CreateWorld();
        using (world)
        {
            var bobSession = new RecordingSession();
            var carolSession = new RecordingSession();
            Player bob = CreatePlayer(BobId, "Bob", Race.Human, bobSession);
            Player carol = CreatePlayer(CarolId, "Carol", Race.Orc, carolSession);
            world.Start();
            await world.InvokeAsync(() =>
            {
                world.AddPlayer(bob);
                world.AddPlayer(carol);
                context.Friends.Load(bob, [new SocialEntry(AliceId, SocialFlags.Ignored)]);
                context.Friends.Load(carol, []);
                return true;
            });

            RoutedSocialDelivery To(int recipient, string name)
                => new(Guid.NewGuid(), SocialRouteChannel.Whisper, (ulong)recipient, name, 100 + recipient, 1, "worker-b", "gw", AliceId, "Alice", 1, "hello");

            bobSession.Clear();
            carolSession.Clear();
            (RoutedWhisperOutcome ignored, RoutedWhisperOutcome allowed, RoutedWhisperOutcome absent) = await world.InvokeAsync(() => (
                CrossWorkerSocial.DeliverWhisper(world, context, To(BobId, "Bob")),
                CrossWorkerSocial.DeliverWhisper(world, context, To(CarolId, "Carol")),
                CrossWorkerSocial.DeliverWhisper(world, context, To(99, "Nobody"))));

            Assert.Equal(RoutedWhisperOutcome.Ignored, ignored);
            Assert.Empty(bobSession.Of(WorldOpcode.SmsgMessagechat));
            Assert.Equal(RoutedWhisperOutcome.Delivered, allowed);
            Assert.Single(carolSession.Of(WorldOpcode.SmsgMessagechat));
            Assert.Equal(RoutedWhisperOutcome.RecipientNotHere, absent);
        }
    }

    [Fact]
    public async Task SocialWrite_ThatFailsFiveTimes_IsRetainedAndRetried()
    {
        var store = new FlakySocialStore(failures: 5);
        await using ServiceProvider provider = new ServiceCollection().AddSingleton<ISocialStore>(store).BuildServiceProvider();
        var queue = new SocialWriteQueue(
            provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, new SocialWriteQueueOptions { RetryDelayMs = 1 })
        {
            RetainedRetryInterval = Timeout.InfiniteTimeSpan,
        };
        queue.Start();
        try
        {
            queue.SetSocial(BobId, AliceId, SocialFlags.Friend);

            // Three attempts fail: the write is retained, not dropped.
            await queue.FlushAsync().WaitAsync(Budget);
            Assert.Equal(3, store.Attempts);
            Assert.True(queue.HasRetainedFailure(BobId));
            Assert.Equal([BobId], queue.RetainedCharacters);
            Assert.Empty(store.Rows);

            // Attempt four (a retained-write sweep) fails too: still retained.
            Assert.True(queue.PostRetainedSweepForTest());
            await queue.FlushAsync().WaitAsync(Budget);
            Assert.Equal(4, store.Attempts);
            Assert.True(queue.HasRetainedFailure(BobId));

            // The login barrier retries it: attempt five fails, attempt six reaches the store.
            await queue.FlushCharacterAsync(BobId).WaitAsync(Budget);
            Assert.Equal(6, store.Attempts);
            Assert.False(queue.HasRetainedFailure(BobId));
            Assert.Empty(queue.RetainedCharacters);
            Assert.Equal(SocialFlags.Friend, store.Rows[(BobId, AliceId)]);
        }
        finally
        {
            await queue.StopAsync().WaitAsync(Budget);
        }
    }

    private sealed class RecordingSession : IPlayerSession
    {
        private readonly object _gate = new();
        private readonly List<(WorldOpcode Opcode, byte[] Payload)> _sent = [];

        public int AccountId => 0;
        public AccountSecurity Security => AccountSecurity.Player;

        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload)
        {
            lock (_gate)
            {
                _sent.Add((opcode, payload.ToArray()));
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _sent.Clear();
            }
        }

        public List<byte[]> Of(WorldOpcode opcode)
        {
            lock (_gate)
            {
                return [.. _sent.Where(p => p.Opcode == opcode).Select(p => p.Payload)];
            }
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

    private sealed class FixedLookup(params CharacterInfo[] characters) : ICharacterLookup
    {
        public CharacterInfo? Find(uint characterId) => characters.FirstOrDefault(c => c.Id == characterId);

        public CharacterInfo? FindByName(string name) => characters.FirstOrDefault(c => c.Name == name);
    }

    private sealed class NullSocialPersistence : ISocialPersistence
    {
        public void SetSocial(int characterId, int otherId, SocialFlags flags)
        {
        }

        public void SaveGuild(GuildData guild)
        {
        }

        public void DeleteGuild(int guildId)
        {
        }
    }

    /// <summary>A store whose first <c>failures</c> row writes throw; after that rows are kept.</summary>
    private sealed class FlakySocialStore(int failures) : ISocialStore
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public Dictionary<(int Character, int Other), SocialFlags> Rows { get; } = [];

        public Task<IReadOnlyList<SocialEntry>> GetSocialAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SocialEntry>>([]);

        public Task SetSocialAsync(int characterId, int otherId, SocialFlags flags, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _attempts) <= failures)
            {
                throw new InvalidOperationException("storage unavailable");
            }

            lock (Rows)
            {
                Rows[(characterId, otherId)] = flags;
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<GuildData>> GetGuildsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GuildData>>([]);

        public Task SaveGuildAsync(GuildData guild, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteGuildAsync(int guildId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

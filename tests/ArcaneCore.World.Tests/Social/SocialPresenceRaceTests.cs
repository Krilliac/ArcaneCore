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

public sealed class SocialPresenceRaceTests
{
    [Fact]
    public async Task LogoutWhileOwnSocialLoadIsBlocked_NotifiesFriendWhoWasToldOnline()
    {
        var store = new BlockedSocialStore();
        var services = new ServiceCollection();
        services.AddSingleton<ISocialStore>(store);
        await using ServiceProvider provider = services.BuildServiceProvider();
        using var world = new WorldRuntime(
            new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 },
            new NoopSaveQueue(), NullLogger<WorldRuntime>.Instance);
        var directory = new CharacterDirectory();
        directory.Add(new CharacterIdentity(1, 1, "Alice", 1, 0, 1));
        directory.Add(new CharacterIdentity(2, 2, "Bob", 1, 0, 1));
        await using var feature = new SocialFeature(
            directory, provider.GetRequiredService<IServiceScopeFactory>(), NullLoggerFactory.Instance);
        var aliceSession = new RecordingSession(1);
        Player alice = CreatePlayer(1, "Alice", aliceSession);
        Player bob = CreatePlayer(2, "Bob", new RecordingSession(2));
        feature.Attach(world);
        world.Start();

        try
        {
            await feature.GuildsLoaded.WaitAsync(TimeSpan.FromSeconds(10));
            await world.InvokeAsync(() =>
            {
                world.AddPlayer(alice);
                world.AddPlayer(bob);
                // The observer has finished loading; only the target's own read is blocked.
                feature.Context.Friends.Load(alice, []);
                world.NotifyLoggedIn(bob);
                return true;
            });
            await store.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            await world.InvokeAsync(() =>
            {
                Assert.False(feature.Context.Friends.IsLoaded(bob));
                feature.Context.Friends.AddFriend(alice, "Bob");
                return true;
            });
            byte[] added = Assert.Single(aliceSession.FriendStatuses);
            Assert.Equal((byte)FriendsResult.AddedOnline, added[0]);
            Assert.Equal((byte)FriendStatus.Online, added[9]);

            await world.InvokeAsync(() =>
            {
                world.RemovePlayer(bob);
                return true;
            });

            byte[][] statuses = aliceSession.FriendStatuses.ToArray();
            Assert.Equal(2, statuses.Length);
            Assert.Equal(new byte[] { (byte)FriendsResult.Offline, 2, 0, 0, 0, 0, 0, 0, 0 }, statuses[1]);
            Assert.False(store.ReleaseRead.Task.IsCompleted);
        }
        finally
        {
            store.ReleaseRead.TrySetResult([]);
        }
    }

    private static Player CreatePlayer(int id, string name, IPlayerSession session)
    {
        var record = new CharacterRecord
        {
            Id = id, AccountId = session.AccountId, Name = name,
            Race = (byte)Race.Human, Class = (byte)Class.Warrior,
            Gender = (byte)Gender.Male, Level = 1, ZoneId = 12,
        };
        return new Player(record, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), session);
    }

    private sealed class RecordingSession(int accountId) : IPlayerSession
    {
        public ConcurrentQueue<byte[]> FriendStatuses { get; } = new();
        public int AccountId => accountId;
        public AccountSecurity Security => AccountSecurity.Player;

        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload)
        {
            if (opcode == WorldOpcode.SmsgFriendStatus)
            {
                FriendStatuses.Enqueue(payload.ToArray());
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

    private sealed class BlockedSocialStore : ISocialStore
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IReadOnlyList<SocialEntry>> ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<SocialEntry>> GetSocialAsync(int characterId, CancellationToken cancellationToken = default)
        {
            Assert.Equal(2, characterId);
            ReadStarted.TrySetResult();
            return ReleaseRead.Task;
        }

        public Task SetSocialAsync(int characterId, int otherId, SocialFlags flags, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<GuildData>> GetGuildsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GuildData>>([]);

        public Task SaveGuildAsync(GuildData guild, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteGuildAsync(int guildId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

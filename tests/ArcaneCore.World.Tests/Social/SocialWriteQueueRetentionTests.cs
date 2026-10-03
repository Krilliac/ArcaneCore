using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Social;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Social;

/// <summary>
/// A social write that fails all its attempts is retained, not dropped, and follows the
/// reputation queue's contract: retried at the next write, at the login barrier, at logout and at
/// shutdown, with FlushAsync a pure barrier. Backoff is 1 ms and nothing here asserts a duration.
/// </summary>
public sealed class SocialWriteQueueRetentionTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private const int ThreeAttempts = 3;

    [Fact]
    public async Task AStoreFailingThreeTimesThenRecovering_PersistsTheFinalState_AtTheNextWrite()
    {
        await using Rig rig = Rig.Create();
        rig.Store.FailNext(ThreeAttempts);

        rig.Queue.SetSocial(1, 2, SocialFlags.Friend);
        await rig.Queue.FlushAsync().WaitAsync(Wait);       // pure barrier: attempted, never throws
        Assert.Equal(ThreeAttempts, rig.Store.Attempts);
        Assert.Equal(SocialFlags.None, rig.Store.Row(1, 2));
        Assert.True(rig.Queue.HasRetainedFailure(1));
        Assert.Equal([1], rig.Queue.RetainedCharacters);
        Assert.Equal(1, rig.Queue.PendingRows);              // still counted: it is not durable

        rig.Queue.SetSocial(1, 3, SocialFlags.Ignored);      // the store works again: this write ...
        await rig.Queue.FlushAsync().WaitAsync(Wait);

        Assert.Equal(SocialFlags.Ignored, rig.Store.Row(1, 3));
        Assert.Equal(SocialFlags.Friend, rig.Store.Row(1, 2));   // ... and the retained one both persist
        Assert.False(rig.Queue.HasRetainedFailure(1));
        Assert.Equal(0, rig.Queue.PendingRows);
    }

    [Fact]
    public async Task ANewerValueOfARetainedRow_ReplacesIt_SoTheFinalStateIsWhatPersists()
    {
        await using Rig rig = Rig.Create();
        rig.Store.FailNext(ThreeAttempts);
        rig.Queue.SetSocial(1, 2, SocialFlags.Friend);
        await rig.Queue.FlushAsync().WaitAsync(Wait);
        Assert.True(rig.Queue.HasRetainedFailure(1));

        rig.Queue.SetSocial(1, 2, SocialFlags.Ignored);       // the player changed their mind while storage was down
        await rig.Queue.FlushAsync().WaitAsync(Wait);

        Assert.Equal(SocialFlags.Ignored, rig.Store.Row(1, 2));
        Assert.False(rig.Queue.HasRetainedFailure(1));
        Assert.Equal(0, rig.Queue.PendingRows);
    }

    [Fact]
    public async Task LoginBarrier_ThrowsWhileUnrecovered_ThenRecovers()
    {
        await using Rig rig = Rig.Create();
        rig.Store.FailNext(int.MaxValue);
        rig.Queue.SetSocial(1, 2, SocialFlags.Friend);
        await rig.Queue.FlushAsync().WaitAsync(Wait);

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Queue.FlushCharacterAsync(1).WaitAsync(Wait));
        Assert.Contains("character 1", failure.Message);
        await rig.Queue.FlushCharacterAsync(2).WaitAsync(Wait);   // another character is not blocked
        await rig.Queue.FlushAsync().WaitAsync(Wait);             // and the plain barrier still never throws

        // The list a player loads meanwhile shows the change that storage has not accepted yet.
        Assert.Equal([new SocialEntry(2, SocialFlags.Friend)], rig.Queue.WithRetained(1, []));

        rig.Store.Heal();
        await rig.Queue.FlushCharacterAsync(1).WaitAsync(Wait);
        Assert.Equal(SocialFlags.Friend, rig.Store.Row(1, 2));
        Assert.False(rig.Queue.HasRetainedFailure(1));
    }

    [Fact]
    public async Task Logout_RetriesInTheBackground()
    {
        await using Rig rig = Rig.Create();
        rig.Store.FailNext(ThreeAttempts);
        rig.Queue.SetSocial(1, 2, SocialFlags.Friend);
        await rig.Queue.FlushAsync().WaitAsync(Wait);
        Assert.True(rig.Queue.HasRetainedFailure(1));

        rig.Queue.RequestRetry(1);
        await rig.Queue.FlushAsync().WaitAsync(Wait);             // ordered after the retry request
        Assert.Equal(SocialFlags.Friend, rig.Store.Row(1, 2));
        Assert.False(rig.Queue.HasRetainedFailure(1));
    }

    [Fact]
    public async Task Shutdown_RetriesOnce_AndThrowsNamingWhatIsStillNotDurable()
    {
        await using Rig rig = Rig.Create();
        rig.Store.FailNext(int.MaxValue);
        rig.Queue.SetSocial(7, 2, SocialFlags.Friend);
        rig.Queue.SaveGuild(Guild(4));
        await rig.Queue.FlushAsync().WaitAsync(Wait);

        InvalidOperationException stop = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Queue.StopAsync().WaitAsync(Wait));
        Assert.Contains("character 7", stop.Message);
        Assert.Contains("guild 4", stop.Message);
        InvalidOperationException again = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Queue.StopAsync().WaitAsync(Wait));
        Assert.Same(stop.Message, again.Message);                 // idempotent: the same outcome
    }

    [Fact]
    public async Task Shutdown_PersistsWhatARecoveredStoreAccepts()
    {
        await using Rig rig = Rig.Create();
        rig.Store.FailNext(ThreeAttempts);
        rig.Queue.SetSocial(7, 2, SocialFlags.Friend);
        await rig.Queue.FlushAsync().WaitAsync(Wait);

        await rig.Queue.StopAsync().WaitAsync(Wait);               // the final retry succeeds: no throw
        Assert.Equal(SocialFlags.Friend, rig.Store.Row(7, 2));
    }

    [Fact]
    public async Task RetainedGuildSnapshot_IsReplacedByANewerOne_AndRetriedAtTheNextGuildWrite()
    {
        await using Rig rig = Rig.Create();
        rig.Store.FailNext(ThreeAttempts);
        rig.Queue.SaveGuild(Guild(4, "old"));
        await rig.Queue.FlushAsync().WaitAsync(Wait);
        Assert.Empty(rig.Store.Guilds);

        rig.Queue.SaveGuild(Guild(4, "new"));
        rig.Queue.SaveGuild(Guild(5, "other"));
        await rig.Queue.FlushAsync().WaitAsync(Wait);
        Assert.Equal("new", rig.Store.Guilds[4].Motd);
        Assert.Equal("other", rig.Store.Guilds[5].Motd);
        await rig.Queue.StopAsync().WaitAsync(Wait);               // nothing left retained
    }

    [Fact]
    public async Task APurge_DiscardsRetainedRowsOfTheDeletedCharacter_SoARetryCannotBringThemBack()
    {
        await using Rig rig = Rig.Create();
        rig.Store.FailNext(ThreeAttempts);
        rig.Queue.SetSocial(1, 9, SocialFlags.Friend);             // retained: character 9 is about to be deleted
        await rig.Queue.FlushAsync().WaitAsync(Wait);
        Assert.True(rig.Queue.HasRetainedFailure(1));

        rig.Queue.PurgeCharacter(9);
        await rig.Queue.FlushAsync().WaitAsync(Wait);
        Assert.False(rig.Queue.HasRetainedFailure(1));
        Assert.Equal(0, rig.Queue.PendingRows);
        rig.Queue.RequestRetry(1);
        await rig.Queue.FlushAsync().WaitAsync(Wait);
        Assert.Equal(SocialFlags.None, rig.Store.Row(1, 9));
    }

    [Fact]
    public async Task ARefusedRowDoesNotStopTheQueue_AndRetainedRowsCountAgainstTheBound()
    {
        await using Rig rig = Rig.Create(new SocialWriteQueueOptions { MaxPendingPerCharacter = 2, RetryDelayMs = 1 });
        rig.Store.FailNext(int.MaxValue);
        Assert.True(rig.Queue.TrySetSocial(1, 10, SocialFlags.Friend));
        Assert.True(rig.Queue.TrySetSocial(1, 11, SocialFlags.Friend));
        await rig.Queue.FlushAsync().WaitAsync(Wait);
        Assert.False(rig.Queue.TrySetSocial(1, 12, SocialFlags.Friend));   // a long outage cannot grow memory either

        rig.Store.Heal();
        await rig.Queue.FlushCharacterAsync(1).WaitAsync(Wait);
        Assert.Equal(SocialFlags.Friend, rig.Store.Row(1, 10));
        Assert.Equal(SocialFlags.Friend, rig.Store.Row(1, 11));
        Assert.True(rig.Queue.TrySetSocial(1, 12, SocialFlags.Friend));
    }

    private static GuildData Guild(int id, string motd = "") => new(
        id, $"G{id}", 1, motd, "", 0, 0, 0, 0, 0, 0,
        [new GuildRankData(0, "Leader", 0xFF)], [new GuildMemberData(1, 0, "", "", 1, 0, 0)]);

    private sealed class Rig : IAsyncDisposable
    {
        private readonly ServiceProvider _services;

        private Rig(SocialWriteQueueOptions options)
        {
            _services = new ServiceCollection().AddScoped<ISocialStore>(_ => Store).BuildServiceProvider();
            Queue = new SocialWriteQueue(_services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, options);
            Queue.Start();
        }

        public RecordingStore Store { get; } = new();

        public SocialWriteQueue Queue { get; }

        public static Rig Create(SocialWriteQueueOptions? options = null) => new(options ?? new SocialWriteQueueOptions { RetryDelayMs = 1 });

        public async ValueTask DisposeAsync()
        {
            Store.Heal();
            try
            {
                await Queue.StopAsync().WaitAsync(Wait);
            }
            catch (InvalidOperationException)
            {
                // the test that left a failure retained asserts it itself
            }

            await _services.DisposeAsync();
        }
    }
}

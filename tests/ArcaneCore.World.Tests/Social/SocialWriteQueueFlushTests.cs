using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Social;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Social;

/// <summary>
/// <see cref="SocialWriteQueue.FlushAsync"/> is what the social delete hook awaits so a deletion
/// completes only after the queued purge was attempted (docs/integration/character-delete.md).
/// </summary>
public sealed class SocialWriteQueueFlushTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Flush_CompletesOnlyAfterEarlierWritesWereAttempted()
    {
        var store = new GatedStore();
        await using ServiceProvider services = new ServiceCollection().AddScoped<ISocialStore>(_ => store).BuildServiceProvider();
        var queue = new SocialWriteQueue(services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);
        queue.Start();
        queue.PurgeCharacter(5);

        Task flush = queue.FlushAsync();
        await store.PurgeStarted.Task.WaitAsync(Wait);
        Assert.False(flush.IsCompleted); // the purge is in flight: not yet attempted

        store.Release.TrySetResult();
        await flush.WaitAsync(Wait);
        Assert.Equal([5], store.Purged);
        await queue.StopAsync();
    }

    [Fact]
    public async Task Flush_AfterAFailedWrite_StillCompletes()
    {
        var store = new GatedStore { Fail = true };
        await using ServiceProvider services = new ServiceCollection().AddScoped<ISocialStore>(_ => store).BuildServiceProvider();
        var queue = new SocialWriteQueue(services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);
        queue.Start();
        queue.PurgeCharacter(6);

        await queue.FlushAsync().WaitAsync(Wait); // three failed attempts are logged and dropped, then the marker completes
        await queue.StopAsync();
    }

    [Fact]
    public async Task Flush_OfAnIdleOrUnstartedQueue_CompletesImmediately()
    {
        await using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        var queue = new SocialWriteQueue(services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);
        await queue.FlushAsync().WaitAsync(Wait); // never started
        queue.Start();
        await queue.FlushAsync().WaitAsync(Wait); // nothing queued
        await queue.StopAsync();
    }

    [Fact]
    public async Task Flush_WithoutAStore_StillCompletes()
    {
        await using ServiceProvider services = new ServiceCollection().BuildServiceProvider(); // no ISocialStore registered
        var queue = new SocialWriteQueue(services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);
        queue.Start();
        queue.PurgeCharacter(7);
        await queue.FlushAsync().WaitAsync(Wait);
        await queue.StopAsync();
    }

    private sealed class GatedStore : ISocialStore
    {
        public TaskCompletionSource PurgeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Fail { get; init; }

        public List<int> Purged { get; } = [];

        public async Task PurgeCharacterAsync(int characterId, CancellationToken cancellationToken = default)
        {
            PurgeStarted.TrySetResult();
            if (Fail)
            {
                throw new IOException("injected purge failure");
            }

            await Release.Task.WaitAsync(cancellationToken);
            lock (Purged)
            {
                Purged.Add(characterId);
            }
        }

        public Task<IReadOnlyList<SocialEntry>> GetSocialAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SocialEntry>>([]);

        public Task SetSocialAsync(int characterId, int otherId, SocialFlags flags, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<GuildData>> GetGuildsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GuildData>>([]);

        public Task SaveGuildAsync(GuildData guild, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteGuildAsync(int guildId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

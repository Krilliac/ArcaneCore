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
using ArcaneCore.World.Features;

namespace ArcaneCore.World.Tests.Social;

/// <summary>
/// The guild load runs off the world thread and finishes with a world invocation. <see cref="WorldRuntime.Stop"/> runs before the
/// features stop (WorldTestHost and the daemon alike) and, since the honor shutdown fix, cancels every pending invocation; a load
/// whose store read returns late must treat that cancellation as "the world is gone" even though the feature itself has not
/// started stopping yet, or <see cref="SocialFeature.GuildsLoaded"/> faults and <see cref="WorldFeatures.StopWorldFeaturesAsync"/>
/// reports the whole feature as failed (CI run 37183649322 on 2f7a624).
/// </summary>
public sealed class SocialGuildLoadShutdownTests
{
    [Fact]
    public async Task AGuildLoadThatFinishesAfterTheWorldStopped_CompletesQuietly_AndTheFeaturesStopClean()
    {
        var store = new GatedStore();
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton<ISocialStore>(store));
        SocialFeature social = host.WorldServices.GetServices<IWorldFeature>().OfType<SocialFeature>().Single();
        await store.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        host.World.Stop();                       // the world thread is gone; nothing will ever run the load's invocation
        store.ReleaseRead.SetResult();           // ...and only now does the store answer

        await social.GuildsLoaded.WaitAsync(TimeSpan.FromSeconds(10));   // before the fix: TaskCanceledException
        // Disposal stops the features (WorldTestHost.DisposeAsync); before the fix it threw AggregateException for this one.
    }

    private sealed class GatedStore : ISocialStore
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<GuildData>> GetGuildsAsync(CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await ReleaseRead.Task.WaitAsync(cancellationToken);
            return [];
        }

        public Task<IReadOnlyList<SocialEntry>> GetSocialAsync(int characterId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SocialEntry>>([]);
        public Task SetSocialAsync(int characterId, int otherId, SocialFlags flags, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveGuildAsync(GuildData guild, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteGuildAsync(int guildId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

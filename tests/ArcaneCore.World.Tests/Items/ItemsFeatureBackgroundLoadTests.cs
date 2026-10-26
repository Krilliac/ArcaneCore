using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Items;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Items;

/// <summary><see cref="ItemsFeature.StartLoadInBackground"/>: one shared load, one fault per failed load, and a backoff before the next try.</summary>
public sealed class ItemsFeatureBackgroundLoadTests
{
    private const string Site = "items.background-load";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Concurrent_callers_share_one_failing_load_and_a_retry_inside_the_backoff_does_not_call_the_source()
    {
        var source = new FailingSource();
        await using ServiceProvider provider = new ServiceCollection().AddSingleton<IItemTemplateSource>(source).BuildServiceProvider();
        var feature = new ItemsFeature(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ItemsFeature>.Instance);
        long faultsBefore = Faults();

        Parallel.For(0, 16, _ => feature.StartLoadInBackground());
        await source.Started.WaitAsync(Budget);
        source.Fail.SetResult(); // every caller had the chance to start its own load while the first one was held
        await WaitUntilAsync(() => Faults() > faultsBefore);

        Assert.Equal(1, source.Calls);
        Assert.Equal(1, Faults() - faultsBefore);
        Assert.False(feature.IsLoaded);

        feature.StartLoadInBackground(); // inside BackgroundRetryDelay: nothing starts
        await Task.Delay(250);
        Assert.Equal(1, source.Calls);
        Assert.Equal(1, Faults() - faultsBefore);
    }

    [Fact]
    public async Task A_loaded_store_with_no_templates_is_loaded()
    {
        await using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
        var feature = new ItemsFeature(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ItemsFeature>.Instance);
        Assert.False(feature.IsLoaded);

        await feature.EnsureLoadedAsync();

        Assert.True(feature.IsLoaded);
        Assert.Equal(0, feature.Templates.Count);
    }

    private static long Faults() => FaultLedger.Shared.Snapshot().Where(s => s.Site == Site).Sum(s => s.Count);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Budget);
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FailingSource : IItemTemplateSource
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);
        public Task Started => _started.Task;
        public TaskCompletionSource Fail { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<ItemTemplate>> LoadTemplatesAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            _started.TrySetResult();
            await Fail.Task.ConfigureAwait(false);
            throw new InvalidOperationException("item_template unavailable");
        }

        public Task<IReadOnlyList<StartingItem>> LoadStartingItemsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<StartingItem>>([]);
    }
}

using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Persistence;

/// <summary>The <c>persist/save-slow</c> anomaly of <see cref="CharacterPersistenceBarrier"/>.</summary>
[Collection(PersistAnomalyCollection.Name)]
public sealed class CharacterPersistenceBarrierAnomalyTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private sealed class Participant(TimeSpan delay, bool fail = false) : ICharacterPersistenceParticipant
    {
        public string Name => "SlowParticipant";

        public async Task FlushCharacterAsync(int characterId, CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            if (fail)
            {
                throw new IOException("participant store down");
            }
        }
    }

    private static CharacterPersistenceBarrier Barrier(ServiceProvider provider, TimeSpan? threshold)
        => threshold is { } value
            ? new CharacterPersistenceBarrier(provider) { SlowFlushThreshold = value }
            : new CharacterPersistenceBarrier(provider);

    private static ServiceProvider Services(Participant participant)
        => new ServiceCollection().AddSingleton<ICharacterPersistenceParticipant>(participant).BuildServiceProvider();

    [Fact]
    public async Task SaveSlow_FiresOnce_WhenTheFlushTakesLongerThanTheThreshold()
    {
        await using ServiceProvider provider = Services(new Participant(TimeSpan.FromMilliseconds(150)));
        CharacterPersistenceBarrier barrier = Barrier(provider, TimeSpan.FromMilliseconds(50));
        using AnomalyCapture capture = PersistAnomalies.Capture();

        CharacterPersistenceFlushResult result = await barrier.TryFlushCharacterAsync(61, Wait);

        Assert.True(result.IsDurable);
        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Equal("persist/save-slow", notice.Key);
        Assert.Equal(AnomalySeverity.Info, notice.Severity);
        Assert.Equal("character 61", notice.Subject);
        Assert.Contains("barrier=CharacterPersistenceBarrier", notice.Detail, StringComparison.Ordinal);
        Assert.Contains("thresholdMs=50", notice.Detail, StringComparison.Ordinal);
        Assert.Contains("failures=0", notice.Detail, StringComparison.Ordinal);
        long elapsed = long.Parse(notice.Detail.Split("elapsedMs=")[1].Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(elapsed >= 100, $"elapsedMs={elapsed} for a 150 ms flush");
    }

    [Fact]
    public async Task SaveSlow_StillFires_AndCountsTheFailures_WhenASlowFlushAlsoFailed()
    {
        await using ServiceProvider provider = Services(new Participant(TimeSpan.FromMilliseconds(150), fail: true));
        CharacterPersistenceBarrier barrier = Barrier(provider, TimeSpan.FromMilliseconds(50));
        using AnomalyCapture capture = PersistAnomalies.Capture();

        CharacterPersistenceFlushResult result = await barrier.TryFlushCharacterAsync(62, Wait);

        Assert.False(result.IsDurable);
        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Equal("persist/save-slow", notice.Key);
        Assert.Contains("failures=1", notice.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveSlow_IsSilent_ForAFastFlush_UnderTheDefaultThreshold()
    {
        await using ServiceProvider provider = Services(new Participant(TimeSpan.FromMilliseconds(5)));
        CharacterPersistenceBarrier barrier = Barrier(provider, threshold: null);
        using AnomalyCapture capture = PersistAnomalies.Capture();

        CharacterPersistenceFlushResult result = await barrier.TryFlushCharacterAsync(63, Wait);

        Assert.True(result.IsDurable);
        Assert.Equal(TimeSpan.FromSeconds(2), barrier.SlowFlushThreshold);
        Assert.Empty(capture.Notices);
    }

    [Fact]
    public async Task SaveSlow_IsSilent_WhenTheThresholdIsZero_EvenForASlowFlush()
    {
        await using ServiceProvider provider = Services(new Participant(TimeSpan.FromMilliseconds(150)));
        CharacterPersistenceBarrier barrier = Barrier(provider, TimeSpan.Zero);
        using AnomalyCapture capture = PersistAnomalies.Capture();

        CharacterPersistenceFlushResult result = await barrier.TryFlushCharacterAsync(64, Wait);

        Assert.True(result.IsDurable);
        Assert.Empty(capture.Notices);
    }
}

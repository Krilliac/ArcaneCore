using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Supervision;

public sealed class ManualDelaysTests
{
    /// <summary>
    /// A watch loop that re-requests a delay as soon as the previous one ends, pulsed from another thread at the moment it registers a delay.
    /// Before the fix a delay could be counted in RequestCount yet not be pending when PulseAsync released, so the pulse never saw a new request.
    /// </summary>
    [Fact]
    public async Task PulseAsync_WhileADelayIsBeingRegistered_NeverMissesIt()
    {
        for (int i = 0; i < 1_500; i++)
        {
            var delays = new ManualDelays();
            using var go = new ManualResetEventSlim();
            Task loop = Task.Run(async () =>
            {
                go.Wait();
                await delays.Delay(TimeSpan.FromSeconds(60), CancellationToken.None);
                await delays.Delay(TimeSpan.FromSeconds(60), CancellationToken.None);
            });
            Task pulse = Task.Run(async () =>
            {
                go.Wait();
                await delays.PulseAsync(timeoutMs: 5_000);
            });
            go.Set();
            await pulse; // would time out (and fail) in the old kit when the first delay was counted but missed by the release
            await delays.ReleaseUntilAsync(() => loop.IsCompleted, "the loop's second delay to be released");
            await loop;
        }
    }
}

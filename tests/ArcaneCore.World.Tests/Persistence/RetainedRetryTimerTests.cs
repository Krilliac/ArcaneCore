using ArcaneCore.World.Persistence;
using Xunit;

namespace ArcaneCore.World.Tests.Persistence;

/// <summary>The shared retained-retry timer: one marker per tick while something is retained, never stacked, never after stop.</summary>
public sealed class RetainedRetryTimerTests
{
    private static async Task<bool> BecomesTrueAsync(Func<bool> condition, int seconds = 10)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(5);
        }

        return true;
    }

    [Fact]
    public async Task PostsOnePerTick_AndNoNewMarkerUntilTheSweepIsFinished()
    {
        int posts = 0;
        var timer = new RetainedRetryTimer(TimeSpan.FromMilliseconds(20), () => true, () =>
        {
            Interlocked.Increment(ref posts);
            return true;
        });
        timer.Start();
        Assert.True(await BecomesTrueAsync(() => Volatile.Read(ref posts) == 1), "no marker was posted");

        await Task.Delay(200); // ten more ticks: the consumer has not reported the first marker handled
        Assert.Equal(1, Volatile.Read(ref posts));
        Assert.False(timer.TryPost());

        timer.SweepFinished();
        Assert.True(await BecomesTrueAsync(() => Volatile.Read(ref posts) == 2), "no marker after the sweep finished");
        await timer.StopAsync();
    }

    [Fact]
    public async Task PostsNothingWhileNothingIsRetained()
    {
        int posts = 0;
        bool retained = false;
        var timer = new RetainedRetryTimer(TimeSpan.FromMilliseconds(20), () => Volatile.Read(ref retained), () =>
        {
            Interlocked.Increment(ref posts);
            return true;
        });
        timer.Start();
        await Task.Delay(200);
        Assert.Equal(0, Volatile.Read(ref posts));

        Volatile.Write(ref retained, true);
        Assert.True(await BecomesTrueAsync(() => Volatile.Read(ref posts) == 1));
        await timer.StopAsync();
    }

    [Fact]
    public void AFailedPost_ReleasesTheFlag_SoTheNextTickMayTryAgain()
    {
        bool open = false;
        int posts = 0;
        var timer = new RetainedRetryTimer(TimeSpan.FromHours(1), () => true, () =>
        {
            if (open)
            {
                posts++;
            }

            return open;
        });

        Assert.False(timer.TryPost()); // channel closed
        open = true;
        Assert.True(timer.TryPost());
        Assert.Equal(1, posts);
    }

    [Fact]
    public async Task StopIsIdempotent_AndNothingPostsAfterIt()
    {
        int posts = 0;
        var timer = new RetainedRetryTimer(TimeSpan.FromMilliseconds(10), () => true, () =>
        {
            Interlocked.Increment(ref posts);
            return true;
        });
        timer.Start();
        Assert.True(await BecomesTrueAsync(() => Volatile.Read(ref posts) == 1));
        await timer.StopAsync();
        await timer.StopAsync();

        timer.SweepFinished();
        int atStop = Volatile.Read(ref posts);
        await Task.Delay(150);
        Assert.Equal(atStop, Volatile.Read(ref posts));
    }
}

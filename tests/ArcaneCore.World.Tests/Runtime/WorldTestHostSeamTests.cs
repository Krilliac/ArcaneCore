using System.Diagnostics;
using ArcaneCore.Game.Maps;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests.Runtime;

/// <summary>
/// The harness seams of <see cref="WorldTestHost"/> on the manual clock: the game-time <see cref="TimeProvider"/>, the delivery barrier
/// failing fast when a client's session is gone (even after the test disposed the client), the per-tick cap on a self-reposting command,
/// and the pending-work hang guard leaving zero-length barriers alone.
/// </summary>
[Collection("World tick load")]
public sealed class WorldTestHostSeamTests
{
    [Fact]
    public async Task ManualHost_ProvidesAGameTimeProvider_ThatFollowsTheManualClock()
    {
        await using WorldTestHost host = WorldTestHost.Start(clock: TestClock.Manual());
        var provider = (TimeProvider)host.WorldServices.GetService(typeof(TimeProvider))!;
        DateTimeOffset before = provider.GetUtcNow();
        long stampBefore = provider.GetTimestamp();

        await host.AdvanceAsync(1000);

        Assert.True(host.World.IsManualClock);
        Assert.Equal(before + TimeSpan.FromMilliseconds(1000), provider.GetUtcNow());
        Assert.Equal(stampBefore + 1000, provider.GetTimestamp());
        Assert.Equal(host.World.Clock.UtcNow, provider.GetUtcNow());
    }

    [Fact]
    public async Task AClosedSession_FailsWaitForDelivery_Within2Seconds_EvenAfterTheClientWasDisposed()
    {
        await using WorldTestHost host = WorldTestHost.Start(clock: TestClock.Manual());
        WorldTestClient client = await host.EnterWorldAsync("SEAMCLOSED", "Seamclosed");
        await host.SettleAsync();
        int id = (await host.Accounts.FindByUsernameAsync("SEAMCLOSED"))!.Id;
        host.Registry.Find(id)!.Kick();
        Assert.True(await client.IsClosedByServerAsync());
        await WorldTestHost.WaitForAsync(() => host.Registry.Find(id) is null, "the kicked session to be unregistered");
        try
        {
            await client.SendAsync(WorldOpcode.CmsgPlayedTime, []);
        }
        catch (IOException)
        {
        }

        await client.DisposeAsync(); // the endpoint is gone from the socket; the host remembers it from the connect

        var wall = Stopwatch.StartNew();
        await Assert.ThrowsAsync<WorldHangException>(() => host.SettleAsync(TimeSpan.FromSeconds(60)));

        Assert.True(wall.Elapsed < TimeSpan.FromSeconds(2), $"the barrier waited {wall.Elapsed.TotalSeconds:F1} s");
    }

    [Fact]
    public async Task ASelfRepostingCommand_RunsOncePerTick_NotUnbounded()
    {
        await using WorldTestHost host = WorldTestHost.Start(clock: TestClock.Manual());
        WorldRuntime world = host.World;
        int runs = 0;
        int stop = 0;
        void Repost()
        {
            Interlocked.Increment(ref runs);
            if (Volatile.Read(ref stop) == 0)
            {
                world.Post(Repost);
            }
        }

        await host.SettleAsync();
        long tickBefore = await world.InvokeAsync(() => world.Clock.TickNumber);
        world.Post(Repost);
        await host.StepAsync(3);
        (int seenRuns, long tickAfter) = await world.InvokeAsync(() => (Volatile.Read(ref runs), world.Clock.TickNumber));
        Volatile.Write(ref stop, 1);

        Assert.InRange(seenRuns, 3, (int)(tickAfter - tickBefore) + 1);
        await host.SettleAsync(); // the barrier still completes while a command keeps reposting
    }

    [Fact]
    public async Task HungPendingWork_FailsTheAdvance_ButNotAZeroLengthBarrier()
    {
        await using WorldTestHost host = WorldTestHost.Start(clock: TestClock.Manual());
        WorldRuntime world = host.World;
        await host.SettleAsync();
        TimeSpan guard = TimeSpan.FromSeconds(2); // long enough that the hold cannot trip before the setup below finishes on a loaded machine
        world.PendingWorkHangGuard = guard;
        using IDisposable work = world.PendingWork.Begin("seam-hung");

        Task advance = world.AdvanceTicksAsync(1);
        // Tick A runs the command that adds the advance; tick B (the next InvokeAsync) holds it and arms the hang guard.
        await world.InvokeAsync(() => 0);
        await world.InvokeAsync(() => 0);

        // Force the trip-tick race. RunCommands drains only what was queued at tick entry, and a settle skips an advance added in its own tick:
        // the barrier add and the sleep, both posted in tick B, run in tick B+1 (the barrier is skipped by that tick's settle, the sleep outlasts
        // the guard), so tick B+2 trips the guard while the zero-length barrier still sits in the advance list. The old code failed it there.
        Task? barrier = null;
        await world.InvokeAsync(() =>
        {
            barrier = world.AdvanceTicksAsync(0);
            world.Post(() => Thread.Sleep(guard + TimeSpan.FromMilliseconds(500)));
            return 0;
        });

        await barrier!.WaitAsync(TimeSpan.FromSeconds(30)); // completes in the zero-diff tick; throws PendingWorkHangException on the old code
        PendingWorkHangException ex = await Assert.ThrowsAsync<PendingWorkHangException>(() => advance.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Contains("seam-hung", ex.Message);
    }
}

using System.Diagnostics;
using ArcaneCore.Game.Maps;
using Xunit;

namespace ArcaneCore.Game.Tests.Maps;

/// <summary>The manual clock as a tick barrier: exact steps, deterministic command drain, pending-work holds, idle wakes.</summary>
[Collection("World tick load")]
public sealed class ManualTickBarrierTests
{
    private static WorldRuntime StartManual(uint step = 50, long startMs = 0)
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        world.UseManualClockFrom(startMs, new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), step);
        world.Start();
        return world;
    }

    [Fact]
    public async Task StepAsync_RunsExactlyNTicksOfTheStep()
    {
        using WorldRuntime world = StartManual(step: 40);
        try
        {
            await world.InvokeAsync(() => 0);
            var diffs = new List<uint>();
            world.Updated += diffs.Add;
            await world.AdvanceTicksAsync(5);
            await world.InvokeAsync(() => 0);

            Assert.Equal([40u, 40u, 40u, 40u, 40u], diffs.Where(d => d > 0));
            Assert.Equal(200u, world.NowMs);
            await world.AdvanceTicksAsync(0);
            Assert.Equal(200u, world.NowMs);
            Assert.Throws<ArgumentOutOfRangeException>(() => { _ = world.AdvanceTicksAsync(-1); });
        }
        finally
        {
            world.Stop();
        }
    }

    [Fact]
    public async Task AdvanceAsync_EndsOnTheExactMillisecond_WithAPartialLastStep()
    {
        using WorldRuntime world = StartManual();
        try
        {
            await world.InvokeAsync(() => 0);
            var diffs = new List<uint>();
            world.Updated += diffs.Add;
            await world.AdvanceClockAsync(120);
            await world.InvokeAsync(() => 0);

            Assert.Equal([50u, 50u, 20u], diffs.Where(d => d > 0));
            Assert.Equal(120u, world.NowMs);
        }
        finally
        {
            world.Stop();
        }
    }

    [Fact]
    public async Task CommandsPostedBeforeAStep_AllRunBeforeItsFirstAdvancingTick_PastTheCommandBudget()
    {
        using WorldRuntime world = StartManual();
        world.Options.MaxCommandsPerTick = 3;
        try
        {
            await world.InvokeAsync(() => 0);
            const int posted = 100;
            int ran = 0;
            int ranAtFirstAdvance = -1;
            world.Updated += diff =>
            {
                if (diff > 0 && ranAtFirstAdvance < 0)
                {
                    ranAtFirstAdvance = ran;
                }
            };
            for (int i = 0; i < posted; i++)
            {
                world.Post(() => ran++);
            }

            await world.AdvanceTicksAsync(1);

            Assert.Equal(posted, ranAtFirstAdvance);
        }
        finally
        {
            world.Stop();
        }
    }

    [Fact]
    public async Task PendingWork_HoldsTheNextAdvancingTick_UntilDisposed()
    {
        using WorldRuntime world = StartManual();
        try
        {
            await world.InvokeAsync(() => 0);
            IDisposable work = world.PendingWork.Begin("test-load");
            Assert.Equal(1, world.PendingWork.Count);
            Assert.Equal(["test-load"], world.PendingWork.Names());

            Task advance = world.AdvanceTicksAsync(2);
            for (int i = 0; i < 20; i++)
            {
                await world.InvokeAsync(() => i); // zero-diff ticks keep draining commands while the advance is held
            }

            Assert.False(advance.IsCompleted);
            Assert.Equal(0u, world.NowMs);

            work.Dispose();
            work.Dispose(); // idempotent
            await advance;

            Assert.Equal(0, world.PendingWork.Count);
            Assert.Equal(100u, world.NowMs);
        }
        finally
        {
            world.Stop();
        }
    }

    [Fact]
    public async Task HungPendingWork_FailsTheAdvance_NamingTheWork_AfterTheHangGuard()
    {
        using WorldRuntime world = StartManual();
        world.PendingWorkHangGuard = TimeSpan.FromMilliseconds(200);
        try
        {
            await world.InvokeAsync(() => 0);
            using IDisposable work = world.PendingWork.Begin("never-finishes");
            var wall = Stopwatch.StartNew();

            PendingWorkHangException ex = await Assert.ThrowsAsync<PendingWorkHangException>(() => world.AdvanceTicksAsync(1));

            Assert.Contains("never-finishes", ex.Message);
            Assert.True(wall.Elapsed >= TimeSpan.FromMilliseconds(150), $"failed after only {wall.ElapsedMilliseconds} ms");
            Assert.Equal(0u, world.NowMs);
        }
        finally
        {
            world.Stop();
        }
    }

    [Fact]
    public async Task IdleWakes_RunNoAdvancingTicks_InDeterministicMode()
    {
        using WorldRuntime world = StartManual(startMs: 777);
        try
        {
            await world.InvokeAsync(() => 0);
            long tickBefore = world.Clock.TickNumber;
            var diffs = new List<uint>();
            world.Updated += diffs.Add;

            for (int i = 0; i < 10; i++)
            {
                await world.InvokeAsync(() => i); // each wake runs a zero-diff tick
            }

            Assert.True(world.Clock.TickNumber >= tickBefore + 10);
            Assert.All(diffs, d => Assert.Equal(0u, d));
            Assert.Equal(777u, world.NowMs);
            Assert.Equal(777L, world.Clock.ElapsedMs);
        }
        finally
        {
            world.Stop();
        }
    }

    [Fact]
    public async Task DeterministicBudgets_CommandAndPathBudgets_AreCountOnly()
    {
        // Scope note: only the runtime's command budget is deterministic in this phase; the creature path budgets belong to a later phase.
        using WorldRuntime world = StartManual();
        world.Options.MaxCommandsPerTick = 1;
        world.Options.CommandTimeBudgetMs = 1;
        try
        {
            await world.InvokeAsync(() => 0);
            int ran = 0;
            for (int i = 0; i < 50; i++)
            {
                world.Post(() =>
                {
                    var spin = Stopwatch.StartNew();
                    while (spin.Elapsed < TimeSpan.FromMilliseconds(0.2))
                    {
                        // a slow command must not push the rest of the queue to a later tick
                    }

                    ran++;
                });
            }

            long tickBefore = world.Clock.TickNumber;
            await world.AdvanceTicksAsync(0);

            Assert.Equal(50, ran);
            Assert.True(world.Clock.TickNumber - tickBefore <= 2);

            using WorldRuntime real = TestWorld.CreateRuntime();
            real.Options.MaxCommandsPerTick = 2;
            int realRan = 0;
            for (int i = 0; i < 10; i++)
            {
                real.Post(() => realRan++);
            }

            real.RunTick(50);
            Assert.Equal(2, realRan); // the real clock keeps its count budget
        }
        finally
        {
            world.Stop();
        }
    }
}

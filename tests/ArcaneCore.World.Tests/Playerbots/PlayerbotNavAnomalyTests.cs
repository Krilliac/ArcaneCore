using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The <c>bot-nav</c> anomaly keys (Hardening Wave H1): <c>plan-stale</c> and <c>plan-dropped</c> of <see cref="PlayerbotAsyncNavigationPlan"/>,
/// and the <c>plan-dropped</c> of <see cref="PlayerbotNavigationExecutor"/>'s two capacity limits. Each fires once per plan on the failure it
/// names and stays silent on the healthy path. Reports are read through <see cref="BotAnomalyCapture"/> (own thread only), because the ledger
/// is process-wide and the executor tests of other classes exhaust the same capacities.
/// </summary>
public sealed class PlayerbotNavAnomalyTests
{
    private static string Key(string code) => PlayerbotAnomalies.Key("nav", code);

    private static PlayerbotNavigationResult Good(Vector3 destination)
        => new(PlayerbotNavigationResultStatus.Completed, PathResult.StraightLine(Vector3.Zero, destination, PathType.Normal), destination,
            false, false, default, TimeSpan.Zero);

    private static PlayerbotNavigationResult Failed(PlayerbotNavigationResultStatus status)
        => new(status, null, default, false, false, default, TimeSpan.Zero);

    /// <summary>Run <paramref name="body"/> on the world thread with a standing bot and a capture of its reports.</summary>
    private static async Task WithBotAsync(Action<Player, WorldSession, AnomalyCapture> body, bool chaosProbe = false)
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            session.IsInChaosProbe = chaosProbe;
            Player player = session.Player!;
            using AnomalyCapture capture = BotAnomalyCapture.For(player);
            await host.OnWorldAsync(() => body(player, session, capture));
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    // --- plan-stale ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PlanStale_Fires_OncePerPlan_WhenAResultIsForAnotherMap()
    {
        await WithBotAsync((player, session, capture) =>
        {
            Vector3 start = PlayerbotMotion.CurrentPosition(player);
            var plan = new PlayerbotAsyncNavigationPlan(player.MapId, start, owner: player, goal: new Vector3(10, 20, 30));
            var wrongMap = new PlayerbotNavigationRequest(player.MapId + 1, start, new Vector3(5, 0, 0), PathOptions.Default);

            Assert.False(plan.AddResult(wrongMap, Good(new Vector3(5, 0, 0))));
            Assert.Equal(1, capture.Count(Key("plan-stale")));
            AnomalyNotice notice = capture.Notices.Single();
            Assert.Equal(AnomalySeverity.Info, notice.Severity);
            Assert.Contains("stale: result map", notice.Detail, StringComparison.Ordinal);
            Assert.Contains("toward (10.0,20.0,30.0)", notice.Detail, StringComparison.Ordinal);

            Assert.False(plan.AddResult(wrongMap, Good(new Vector3(6, 0, 0))));
            Assert.Equal(1, capture.Count(Key("plan-stale")));
        });
    }

    [Fact]
    public async Task PlanStale_Fires_WhenTheLeaseEndsAndTheBotHasMovedAwayFromThePlanStart()
    {
        await WithBotAsync((player, session, capture) =>
        {
            Vector3 start = PlayerbotMotion.CurrentPosition(player) + new Vector3(25f, 0, 0);
            var plan = new PlayerbotAsyncNavigationPlan(player.MapId, start, owner: player);

            plan.OnLeaseReleased(executorDisposed: false);

            Assert.Equal(1, capture.Count(Key("plan-stale")));
            Assert.Contains("yd from plan start", capture.Notices.Single().Detail, StringComparison.Ordinal);
            Assert.False(capture.Any(Key("plan-dropped")), capture.Describe());
        });
    }

    [Fact]
    public async Task PlanStale_StaysSilent_ForAMatchingMapAndAGoodAnswer()
    {
        await WithBotAsync((player, session, capture) =>
        {
            Vector3 start = PlayerbotMotion.CurrentPosition(player);
            var plan = new PlayerbotAsyncNavigationPlan(player.MapId, start, owner: player);
            var request = new PlayerbotNavigationRequest(player.MapId, start, new Vector3(5, 0, 0), PathOptions.Default);

            Assert.True(plan.AddResult(request, Good(new Vector3(5, 0, 0))));
            plan.Completed = true;
            plan.OnLeaseReleased(executorDisposed: false);

            Assert.Empty(capture.Notices);
        });
    }

    [Fact]
    public async Task PlanStale_StaysSilent_ForABotInAChaosProbe()
    {
        await WithBotAsync((player, session, capture) =>
        {
            Vector3 start = PlayerbotMotion.CurrentPosition(player);
            var plan = new PlayerbotAsyncNavigationPlan(player.MapId, start, owner: player);
            var wrongMap = new PlayerbotNavigationRequest(player.MapId + 1, start, new Vector3(5, 0, 0), PathOptions.Default);

            Assert.False(plan.AddResult(wrongMap, Good(new Vector3(5, 0, 0))));
            plan.OnLeaseReleased(executorDisposed: false);

            Assert.Empty(capture.Notices);
        }, chaosProbe: true);
    }

    // --- plan-dropped (plan) ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlanDropped_Fires_OncePerPlan_WhenAQueryDoesNotComplete(bool faulted)
    {
        PlayerbotNavigationResultStatus status = faulted ? PlayerbotNavigationResultStatus.Faulted : PlayerbotNavigationResultStatus.Canceled;
        await WithBotAsync((player, session, capture) =>
        {
            Vector3 start = PlayerbotMotion.CurrentPosition(player);
            var plan = new PlayerbotAsyncNavigationPlan(player.MapId, start, owner: player);
            var request = new PlayerbotNavigationRequest(player.MapId, start, new Vector3(5, 0, 0), PathOptions.Default);

            Assert.False(plan.AddResult(request, Failed(status)));
            Assert.Equal(1, capture.Count(Key("plan-dropped")));
            AnomalyNotice notice = capture.Notices.Single();
            Assert.Equal(AnomalySeverity.Warning, notice.Severity);
            Assert.Contains($"status={status}", notice.Detail, StringComparison.Ordinal);

            Assert.False(plan.AddResult(request, Failed(status)));
            plan.OnLeaseReleased(executorDisposed: false);
            Assert.Equal(1, capture.Count(Key("plan-dropped")));
        });
    }

    [Fact]
    public async Task PlanDropped_Fires_WhenTheCacheIsExhausted()
    {
        await WithBotAsync((player, session, capture) =>
        {
            Vector3 start = PlayerbotMotion.CurrentPosition(player);
            var plan = new PlayerbotAsyncNavigationPlan(player.MapId, start, owner: player);
            for (int i = 0; i < PlayerbotAsyncNavigationPlan.MaxQueries; i++)
            {
                var destination = new Vector3(10 + i, 0, 0);
                Assert.True(plan.AddResult(new PlayerbotNavigationRequest(player.MapId, start, destination, PathOptions.Default), Good(destination)), $"query {i}");
            }

            Assert.Empty(capture.Notices); // a full cache is not yet a drop

            var over = new Vector3(5_000, 0, 0);
            Assert.False(plan.AddResult(new PlayerbotNavigationRequest(player.MapId, start, over, PathOptions.Default), Good(over)));
            Assert.Equal(1, capture.Count(Key("plan-dropped")));
            Assert.Contains("cache exhausted", capture.Notices.Single().Detail, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task PlanDropped_Fires_WhenTheLeaseEndsBeforeThePlanCompleted_AndTheBotStoodStill()
    {
        await WithBotAsync((player, session, capture) =>
        {
            var plan = new PlayerbotAsyncNavigationPlan(player.MapId, PlayerbotMotion.CurrentPosition(player), owner: player);

            plan.OnLeaseReleased(executorDisposed: false);

            Assert.Equal(1, capture.Count(Key("plan-dropped")));
            Assert.Contains("plan abandoned before completion", capture.Notices.Single().Detail, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task PlanDropped_StaysSilent_WhenThePlanCompleted_OrTheExecutorIsShuttingDown_OrThereIsNoOwner()
    {
        await WithBotAsync((player, session, capture) =>
        {
            Vector3 start = PlayerbotMotion.CurrentPosition(player);

            var completed = new PlayerbotAsyncNavigationPlan(player.MapId, start, owner: player) { Completed = true };
            completed.OnLeaseReleased(executorDisposed: false);

            var shuttingDown = new PlayerbotAsyncNavigationPlan(player.MapId, start, owner: player);
            shuttingDown.OnLeaseReleased(executorDisposed: true);

            var ownerless = new PlayerbotAsyncNavigationPlan(player.MapId, start);
            ownerless.OnLeaseReleased(executorDisposed: false);
            Assert.False(ownerless.AddResult(new PlayerbotNavigationRequest(player.MapId, start, new Vector3(5, 0, 0), PathOptions.Default),
                Failed(PlayerbotNavigationResultStatus.Faulted)));

            Assert.Empty(capture.Notices);
        });
    }

    // --- plan-dropped (executor) -----------------------------------------------------------------------------------------------------

    [Fact]
    public void PlanDropped_Fires_WhenThePlanLeaseCapacityIsExhausted_AndNotBefore()
    {
        using AnomalyCapture capture = BotAnomalyCapture.OnThisThread(n => n.Subject == "navigation executor");
        using var executor = new PlayerbotNavigationExecutor(() => new ImmediatePathfinder());
        var leases = new List<IDisposable>();
        try
        {
            for (int i = 0; i < PlayerbotNavigationExecutor.DefaultCapacity; i++)
            {
                Assert.True(executor.TryAcquirePlan(out IDisposable lease));
                leases.Add(lease);
            }

            Assert.Empty(capture.Notices); // all 32 fit: silent

            Assert.False(executor.TryAcquirePlan(out _));
            Assert.Equal(1, capture.Count(Key("plan-dropped")));
            AnomalyNotice notice = capture.Notices.Single();
            Assert.Equal(AnomalySeverity.Warning, notice.Severity);
            Assert.Equal("plan lease capacity exhausted", notice.Detail);
        }
        finally
        {
            foreach (IDisposable lease in leases) lease.Dispose();
        }
    }

    [Fact]
    public async Task PlanDropped_Fires_WhenTheJobCapacityIsExhausted_ForARequestOfNoPlan_AndNotBefore()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using AnomalyCapture capture = BotAnomalyCapture.OnThisThread(n => n.Subject == "navigation executor");
        await using var executor = new PlayerbotNavigationExecutor(() => new GatedPathfinder(gate), capacity: 1);
        var start = new Vector3(123.4f, 567.8f, 9f);
        var request = new PlayerbotNavigationRequest(9_123, start, new Vector3(130, 570, 9), PathOptions.Default);

        Assert.True(executor.TrySubmit(request, out PlayerbotNavigationJob first));
        try
        {
            Assert.Empty(capture.Notices); // the one slot was free: silent

            Assert.False(executor.TrySubmit(request, out _));
            Assert.Equal(1, capture.Count(Key("plan-dropped")));
            string detail = capture.Notices.Single().Detail;
            Assert.StartsWith("executor job capacity exhausted on map 9123 from (123.4,567.8,9.0)", detail, StringComparison.Ordinal);
        }
        finally
        {
            gate.SetResult(true); // never leave the worker blocked: the executor's disposal waits for it
        }

        await first.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        first.Dispose();
    }

    [Fact]
    public async Task PlanDropped_Fires_OnTheBotsPlan_WhenTheJobCapacityIsExhaustedUnderItsLease()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        PlayerbotNavigationExecutor executor = new(() => new GatedPathfinder(gate), capacity: 1);
        try
        {
            await WithBotAsync((player, session, capture) =>
            {
                Vector3 start = PlayerbotMotion.CurrentPosition(player);
                var request = new PlayerbotNavigationRequest(player.MapId, start, start + new Vector3(30, 0, 0), PathOptions.Default);
                Assert.True(executor.TrySubmit(request, out PlayerbotNavigationJob first));
                Assert.True(executor.TryAcquirePlan(out IDisposable lease));
                try
                {
                    var plan = new PlayerbotAsyncNavigationPlan(player.MapId, start, owner: player);
                    Assert.Empty(capture.Notices);

                    Assert.False(executor.TrySubmit(request, out _));

                    Assert.Equal(1, capture.Count(Key("plan-dropped")));
                    Assert.Contains("executor job capacity exhausted", capture.Notices.Single().Detail, StringComparison.Ordinal);
                    Assert.True(plan.ReportedOutcome);
                }
                finally
                {
                    lease.Dispose();
                    gate.SetResult(true);
                    first.Dispose();
                }
            });
        }
        finally
        {
            await executor.DisposeAsync();
        }
    }

    private sealed class ImmediatePathfinder : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => PathResult.StraightLine(start, end, PathType.Normal);
    }

    private sealed class GatedPathfinder(TaskCompletionSource<bool> gate) : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            gate.Task.GetAwaiter().GetResult();
            return PathResult.StraightLine(start, end, PathType.Normal);
        }
    }
}

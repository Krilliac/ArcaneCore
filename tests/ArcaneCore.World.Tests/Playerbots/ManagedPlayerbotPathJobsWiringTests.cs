using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The feature's wiring of path jobs next to the asynchronous-navigation switch: a managed bot's far plan queues one job, and the finished
/// answer is applied on a later think, with no search on the world thread and no dropped plan. This is coverage of a combination that
/// already works (green before the path-job fixes): without navigation meshes in a test world the navigation executor is not created
/// (it needs a <see cref="ArcaneCore.Game.Maps.Collision.MMaps.NavMeshPathfinder"/>), so it proves the two switches coexist, not the
/// precedence of the executor over jobs (<see cref="PlayerbotPathJobTests.InsideAnAsyncNavigationPlan_AFarQuerySubmitsNoPathJob"/>).
/// </summary>
public sealed class ManagedPlayerbotPathJobsWiringTests
{
    private sealed record Sample(int Submitted, int Applied);

    /// <summary>Plans to a point 250 yards away on every think.</summary>
    private sealed class FarPlanner(PlayerbotOptions options) : IPlayerbotController
    {
        private TileWait _wait;
        private Vector3? _destination;

        public AnomalyCapture? Capture { get; private set; }

        public void Tick(PlayerbotControllerContext context, uint elapsedMs)
        {
            if (context.Player is not { Map: not null } player) return;
            Capture ??= BotAnomalyCapture.For(player);
            _destination ??= new Vector3(player.X + 250f, player.Y, player.Z);
            PlayerbotNavigation.PlanExact(player, _destination.Value, options, ref _wait, context.World.NowMs, out _);
        }

        public void Detached(Guid botId) { }
    }

    [Fact]
    public async Task AFarPlanOfAManagedBot_QueuesOneJob_AndAppliesItOnALaterThink_WithTheAsyncNavigationSwitchOn()
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        PlayerbotOptions? shared = null;
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(accounts, characters, owners,
            configure: o =>
            {
                o.PathJobs = true;
                o.AsyncNavigationEnabled = true;
                o.ThinkIntervalMs = 100;
                o.MaxPathPoints = 128;
                o.MaxRouteYards = 2000;
                shared = o;
            });
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await feature.StartupAsync(default);
        var nav = new ManualSharedPathSearch();
        using var runner = new ManualPathJobRunner();
        var samples = new List<Sample>();
        Guid id = Assert.IsType<Guid>((await feature.CreateAsync("Pathjobwire", 1, 1)).BotId);
        await host.OnWorldAsync(() =>
        {
            WorldCollision.Of(host.World).Install(pathfinder: nav);
            feature.PathJobRunnerForTests = runner;
            feature.ThinkObserver = (bot, probe) =>
            {
                if (bot == id) samples.Add(new Sample(probe.JobsSubmitted, probe.JobsApplied));
            };
            return true;
        });

        var controller = new FarPlanner(shared!);
        Assert.True((await feature.StartScriptedAsync(id.ToString(), controller)).Success);
        await host.WaitForWorldAsync(() => samples.Exists(s => s.Submitted == 1), "the think that queued the job");

        await host.OnWorldAsync(() =>
        {
            Assert.Single(runner.Submitted);
            runner.CompleteAll();
            return true;
        });
        await host.WaitForWorldAsync(() => samples.Exists(s => s.Applied == 1), "the think that applied the job");

        await host.OnWorldAsync(() =>
        {
            Assert.Equal(0, nav.OwnerCalls);
            Assert.Equal(0, controller.Capture!.Count(PlayerbotAnomalies.Key("nav", "plan-dropped")));
            Assert.Equal(1, feature.PathJobsForTests!.Totals.Applied);
            controller.Capture.Dispose();
            feature.ThinkObserver = null;
            return true;
        });
        await feature.ShutdownBeforeWorldStopAsync();
    }
}

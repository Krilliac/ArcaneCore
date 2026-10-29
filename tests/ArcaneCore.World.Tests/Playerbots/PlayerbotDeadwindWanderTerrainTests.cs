using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// A server of level 60 bots with no home area stalled 412 times in one day on one spot in Deadwind Pass (map 0, zone 41), goal Explore,
/// <c>motion=moving:no,wp:0/0,dest:none</c>, traces full of <c>wander-refused</c> (a build before the mesh-point fallback). On the real mesh, without
/// creatures, the spot is not a pocket: most 48-yard rays plan, and the bot takes a leg on its first think. This pins that. It runs the exploring brain on the real terrain from that spot (it skips without it, like every
/// <see cref="RealTerrainBotFactAttribute"/> test).
/// </summary>
public sealed class PlayerbotDeadwindWanderTerrainTests : RealTerrainNavigationTestBase
{
    private static readonly Vector3 Start = new(-11153.8f, -1878.6f, 63.5f);

    private const int MaxThinks = 40;
    private const uint ThinkMs = 500;

    public PlayerbotDeadwindWanderTerrainTests(ITestOutputHelper output) : base(output) { }

    [RealTerrainBotFact]
    public async Task AHomelessLevel60BotInDeadwindPass_GetsAnAcceptedExploreRoute()
    {
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(Start, 0);
        var brain = new PlayerbotBrain(terrain.Session, new PlayerbotOptions { Enabled = true });
        try
        {
            float ground = await terrain.Host.OnWorldAsync(() =>
            {
                terrain.Player.Level = 60;
                return terrain.Player.Map!.Collision.GetHeight(Start.X, Start.Y, Start.Z);
            });
            Vector3 begin = await terrain.Host.OnWorldAsync(() => new Vector3(terrain.Player.X, terrain.Player.Y, terrain.Player.Z));
            output.WriteLine($"start {begin}, ground height at the start {ground:F2}, level 60, no home");

            var goals = new HashSet<PlayerbotGoalKind>();
            bool accepted = false;
            string how = "none";
            int thinksRun = 0;
            for (int think = 0; think < MaxThinks && !accepted; think++)
            {
                thinksRun++;
                (string line, bool routeTaken, float moved) = await terrain.Host.OnWorldAsync(() =>
                {
                    terrain.Session.ManagedBudget = new ManagedActionBudget(4);
                    brain.Update(ThinkMs);
                    PlayerbotRoute? route = PlayerbotMotion.Of(terrain.Player).Route;
                    bool taken = route is { Complete: false, Points.Count: > 0 };
                    PlayerbotMotion.Pump(terrain.Session, terrain.Player, terrain.Host.World.NowMs);
                    var now = new Vector3(terrain.Player.X, terrain.Player.Y, terrain.Player.Z);
                    goals.Add(brain.Goal);
                    string text = $"think {think}: goal={brain.Goal} phase={brain.LastThinkPhase} "
                        + $"route={(route is null ? "none" : $"complete={route.Complete},points={route.Points.Count}")} "
                        + $"pos={now} wanderPlans={brain.ExploreWanderPlans}";
                    return (text, taken, Vector3.Distance(now, begin));
                });
                output.WriteLine(line);
                if (routeTaken) (accepted, how) = (true, "route in hand");
                else if (moved >= PlayerbotStallWatch.ProgressYards) (accepted, how) = (true, $"moved {moved:F1} yards");
                await terrain.Host.World.AdvanceClockAsync(ThinkMs);
            }

            string diagnostics = await terrain.Host.OnWorldAsync(() =>
            {
                string snapshot = brain.FormatDiagnosticSnapshot(terrain.Player);
                return $"thinks={thinksRun}, wanderPlans={brain.ExploreWanderPlans}, goals=[{string.Join(',', goals)}], "
                    + $"start={begin}, ground height at the start={ground:F2}, position=({terrain.Player.X:F1},{terrain.Player.Y:F1},{terrain.Player.Z:F1}), "
                    + $"snapshot (last trace entries, a refusal shows as wander-refused, the mesh-point fallback as wander-mesh-point): {snapshot}";
            });
            output.WriteLine($"accepted: {how}; {diagnostics}");

            Assert.True(goals.Contains(PlayerbotGoalKind.Explore),
                $"the bot never took the Explore goal, so the wander was not exercised: {diagnostics}");
            Assert.True(accepted,
                $"no explore route accepted in {MaxThinks} thinks of {ThinkMs} ms (no incomplete route with a waypoint, and under {PlayerbotStallWatch.ProgressYards} yards moved): {diagnostics}");
        }
        finally
        {
            brain.Stop();
        }
    }
}

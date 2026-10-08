using System.Globalization;
using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Scenarios;
using Xunit;
using Xunit.Abstractions;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>
/// What another client sees of a server-managed bot's movement. A second bot stands beside the mover and records the
/// relayed MSG_MOVE_* packets; the tests decode them and check them the way a 1.12 client consumes them: it keeps a
/// remote player moving along the last packet's orientation at its known speed until the next packet arrives
/// (a real client sends START, a heartbeat about every 500 ms, and STOP). Every packet must therefore land where the
/// previous one predicted, at a cadence that does not depend on how often the bot "thinks".
/// </summary>
public sealed class PlayerbotMovementWireTests(ITestOutputHelper output)
{
    private const float RunSpeed = 7f; // Unit.BaseRunSpeed; PlayerbotOptions.MoveSpeed default
    private const float Tolerance = 0.75f; // yards between where a packet lands and where the previous one predicted
    private const uint MaxPacketGapMs = 600; // a real client heartbeats about every 500 ms
    private const float WireOffsetY = 20f; // where the walks run, south of the start (clear of the test map's triggers)

    [Fact]
    public async Task StraightRoute_EveryPacketLandsWhereThePreviousOnePredicted()
    {
        Vector3 offset = new(30, 0, 0);
        Run run = await RunAsync(500, (walker, context, think, _) => walker.Follow(context, offset));
        Expect(run, offset, expectStop: true);
    }

    [Fact]
    public async Task ThinksThatDoNotTouchMovement_DoNotStallHeartbeatsOrLoseDistance()
    {
        // The brain often returns early (quest interaction, equipment, casts, budget) without advancing its route.
        Vector3 offset = new(40, 0, 0);
        Run run = await RunAsync(500, (walker, context, think, _) =>
        {
            if (think % 3 != 2) walker.Follow(context, offset);
        });
        Expect(run, offset, expectStop: true);
    }

    [Fact]
    public async Task HeartbeatCadence_DoesNotFollowTheThinkInterval()
    {
        Vector3 offset = new(30, 0, 0);
        Run run = await RunAsync(2000, (walker, context, think, _) => walker.Follow(context, offset));
        Expect(run, offset, expectStop: true);
    }

    [Fact]
    public async Task CorneredRoute_OrientationAlwaysPointsAlongTheTravel()
    {
        Run run = await RunAsync(500, (walker, context, think, _) =>
        {
            if (walker.Done) return;
            walker.Route ??= Corner(walker.Origin);
            bool advanced = PlayerbotNavigation.TryAdvance(context.Session, walker.Route, walker.Options, 500, context.World.NowMs);
            walker.Results.Add(advanced || walker.Route.Complete);
            walker.Done = walker.Route.Complete;
        });
        Expect(run, new(10, 10, 0), expectStop: true);
    }

    [Fact]
    public async Task ExhaustedActionBudget_DoesNotDropAMovingRoute()
    {
        // Many bots share MaxActionsPerTick. Running out of it must not cost a moving bot its route or its heartbeat.
        Vector3 offset = new(30, 0, 0);
        Run run = await RunAsync(500, (walker, context, think, _) =>
        {
            context.Session.ManagedBudget = think == 0 ? new ManagedActionBudget(1) : new ManagedActionBudget(0);
            try { walker.Follow(context, offset); }
            finally { context.Session.ManagedBudget = null; }
        });
        Assert.True(run.Results.Count > 3, "the walker thought");
        Assert.All(run.Results.Take(run.Results.Count - 1), Assert.True);
        Expect(run, offset, expectStop: true);
    }

    [Fact]
    public async Task StopThenSit_StopsWhereObserversAlreadySawTheBot_AndNothingSlidesWhileSeated()
    {
        // The live report: a bot sat down to drink and slid across the floor. Stop is requested after thinks that did
        // not touch movement (observers kept extrapolating the run), then the bot sits.
        Vector3 offset = new(60, 0, 0);
        Run run = await RunAsync(500, (walker, context, think, _) =>
        {
            if (think < 4) walker.Follow(context, offset);
            else if (think == 6)
            {
                Assert.True(PlayerbotMovementControl.Stop(context.Session, context.Player!));
                Assert.True(context.TryAction(WorldOpcode.CmsgStandstatechange, BitConverter.GetBytes(1u)));
            }
        }, TimeSpan.FromSeconds(6));
        Expect(run, null, expectStop: true);
        Assert.Equal(StandState.Sit, run.FinalStandState);
        Assert.False(run.FinalMoving);
    }

    [Fact]
    public async Task OscillatingGoals_AreDetected_TheDestinationIsBlacklisted_AndTheBotStops()
    {
        // Two goals fighting over the bot (Bramblepaw going back and forth on a line): every second the wanted
        // destination flips between 12 yards east and 12 yards west of the start.
        Vector3 east = new(12, 0, 0), west = new(-12, 0, 0);
        Run run = await RunAsync(500, (walker, context, think, _) =>
        {
            Vector3 wanted = think / 2 % 2 == 0 ? east : west;
            if (walker.Wanted != wanted) walker.Route = null;
            walker.Wanted = wanted;
            walker.Follow(context, wanted);
        }, TimeSpan.FromSeconds(25), after: (walker, player) =>
        {
            Vector3 e = walker.Origin + east, w = walker.Origin + west;
            bool eastPlannable = PlayerbotNavigation.TryPlan(player, e, walker.Options, out _);
            bool westPlannable = PlayerbotNavigation.TryPlan(player, w, walker.Options, out _);
            return $"east={eastPlannable} west={westPlannable}";
        });
        output.WriteLine(run.After);
        Assert.Contains("False", run.After); // at least one of the fought-over destinations is refused for a while
        Assert.False(run.FinalMoving, "the bot gave up instead of oscillating");
        // It kept trying for a bounded time only: the walker saw a refusal well before the end of the run.
        int firstRefusal = run.Results.IndexOf(false);
        Assert.InRange(firstRefusal, 1, 30); // within 15 s of thinking
    }

    // --- harness -----------------------------------------------------------------------------------------------

    private static PlayerbotRoute Corner(Vector3 origin)
    {
        float z = origin.Z;
        Vector3[] points = [origin, origin with { X = origin.X + 10, Z = z }, new(origin.X + 10, origin.Y + 10, z)];
        return new PlayerbotRoute(points, 20);
    }

    private sealed record Run(List<(WorldOpcode Opcode, MovementInfo Info)> Moves, Vector3 Origin, List<bool> Results,
        StandState FinalStandState, bool FinalMoving, string After);

    private delegate void Think(Walker walker, PlayerbotControllerContext context, int think, uint intervalMs);

    /// <summary>A scripted mover: thinks every <see cref="ThinkMs"/> like the brain and lets a test decide what each think does.</summary>
    private sealed class Walker(uint thinkMs, Think think) : IPlayerbotController
    {
        private uint _accumulated;
        private int _think;
        private bool _started;

        public uint ThinkMs { get; } = thinkMs;
        public PlayerbotOptions Options { get; } = new() { Enabled = true, MaxPathPoints = 128, MaxRouteYards = 200, AllowedMaps = [0, 1] };
        public Vector3 Origin;
        public Vector3? Wanted;
        public PlayerbotRoute? Route;
        public bool Done;
        public List<bool> Results { get; } = [];

        public void Follow(PlayerbotControllerContext context, Vector3 offset)
        {
            if (Done) return;
            Player player = context.Player!;
            if (Route is null && !PlayerbotNavigation.TryPlan(player, Origin + offset, Options, out Route))
            {
                Results.Add(false);
                return;
            }

            bool advanced = PlayerbotNavigation.TryAdvance(context.Session, Route!, Options, ThinkMs, context.World.NowMs);
            Results.Add(advanced || Route!.Complete);
            if (Route!.Complete && Wanted is null) Done = true;
            if (!advanced) Route = null;
        }

        public void Tick(PlayerbotControllerContext context, uint elapsedMs)
        {
            if (context.Player is not { } player) return;
            if (context.AcknowledgeServerOrders()) return;
            if (!_started)
            {
                _started = true;
                Origin = new(player.X, player.Y, player.Z);
            }

            _accumulated += elapsedMs;
            if (_accumulated < ThinkMs) return;
            uint interval = _accumulated;
            _accumulated = 0;
            think(this, context, _think++, interval);
        }

        public void Detached(Guid botId)
        {
        }
    }

    private static async Task<Run> RunAsync(uint thinkMs, Think think, TimeSpan? duration = null,
        Func<Walker, Player, string>? after = null)
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync();
        Run? result = null;
        ScenarioReport report = await world.RunAsync(new DelegateScenario("movement-wire", async context =>
        {
            ScenarioBot mover = await context.LoginAsync(PlayerbotScenarioCatalog.BotA);
            ScenarioBot observer = await context.LoginAsync(PlayerbotScenarioCatalog.BotB);
            await context.ReadAsync(() =>
            {
                WorldCollision.Of(context.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                return true;
            });
            // 20 yards south of the human start: the host's map content has a teleport trigger 10 yards east of the start
            // (MapTestData "Test shortcut", a 3-yard sphere), and a bot walking into it reports it like a client
            // (PlayerbotAreaTriggers) and is teleported away mid-route.
            await context.PlaceAsync(observer, 0, StartX, StartY - WireOffsetY + 6f, StartZ);
            await context.PlaceAsync(mover, 0, StartX, StartY - WireOffsetY, StartZ);
            await context.WaitUntilAsync("observer sees mover", () => observer.RequirePlayerForTests().VisibleObjects.Contains(mover.Guid));

            var walker = new Walker(thinkMs, think);
            long mark = observer.Mark();
            Assert.True(await world.Bots.SetControllerAsync(mover.BotId, walker));
            await context.IdleAsync(duration ?? TimeSpan.FromSeconds(10));

            string afterText = after is null ? "" : await context.ReadAsync(() => after(walker, mover.RequirePlayerForTests()));
            (StandState stand, bool moving) = await context.ReadAsync(() =>
            {
                Player player = mover.RequirePlayerForTests();
                return (player.StandState, (player.Movement.Flags & MovementFlags.MaskMoving) != 0);
            });
            List<(WorldOpcode, MovementInfo)> moves = observer.Log.Snapshot()
                .Where(p => p.Sequence >= mark && p.Direction == ScenarioPacketDirection.Received && MovementOpcodes.IsRelayable(p.Opcode))
                .Select(p => (p.Opcode, View: ScenarioDecoders.Movement(p.Payload)))
                .Where(m => m.View.Mover == mover.Guid.Value)
                .Select(m => (m.Opcode, m.View.Info))
                .ToList();
            result = new Run(moves, walker.Origin, walker.Results, stand, moving, afterText);
        }));
        Assert.True(report.Passed, report.ToString());
        return result!;
    }

    /// <summary>
    /// Check the observed packets like a client consumes them. <paramref name="destinationOffset"/>: where the final STOP
    /// must be, relative to the origin (null: anywhere).
    /// </summary>
    private void Expect(Run run, Vector3? destinationOffset, bool expectStop)
    {
        foreach ((WorldOpcode opcode, MovementInfo info) in run.Moves)
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{opcode,-24} t={info.Time,8} flags={info.Flags,-30} x={info.X - run.Origin.X,8:F3} y={info.Y - run.Origin.Y,8:F3} o={info.Orientation:F3}"));
        var problems = new List<string>();
        Assert.NotEmpty(run.Moves);
        (WorldOpcode firstOpcode, MovementInfo first) = run.Moves[0];
        if (firstOpcode != WorldOpcode.MsgMoveStartForward) problems.Add($"first packet is {firstOpcode}, not MSG_MOVE_START_FORWARD");
        if (!first.HasFlag(MovementFlags.Forward)) problems.Add("START does not carry MOVEFLAG_FORWARD");
        if (first.Orientation is < 0 or >= MathF.Tau) problems.Add($"START orientation {first.Orientation} is outside [0, 2pi)");

        for (int i = 1; i < run.Moves.Count; i++)
        {
            (WorldOpcode prevOpcode, MovementInfo a) = run.Moves[i - 1];
            (WorldOpcode opcode, MovementInfo b) = run.Moves[i];
            string where = $"#{i} {prevOpcode}->{opcode} at t={b.Time}";
            if (b.Time < a.Time) problems.Add($"{where}: time went backwards ({a.Time} -> {b.Time})");
            if (opcode == WorldOpcode.MsgMoveHeartbeat && !b.HasFlag(MovementFlags.Forward))
                problems.Add($"{where}: a heartbeat without MOVEFLAG_FORWARD moves the unit without running (sliding)");
            if (b.Orientation is < 0 or >= MathF.Tau) problems.Add($"{where}: orientation {b.Orientation} is outside [0, 2pi)");
            if (!a.HasFlag(MovementFlags.Forward))
            {
                float moved = Vector2.Distance(new(a.X, a.Y), new(b.X, b.Y));
                if (moved > 0.05f) problems.Add($"{where}: moved {moved:F2} yd after a packet without MOVEFLAG_FORWARD (sliding)");
                continue;
            }

            uint gap = b.Time - a.Time;
            if (gap > MaxPacketGapMs) problems.Add($"{where}: {gap} ms since the previous packet of a running unit");
            float speed = a.HasFlag(MovementFlags.WalkMode) ? 2.5f : RunSpeed;
            Vector2 predicted = new Vector2(a.X, a.Y)
                + new Vector2(MathF.Cos(a.Orientation), MathF.Sin(a.Orientation)) * (speed * gap / 1000f);
            float error = Vector2.Distance(predicted, new Vector2(b.X, b.Y));
            if (error > Tolerance)
                problems.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{where}: landed {error:F2} yd from where the previous packet predicted (dt={gap} ms) - the observer snaps/rubber-bands"));
        }

        (WorldOpcode lastOpcode, MovementInfo last) = run.Moves[^1];
        if (expectStop)
        {
            if (lastOpcode != WorldOpcode.MsgMoveStop) problems.Add($"last packet is {lastOpcode}, not MSG_MOVE_STOP");
            if ((last.Flags & MovementFlags.MaskMoving) != 0) problems.Add("the final packet still says the unit is moving");
            if (destinationOffset is { } offset)
            {
                float miss = Vector2.Distance(new(last.X, last.Y), new(run.Origin.X + offset.X, run.Origin.Y + offset.Y));
                if (miss > 0.5f) problems.Add($"stopped {miss:F2} yd from the destination");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}

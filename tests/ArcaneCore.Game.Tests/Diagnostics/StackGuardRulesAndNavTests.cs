using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Rules;
using ArcaneCore.Game.Tests.Rules;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Rules;
using ArcaneCore.TestSupport;
using Xunit;
using static ArcaneCore.Game.Tests.Rules.RuleTestKit;

namespace ArcaneCore.Game.Tests.Diagnostics;

/// <summary>
/// Margin fixtures (the pattern of <see cref="StackGuardPathTests"/>) for the two guards that change an answer when they trip:
/// <c>rules.start-list</c> (<see cref="RuleEvaluator.StartList"/> returns false and counts <c>DepthLimited</c>) and <c>nav.find-path</c>
/// (<see cref="NavMeshPathfinder.FindPath(uint, Vector3, Vector3, PathOptions?)"/> answers <see cref="PathType.NoPath"/>). The stack of a small thread is used
/// up until only the runtime's safety margin is left, then the production entry point is called. RED: the guard suspended, the call runs on.
/// GREEN: the guard on, the call is dropped and one fault is recorded under the site.
/// </summary>
public sealed class StackGuardRulesAndNavTests
{
    private const int SmallStackBytes = 512 * 1024;
    private const int PadBytes = 2048;
    private const uint MapId = 0;

    [Fact]
    public void RuleStartList_AtTheStackMargin_IsCut_CountedAndRecorded()
    {
        RuleCatalog catalog = Build(new RuleContent
        {
            Rules = [Rule(1)],
            Actions = [Action(1, StartList, 7)],
            Lists = [new RuleListRecord { Id = 7 }],
            ListActions = [RecordOf(7, 70)],
        });
        WarmUpFaultRecording();

        // Warm run on a normal stack: the list runs.
        (RuleSubjectState warmState, TestSubject warmSubject) = (catalog.Bind(Kind).CreateState(), new TestSubject(1));
        Evaluator().Fire(warmState, warmSubject, Ping, default, 0);
        Assert.Equal([70], warmSubject.Tags);

        // RED: nothing stops the call; the started list runs on the last of the stack.
        (RuleSubjectState redState, TestSubject redSubject) = (catalog.Bind(Kind).CreateState(), new TestSubject(1));
        RuleEvaluator red = Evaluator();
        bool marginGone = false;
        OnSmallStack(() =>
        {
            using StackGuard.SuspendScope scope = StackGuard.Suspend();
            marginGone = UntilExhausted(() => red.Fire(redState, redSubject, Ping, default, 0));
        });
        Assert.True(marginGone, "the call must be made with the runtime's stack margin used up");
        Assert.Equal([70], redSubject.Tags);
        Assert.Equal(0, red.Counters.DepthLimited);

        // GREEN: the guard drops the start, counts it and records a fault under the site.
        (RuleSubjectState greenState, TestSubject greenSubject) = (catalog.Bind(Kind).CreateState(), new TestSubject(1));
        RuleEvaluator green = Evaluator();
        long before = FaultCount("rules.start-list");
        marginGone = false;
        OnSmallStack(() => marginGone = UntilExhausted(() => green.Fire(greenState, greenSubject, Ping, default, 0)));

        Assert.True(marginGone);
        Assert.Empty(greenSubject.Tags);
        Assert.Equal(1, green.Counters.DepthLimited);
        Assert.True(FaultCount("rules.start-list") > before, "a fault must be recorded under rules.start-list");
        Assert.Contains(FaultLedger.Shared.Snapshot(), s => s.Site == "rules.start-list"
            && s.ExceptionType.EndsWith(nameof(InsufficientExecutionStackException), StringComparison.Ordinal));
    }

    [Fact]
    public void NavFindPath_AtTheStackMargin_AnswersNoPath_AndRecords()
    {
        using var fixture = new NavMeshFixture();
        fixture.WriteParams(MapId);
        fixture.WriteTile(MapId, 31, 31, new CellTile(0, 0, 0, 0, 6, 6, Size: 4) { LinkedSides = [2], Walkable = (i, j) => !(i == 3 && j < 4) });
        fixture.WriteTile(MapId, 30, 31, new CellTile(0, 1, 24, 0, 6, 6, Size: 4) { LinkedSides = [6], Walkable = (i, j) => !(i == 2 && j > 1) });
        var nav = new NavMeshPathfinder(fixture.Directory);
        Assert.True(nav.LoadTile(MapId, 31, 31));
        Assert.True(nav.LoadTile(MapId, 30, 31));
        Vector3 start = new(4f, 8f, 0f);
        Vector3 end = new(44f, 16f, 0f);
        WarmUpFaultRecording();

        // Warm run on a normal stack: a path is found.
        Assert.Equal(PathType.Normal, nav.FindPath(MapId, start, end).Type);

        // RED: nothing stops the query; it runs on the last of the stack and still finds the path.
        PathResult? red = null;
        bool marginGone = false;
        OnSmallStack(() =>
        {
            using StackGuard.SuspendScope scope = StackGuard.Suspend();
            marginGone = UntilExhausted(() => red = nav.FindPath(MapId, start, end));
        });
        Assert.True(marginGone, "the call must be made with the runtime's stack margin used up");
        Assert.Equal(PathType.Normal, red?.Type);

        // GREEN: the guard drops the query, answers no path and records a fault under the site.
        PathResult? green = null;
        long before = FaultCount("nav.find-path");
        marginGone = false;
        OnSmallStack(() => marginGone = UntilExhausted(() => green = nav.FindPath(MapId, start, end)));

        Assert.True(marginGone);
        Assert.Equal(PathType.NoPath, green?.Type);
        Assert.True(FaultCount("nav.find-path") > before, "a fault must be recorded under nav.find-path");
        Assert.Contains(FaultLedger.Shared.Snapshot(), s => s.Site == "nav.find-path"
            && s.ExceptionType.EndsWith(nameof(InsufficientExecutionStackException), StringComparison.Ordinal));
    }

    private static long FaultCount(string site) => FaultLedger.Shared.Snapshot().Where(s => s.Site == site).Sum(s => s.Count);

    private static void WarmUpFaultRecording()
    {
        try
        {
            throw new InsufficientExecutionStackException("warm-up");
        }
        catch (InsufficientExecutionStackException ex)
        {
            new FaultLedger().Record("test.stack-guard-warmup", ex); // a private ledger: the shared one keeps only real sites
        }
    }

    /// <summary>Use up the stack of the calling thread, 2 KB a frame, until only the runtime's margin is left, then call <paramref name="action"/> there.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool UntilExhausted(System.Action action)
    {
        if (RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            Span<byte> pad = stackalloc byte[PadBytes];
            pad[0] = 1;
            Keep(pad);
            bool reached = UntilExhausted(action);
            Keep(pad); // not a tail call: the frame stays
            return reached;
        }

        action();
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Keep(Span<byte> pad) => pad[0]++;

    private static void OnSmallStack(System.Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failure = ex;
            }
        }, SmallStackBytes)
        {
            IsBackground = true,
        };
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60)))
        {
            throw new TimeoutException("the call did not end within 60 s");
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}

using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Combat;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Combat;

/// <summary>
/// <see cref="PlayerbotDefenseBoard"/> on a manual clock, with no world: guids and positions only. Tests that name a RED proof run the same scene with the
/// guarded rule switched off (a zero cooldown, a long maximum age) and assert the opposite outcome, so the guard is shown to be what holds.
/// </summary>
public sealed class PlayerbotDefenseBoardTests
{
    private const uint Map = 0;
    private const uint Zone = 12;
    private const uint Area = 87;

    private long _now = 1_000_000;

    // Merging by distance is off unless a test turns it on, so the cap and cooldown tests keep their scenes (every report there sits at one spot).
    private PlayerbotPvpDefenseOptions Options { get; } = new() { MergeRadiusYards = 0 };

    /// <summary>A board that reads grace and maximum age from <see cref="Options"/>, as the live feature does.</summary>
    private PlayerbotDefenseBoard NewLiveBoard() => new(() => _now, () => Options);

    private PlayerbotDefenseBoard NewBoard(long graceMs = 0, long maxOpenMs = PlayerbotDefenseBoard.DefaultMaxOpenMs)
        => new(() => _now, () => Options, graceMs, maxOpenMs);

    private static DefenseReport Report(PlayerbotDefenseBoard board, ulong caller, ulong attacker, Vector3 at, uint zone = Zone, uint instance = 0)
        => board.Report(caller, attacker, Team.Alliance, Map, instance, zone, Area, at, level: 20);

    private static List<DefenseCall> Open(PlayerbotDefenseBoard board)
    {
        var list = new List<DefenseCall>();
        board.OpenCalls(list);
        return list;
    }

    [Fact]
    public void Report_OpensACall_ThenRefreshes_NotReopens()
    {
        var board = NewBoard();
        var first = Report(board, 1, 100, new Vector3(10, 10, 0));
        _now += 2_000;
        var second = Report(board, 1, 100, new Vector3(12, 10, 0));

        Assert.Equal(DefenseReportOutcome.Opened, first.Outcome);
        Assert.Equal(DefenseReportOutcome.Refreshed, second.Outcome);
        Assert.Equal(first.CallId, second.CallId);
        Assert.Single(Open(board));
    }

    [Fact]
    public void CallerCooldown_BlocksASecondNewCall_AndRemovingItLetsOneThrough()
    {
        var board = NewBoard();
        Assert.Equal(DefenseReportOutcome.Opened, Report(board, 1, 100, Vector3.Zero).Outcome);

        _now += DefenseCall.LiveWindowMs + 1_000; // the first call has closed; the caller is still cooling down
        Assert.Empty(Open(board));
        Assert.Equal(DefenseReportOutcome.CallerCooldown, Report(board, 1, 101, Vector3.Zero).Outcome);

        // RED proof: with no cooldown the same report opens a second call.
        var noCooldown = NewBoard();
        Options.CallerCooldownSeconds = 0;
        Options.ProvokeThrottleSeconds = 0;
        Assert.Equal(DefenseReportOutcome.Opened, Report(noCooldown, 1, 100, Vector3.Zero).Outcome);
        _now += DefenseCall.LiveWindowMs + 1_000;
        Assert.Equal(DefenseReportOutcome.Opened, Report(noCooldown, 1, 101, Vector3.Zero).Outcome);
    }

    [Fact]
    public void CallerCooldown_Ends_AtTheBoundary()
    {
        var board = NewBoard();
        Report(board, 1, 100, Vector3.Zero);
        long opened = _now;

        _now = opened + Options.CallerCooldownSeconds * 1000L - 1;
        Assert.Equal(DefenseReportOutcome.CallerCooldown, Report(board, 1, 101, Vector3.Zero).Outcome);
        _now = opened + Options.CallerCooldownSeconds * 1000L;
        Assert.Equal(DefenseReportOutcome.Opened, Report(board, 1, 101, Vector3.Zero).Outcome);
    }

    [Fact]
    public void ProvokeThrottle_LimitsOneAttacker_ToOneNewCallPerWindow()
    {
        var board = NewBoard();
        Assert.Equal(DefenseReportOutcome.Opened, Report(board, 1, 100, Vector3.Zero).Outcome);
        long opened = _now;

        _now = opened + DefenseCall.LiveWindowMs + 1_000; // the call has closed; the attacker is still throttled
        Assert.Equal(DefenseReportOutcome.AttackerThrottled, Report(board, 2, 100, Vector3.Zero).Outcome);
        _now = opened + Options.ProvokeThrottleSeconds * 1000L;
        Assert.Equal(DefenseReportOutcome.Opened, Report(board, 2, 100, Vector3.Zero).Outcome);
    }

    [Fact]
    public void ASecondCallerNamingTheSameAttacker_JoinsTheCall_WithoutCooldownOrThrottle()
    {
        var board = NewBoard();
        var first = Report(board, 1, 100, Vector3.Zero);
        var second = Report(board, 2, 100, Vector3.Zero);

        Assert.Equal(DefenseReportOutcome.Joined, second.Outcome);
        Assert.Equal(first.CallId, second.CallId);
        Assert.Single(Open(board));
        Assert.Equal(2, board.Find(first.CallId)!.Callers.Count);
    }

    [Fact]
    public void Call_ClosesAfterLiveWindow_WhenGraceIsZero()
    {
        var board = NewBoard();
        Report(board, 1, 100, Vector3.Zero);

        _now += DefenseCall.LiveWindowMs - 1;
        Assert.Single(Open(board));
        _now += 1;
        Assert.Empty(Open(board));
    }

    [Fact]
    public void Call_StaysOpenThroughGrace_ThenCloses()
    {
        var board = NewBoard(graceMs: 120_000);
        var id = Report(board, 1, 100, Vector3.Zero).CallId;
        long reported = _now;

        _now = reported + 119_999;
        Assert.True(board.Find(id)!.IsOpen(_now));
        _now = reported + 120_000;
        Assert.False(board.Find(id)!.IsOpen(_now));
    }

    [Fact]
    public void MaxOpen_ClosesALiveCall_AndALongMaxOpenKeepsItOpen()
    {
        var board = NewBoard(maxOpenMs: 60_000);
        var id = Report(board, 1, 100, Vector3.Zero).CallId;

        for (int i = 0; i < 11; i++)  // a report every 5 s keeps the call live (the live window is 10 s, the grace here is 0)
        {
            _now += 5_000;
            Report(board, 1, 100, Vector3.Zero);
        }
        _now += 4_000;
        Assert.True(board.Find(id)!.IsLive(_now));
        Assert.True(board.Find(id)!.IsOpen(_now));
        _now += 1_000; // 60 s after opening, last report 5 s ago: live, but too old
        Assert.True(board.Find(id)!.IsLive(_now));
        Assert.False(board.Find(id)!.IsOpen(_now));

        // RED proof: the same timeline with the default maximum age leaves the call open.
        _now = 1_000_000;
        var long_ = NewBoard();
        var longId = Report(long_, 1, 100, Vector3.Zero).CallId;
        for (int i = 0; i < 11; i++)
        {
            _now += 5_000;
            Report(long_, 1, 100, Vector3.Zero);
        }
        _now += 5_000;
        Assert.True(long_.Find(longId)!.IsOpen(_now));
    }

    [Fact]
    public void ZoneCap_AndMapCap_RefuseFurtherCalls()
    {
        Options.MaxActiveCallsPerZone = 2;
        Options.MaxOpenCallsPerMap = 3;
        var board = NewBoard();

        Assert.Equal(DefenseReportOutcome.Opened, Report(board, 1, 101, Vector3.Zero).Outcome);
        Assert.Equal(DefenseReportOutcome.Opened, Report(board, 2, 102, Vector3.Zero).Outcome);
        Assert.Equal(DefenseReportOutcome.ZoneFull, Report(board, 3, 103, Vector3.Zero).Outcome);
        Assert.Equal(DefenseReportOutcome.Opened, Report(board, 3, 103, Vector3.Zero, zone: Zone + 1).Outcome);
        Assert.Equal(DefenseReportOutcome.MapFull, Report(board, 4, 104, Vector3.Zero, zone: Zone + 2).Outcome);
        // Another instance of the map has its own count.
        Assert.Equal(DefenseReportOutcome.Opened, Report(board, 4, 104, Vector3.Zero, zone: Zone + 2, instance: 1).Outcome);
    }

    [Fact]
    public void Centre_IsCentroidOfLiveCallers_AndHoldsWhenNoneIsLive()
    {
        var board = NewBoard(graceMs: 60_000);
        var id = Report(board, 1, 101, new Vector3(0, 0, 0)).CallId;
        Report(board, 2, 101, new Vector3(40, 20, 0));
        var call = board.Find(id)!;
        Assert.Equal(new Vector3(20, 10, 0), call.Centre);

        _now += DefenseCall.LiveWindowMs + 1;
        Assert.Equal(0, call.LiveCallerCount(_now));
        Assert.Equal(new Vector3(20, 10, 0), call.Centre);
        Assert.False(float.IsNaN(call.Centre.X));
    }

    [Fact]
    public void Centre_IsClampedAroundTheOrigin()
    {
        var board = NewBoard();
        var id = Report(board, 1, 101, Vector3.Zero).CallId;
        // Caller 1 stops reporting (still live); caller 2 reports far away. Centroid is 250 yd out: clamped to 150.
        Report(board, 2, 101, new Vector3(500, 0, 0));
        var call = board.Find(id)!;
        Assert.True(Vector3.Distance(call.Origin, call.Centre) <= PlayerbotDefenseBoard.MaxCentreDriftYards + 0.01f);
        Assert.Equal(150f, call.Centre.X, 0.01f);
    }

    [Fact]
    public void Report_RefusesANonFinitePosition()
    {
        var board = NewBoard();
        Assert.Equal(DefenseReportOutcome.Invalid, Report(board, 1, 100, new Vector3(float.NaN, 0, 0)).Outcome);
        Assert.Empty(Open(board));
    }

    [Fact]
    public void Callers_AndAttackers_AreCapped()
    {
        var board = NewBoard();
        var id = Report(board, 1, 1000, Vector3.Zero).CallId;
        for (ulong caller = 2; caller <= 20; caller++)
            Report(board, caller, 1000, Vector3.Zero);
        for (ulong attacker = 2001; attacker <= 2040; attacker++)
            Report(board, 1, attacker, Vector3.Zero);

        var call = board.Find(id)!;
        Assert.Equal(PlayerbotDefenseBoard.MaxCallersPerCall, call.Callers.Count);
        Assert.Equal(PlayerbotDefenseBoard.MaxAttackersPerCall, call.Attackers.Count);
    }

    [Fact]
    public void Assign_RespectsTheSideCap_AndRelease_FreesASlot()
    {
        Options.MaxRespondersPerSide = 2;
        var board = NewBoard();
        var id = Report(board, 1, 100, Vector3.Zero).CallId;

        Assert.True(board.Assign(id, 10, DefenseSide.Defender));
        Assert.False(board.Assign(id, 10, DefenseSide.Defender));
        Assert.True(board.Assign(id, 11, DefenseSide.Defender));
        Assert.False(board.Assign(id, 12, DefenseSide.Defender));

        board.Release(id, 10);
        Assert.True(board.Assign(id, 12, DefenseSide.Defender));
    }

    [Fact]
    public void JoinerCap_NeverExceedsDefendersPlusCallersPlusOne()
    {
        Options.MaxRespondersPerSide = 10;
        var board = NewBoard();
        var id = Report(board, 1, 100, Vector3.Zero).CallId; // one live caller, no defenders: cap 2

        Assert.True(board.Assign(id, 20, DefenseSide.Joiner));
        Assert.True(board.Assign(id, 21, DefenseSide.Joiner));
        Assert.False(board.Assign(id, 22, DefenseSide.Joiner));

        Assert.True(board.Assign(id, 10, DefenseSide.Defender)); // cap rises to 3
        Assert.True(board.Assign(id, 22, DefenseSide.Joiner));
        Assert.False(board.Assign(id, 23, DefenseSide.Joiner));
    }

    [Fact]
    public void Assign_RefusesAClosedOrUnknownCall()
    {
        var board = NewBoard();
        var id = Report(board, 1, 100, Vector3.Zero).CallId;
        Assert.False(board.Assign(id + 99, 10, DefenseSide.Defender));

        _now += DefenseCall.LiveWindowMs;
        Assert.False(board.Assign(id, 10, DefenseSide.Defender));
    }

    [Fact]
    public void Absorb_FoldsIntoTheLowerId_AndFindFollowsTheRedirect()
    {
        var board = NewBoard();
        int low = Report(board, 1, 101, new Vector3(0, 0, 0)).CallId;
        int high = Report(board, 2, 102, new Vector3(30, 0, 0), zone: Zone + 1).CallId;
        board.Assign(high, 50, DefenseSide.Defender);

        Assert.True(board.Absorb(high, low));

        var survivor = board.Find(high);
        Assert.NotNull(survivor);
        Assert.Equal(low, survivor!.Id);
        Assert.Contains(2UL, survivor.Callers.Keys);
        Assert.Contains(102UL, survivor.Attackers);
        Assert.Contains(50UL, survivor.Defenders);
        Assert.Single(Open(board));
        Assert.False(board.Absorb(low, low));
    }

    [Fact]
    public void Sweep_DropsClosedCalls_Redirects_AndExpiredCooldowns()
    {
        var board = NewBoard();
        int low = Report(board, 1, 101, Vector3.Zero).CallId;
        int high = Report(board, 2, 102, Vector3.Zero, zone: Zone + 1).CallId;
        board.Absorb(high, low);
        Assert.Equal((1, 2, 2), board.BookkeepingCounts);

        _now += DefenseCall.LiveWindowMs + 1;
        board.Sweep();
        Assert.Null(board.Find(low));
        Assert.Null(board.Find(high));
        // The 60 s cooldown and 30 s throttle have not run out yet; the redirect went with its survivor.
        Assert.Equal((0, 2, 2), board.BookkeepingCounts);

        _now += 61_000;
        board.Sweep();
        Assert.Equal((0, 0, 0), board.BookkeepingCounts);
    }

    [Fact]
    public void OpenCalls_AppendsInIdOrder_AndKeepsWhatWasInTheList()
    {
        var board = NewBoard();
        int a = Report(board, 1, 101, Vector3.Zero, zone: 1, instance: 5).CallId;
        int b = Report(board, 2, 102, Vector3.Zero, zone: 2, instance: 3).CallId;
        var list = new List<DefenseCall>();

        board.OpenCalls(list);

        Assert.Equal([a, b], list.Select(c => c.Id).ToArray());
    }

    [Fact]
    public void Grace_FromOptions_Boundary_Is119999Open_120000Closed()
    {
        Options.GraceSeconds = 120;
        var board = NewLiveBoard();
        int id = Report(board, 1, 100, Vector3.Zero).CallId;
        long reported = _now;

        _now = reported + DefenseCall.LiveWindowMs; // no longer live: only the grace holds it open
        Assert.False(board.Find(id)!.IsLive(_now));
        Assert.True(board.Find(id)!.IsOpen(_now));
        _now = reported + 119_999;
        Assert.True(board.Find(id)!.IsOpen(_now));
        Assert.Single(Open(board));
        _now = reported + 120_000;
        Assert.False(board.Find(id)!.IsOpen(_now));
        Assert.Empty(Open(board));

        // RED proof: the same timeline with no grace closes the call at the live window, so the 10 s assertion above is what the grace decides.
        _now = 1_000_000;
        Options.GraceSeconds = 0;
        var none = NewLiveBoard();
        int noneId = Report(none, 2, 200, Vector3.Zero).CallId;
        _now += DefenseCall.LiveWindowMs;
        Assert.False(none.Find(noneId)!.IsOpen(_now));
    }

    [Fact]
    public void MaxOpenSeconds_FromOptions_ClosesALiveCall_AndAChangeReachesACallAlreadyOpen()
    {
        Options.MaxOpenSeconds = 60;
        var board = NewLiveBoard();
        int id = Report(board, 1, 100, Vector3.Zero).CallId;
        _now += 59_000;
        Report(board, 1, 100, Vector3.Zero);
        _now += 1_000;
        Assert.True(board.Find(id)!.IsLive(_now));
        Assert.False(board.Find(id)!.IsOpen(_now));

        Options.MaxOpenSeconds = 600;
        Assert.True(board.Find(id)!.IsOpen(_now));
    }

    [Fact]
    public void Report_WithinMergeRadius_JoinsTheCall_OutsideOpensANewOne()
    {
        Options.MergeRadiusYards = 80;
        var board = NewBoard();
        int first = Report(board, 1, 101, Vector3.Zero).CallId;

        var near = Report(board, 2, 102, new Vector3(79.9f, 0, 0));
        Assert.Equal(DefenseReportOutcome.Joined, near.Outcome);
        Assert.Equal(first, near.CallId);

        var far = Report(board, 3, 103, new Vector3(0, 200.1f, 0));
        Assert.Equal(DefenseReportOutcome.Opened, far.Outcome);
        Assert.NotEqual(first, far.CallId);
    }

    [Fact]
    public void MergeBoundary_IsExactlyTheRadius()
    {
        Options.MergeRadiusYards = 80;
        var inside = NewBoard();
        int a = Report(inside, 1, 101, Vector3.Zero).CallId;
        Assert.Equal(a, Report(inside, 2, 102, new Vector3(80f, 0, 0)).CallId);

        var outside = NewBoard();
        int b = Report(outside, 1, 101, Vector3.Zero).CallId;
        Assert.NotEqual(b, Report(outside, 2, 102, new Vector3(80.1f, 0, 0)).CallId);
    }

    [Fact]
    public void MergeRadiusZero_NeverMerges_AndRadiusEighty_Does()
    {
        // RED proof pair: the same two reports, 10 yd apart.
        Options.MergeRadiusYards = 0;
        var off = NewBoard();
        int one = Report(off, 1, 101, Vector3.Zero).CallId;
        Assert.NotEqual(one, Report(off, 2, 102, new Vector3(10, 0, 0)).CallId);
        Assert.Equal(2, Open(off).Count);

        Options.MergeRadiusYards = 80;
        var on = NewBoard();
        int merged = Report(on, 1, 101, Vector3.Zero).CallId;
        Assert.Equal(merged, Report(on, 2, 102, new Vector3(10, 0, 0)).CallId);
        Assert.Single(Open(on));
    }

    [Fact]
    public void Report_NeverMergesIntoACallOfTheOtherTeam()
    {
        Options.MergeRadiusYards = 80;
        var board = NewBoard();
        int ally = Report(board, 1, 101, Vector3.Zero).CallId;
        var horde = board.Report(2, 102, Team.Horde, Map, 0, Zone, Area, new Vector3(5, 0, 0), level: 20);
        Assert.Equal(DefenseReportOutcome.Opened, horde.Outcome);
        Assert.NotEqual(ally, horde.CallId);
    }

    [Fact]
    public void TwoCallsConverging_MergeOnSweep_LowerIdSurvives_RedirectResolvesMultiHop()
    {
        var board = NewBoard();
        int low = Report(board, 1, 101, new Vector3(0, 0, 0)).CallId;
        int mid = Report(board, 2, 102, new Vector3(200, 0, 0), zone: Zone + 1).CallId;
        int high = Report(board, 3, 103, new Vector3(300, 0, 0), zone: Zone + 2).CallId;
        Assert.True(board.Assign(high, 70, DefenseSide.Defender));

        // Merging off: the sweep leaves three calls even when two are 100 yd apart, and with it on they are still out of range.
        board.Sweep();
        Assert.Equal(3, Open(board).Count);
        Options.MergeRadiusYards = 80;
        _now += 2_000;
        board.Sweep();
        Assert.Equal(3, Open(board).Count);

        // The fight converges: high's caller moves to 60 yd from mid's. The higher id folds into the lower one.
        Report(board, 3, 103, new Vector3(260, 0, 0));
        board.Sweep();
        Assert.Equal(2, Open(board).Count);
        Assert.Equal(mid, board.Find(high)!.Id);

        // Then low's caller walks to within 75 yd of the merged centre: mid folds into low, and high resolves through two hops.
        Report(board, 1, 101, new Vector3(145, 0, 0));
        Report(board, 2, 102, new Vector3(180, 0, 0));
        board.Sweep();
        var survivor = Assert.Single(Open(board));
        Assert.Equal(low, survivor.Id);
        Assert.Equal(low, board.Find(high)!.Id);
        Assert.Equal(low, board.Find(mid)!.Id);
        Assert.Contains(70UL, survivor.Defenders); // no responder was evicted
        Assert.Equal([1UL, 2UL, 3UL], survivor.Callers.Keys.Order().ToArray());
    }

    [Fact]
    public void Centre_HoldsDuringGrace_AndNoFreshResponderGoesToACallStaleFor60Seconds()
    {
        Options.GraceSeconds = 120;
        Options.MaxRespondersPerSide = 4;
        var board = NewLiveBoard();
        int id = Report(board, 1, 100, new Vector3(30, 40, 0)).CallId;
        var centre = board.Find(id)!.Centre;
        long reported = _now;

        _now = reported + 59_999;
        Assert.True(board.Assign(id, 10, DefenseSide.Defender)); // in grace, with a report under 60 s old
        Assert.Equal(centre, board.Find(id)!.Centre);

        _now = reported + 60_000;
        Assert.True(board.Find(id)!.IsOpen(_now));
        Assert.False(board.Assign(id, 11, DefenseSide.Defender)); // no live report for 60 s: refused
        Assert.Contains(10UL, board.Find(id)!.Defenders);         // the responder already on the way is untouched
        Assert.Equal(centre, board.Find(id)!.Centre);

        // RED proof: a report inside the minute keeps the call taking responders for another 60 s.
        _now = reported + 59_000;
        Report(board, 1, 100, new Vector3(30, 40, 0));
        _now += 59_999;
        Assert.True(board.Assign(id, 12, DefenseSide.Defender));
    }
}

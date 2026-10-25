using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Insight;
using ArcaneCore.Atlas.Live;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Insight;

/// <summary>The record ring: bounded by bytes and age, exact at recorded seqs, interpolated between them, and honest about what it keeps.</summary>
public sealed class InsightRecorderTests
{
    private static RecordedEntity E(ulong guid, float x, float y, byte hp = 100, uint flags = 0, uint d = 0, float z = 0f)
        => new(guid, (byte)LiveKind.Creature, x, y, z, hp, flags, d);

    [Fact]
    public void FiveThousandEntitiesAtFourHertzForFiveMinutes_StayUnderTheByteBudget_AndTheOldestFramesGoFirst()
    {
        const long Budget = 32L * 1024 * 1024;
        var recorder = new InsightRecorder(Budget, TimeSpan.FromMinutes(15));
        var entities = new RecordedEntity[5000];
        const int Frames = 5 * 60 * 4;
        for (int f = 0; f < Frames; f++)
        {
            for (int i = 0; i < entities.Length; i++)
            {
                entities[i] = E((ulong)(i + 1), i + (f * 0.25f), i * 0.5f, (byte)(f % 101), (uint)(i & 1), (uint)i);
            }

            recorder.Add(f + 1, f * 250L, 0, 0, entities);
            Assert.True(recorder.Bytes <= Budget, $"frame {f + 1}: {recorder.Bytes} bytes exceed the {Budget} byte budget");
        }

        Assert.True(recorder.EvictedFrames > 0, "five minutes of 5,000 entities did not reach a 32 MB budget; the test proves nothing");
        Assert.Equal(Frames, recorder.NewestSeq);
        Assert.True(recorder.OldestSeq > 1);
        Assert.Null(recorder.Exact(1));
        Assert.NotNull(recorder.Exact(Frames));
        Assert.Equal(Frames - recorder.FrameCount + 1, recorder.OldestSeq);
        Assert.Equal(Frames - recorder.FrameCount, recorder.EvictedFrames);
        Assert.True(recorder.Bytes > Budget / 2, "eviction threw away far more than the budget required");
    }

    [Fact]
    public void FramesOlderThanMaxAge_AreEvicted_ByTheFramesOwnClock()
    {
        var recorder = new InsightRecorder(long.MaxValue, TimeSpan.FromMinutes(1));
        for (int f = 0; f < 1200; f++)
        {
            recorder.Add(f + 1, f * 250L, 0, 0, [E(1, f, 0)]);
        }

        // Frame n (seq n + 1) is at n * 250 ms; the newest is at 299,750 ms, so everything from 239,750 ms on stays: seqs 960..1200.
        Assert.Equal(241, recorder.FrameCount);
        Assert.Equal(960, recorder.OldestSeq);
        Assert.Equal(1200, recorder.NewestSeq);
    }

    [Fact]
    public void ReplayAtARecordedSeq_ReturnsEveryEntityExactly_AndZeroWhereTheFrameHadNoD()
    {
        var recorder = new InsightRecorder(long.MaxValue, TimeSpan.FromMinutes(15));
        RecordedEntity a = E(0xABCDEF0123456789UL, 1.5f, -2.25f, 80, InsightRecorder.FlagCombat, 4242, 33.5f);
        RecordedEntity b = E(7, -17000.5f, 17000.25f, 0, 0, 0, -100000f);
        recorder.Add(10, 2500, 1, 3, [a, b]);
        recorder.Add(11, 2750, 1, 3, [a]);

        ReplayFrame frame = Assert.IsType<ReplayFrame>(recorder.Exact(10));

        Assert.Equal(10, frame.Seq);
        Assert.Equal(2500, frame.ClockMs);
        Assert.Equal(1u, frame.Map);
        Assert.Equal(3u, frame.Instance);
        Assert.Equal([a, b], frame.Entities);
        Assert.Equal(4242u, frame.Entities[0].D);
        Assert.Equal(0u, frame.Entities[1].D);
        Assert.Null(recorder.Exact(12));
        Assert.Null(recorder.Exact(9));
    }

    [Fact]
    public void ReplayBetweenFrames_InterpolatesPositions_ButTakesHpFlagsAndDFromTheEarlierFrame()
    {
        var recorder = new InsightRecorder(long.MaxValue, TimeSpan.FromMinutes(15));
        recorder.Add(1, 1000, 0, 0, [E(1, 0, 0, 100, 0, 9), E(2, 10, 10, 50), E(3, 0, 0)]);
        recorder.Add(2, 2000, 0, 0, [E(1, 100, 40, 60, InsightRecorder.FlagCombat, 8), E(2, 10, 10, 50), E(4, 5, 5)]);

        ReplayFrame mid = Assert.IsType<ReplayFrame>(recorder.AtClock(1500));

        Assert.Equal(1500, mid.ClockMs);
        RecordedEntity one = Assert.Single(mid.Entities, e => e.Guid == 1);
        Assert.Equal(50f, one.X);
        Assert.Equal(20f, one.Y);
        Assert.Equal((byte)100, one.Hp);
        Assert.Equal(9u, one.D);
        Assert.Equal(0u, one.Flags);
        Assert.Contains(mid.Entities, e => e.Guid == 3);
        Assert.DoesNotContain(mid.Entities, e => e.Guid == 4);

        Assert.Equal(100f, Assert.Single(recorder.AtClock(2000)!.Entities, e => e.Guid == 1).X);
        Assert.Equal(0f, Assert.Single(recorder.AtClock(1000)!.Entities, e => e.Guid == 1).X);
        Assert.Null(recorder.AtClock(999));
        Assert.Null(recorder.AtClock(2001));
    }

    [Fact]
    public void Diff_ReportsAppearedVanishedDiedAndMoved_WithAStrictlyGreaterThanThreshold()
    {
        var recorder = new InsightRecorder(long.MaxValue, TimeSpan.FromMinutes(15));
        recorder.Add(1, 0, 0, 0, [E(1, 0, 0), E(2, 0, 0, 50), E(3, 0, 0), E(5, 0, 0, 0), E(6, 0, 0)]);
        recorder.Add(2, 250, 0, 0, [E(1, 3, 4), E(2, 0, 0, 0), E(4, 9, 9), E(5, 0, 0, 0), E(6, 100, 0)]);

        ReplayDiff atFive = recorder.Diff(1, 2, 5.0);
        ReplayDiff atFourNine = recorder.Diff(1, 2, 4.9);

        Assert.Equal([4UL], atFive.Appeared);
        Assert.Equal([3UL], atFive.Vanished);
        Assert.Equal([2UL], atFive.Died);
        Assert.Equal([6UL], atFive.Moved);
        Assert.Equal([1UL, 6UL], atFourNine.Moved);
        Assert.Throws<ArgumentException>(() => recorder.Diff(1, 99, 5));
        Assert.Throws<ArgumentException>(() => recorder.Diff(99, 1, 5));
    }

    [Fact]
    public void Deaths_AreTheEntitiesWhoseHpFellToZeroInsideTheWindow_AtTheirDeathPosition()
    {
        var recorder = new InsightRecorder(long.MaxValue, TimeSpan.FromMinutes(15));
        recorder.Add(1, 0, 0, 0, [E(1, 0, 0, 100), E(2, 0, 0, 100)]);
        recorder.Add(2, 10_000, 0, 0, [E(1, 5, 6, 0), E(2, 0, 0, 100)]);
        recorder.Add(3, 20_000, 0, 0, [E(1, 5, 6, 0), E(2, 7, 8, 0)]);
        recorder.Add(4, 21_000, 7, 0, [E(3, 9, 9, 0)]);

        IReadOnlyList<RecordedEntity> all = recorder.Deaths(60_000, 0);
        IReadOnlyList<RecordedEntity> recent = recorder.Deaths(15_000, 0);

        Assert.Equal([1UL, 2UL], all.Select(e => e.Guid).Order());
        Assert.Equal(5f, all.Single(e => e.Guid == 1).X);
        Assert.Equal(8f, all.Single(e => e.Guid == 2).Y);
        Assert.Equal([2UL], recent.Select(e => e.Guid));
        Assert.Empty(recorder.Deaths(60_000, 7));
    }

    [Fact]
    public void AddJson_RecordsTheLiveFrameFields_AndDeclaresWhatItDoesNotKeep()
    {
        var recorder = new InsightRecorder(long.MaxValue, TimeSpan.FromMinutes(15));
        JsonObject frame = JsonNode.Parse("""
            {"op":"frame","seq":5,"clockMs":1250,"map":1,"instance":0,
             "entities":[
              {"g":"ABC","k":"creature","e":10,"n":"Boar","x":1.5,"y":2.5,"z":3.5,"o":0.1,"hp":42,"mt":"chase","cb":true,"d":777,
               "sp":{"id":1,"start":[0,0,0],"points":[[1,1,1]],"startClockMs":0,"durationMs":100,"run":true},"tg":"DEF"},
              {"g":"not-a-guid","k":"creature","x":0,"y":0,"z":0},
              {"g":"1","k":"player","x":9,"y":8,"z":7,"hp":100,"mt":"idle"}]}
            """)!.AsObject();

        Assert.True(recorder.AddJson(frame, out string? problem), problem);

        ReplayFrame stored = Assert.IsType<ReplayFrame>(recorder.Exact(5));
        Assert.Equal(1250, stored.ClockMs);
        Assert.Equal(1u, stored.Map);
        Assert.Equal(2, stored.Entities.Count);
        RecordedEntity boar = Assert.Single(stored.Entities, e => e.Guid == 0xABC);
        Assert.True(LiveKinds.TryParse("creature", out LiveKind creature));
        Assert.Equal((byte)creature, boar.Kind);
        Assert.Equal((1.5f, 2.5f, 3.5f), (boar.X, boar.Y, boar.Z));
        Assert.Equal((byte)42, boar.Hp);
        Assert.Equal(777u, boar.D);
        Assert.Equal(InsightRecorder.FlagCombat, boar.Flags & InsightRecorder.FlagCombat);
        Assert.Equal("chase", recorder.MotionName(boar.Flags));
        RecordedEntity player = Assert.Single(stored.Entities, e => e.Guid == 1);
        Assert.Equal(0u, player.Flags & InsightRecorder.FlagCombat);
        Assert.Equal(0u, player.D);
        Assert.Equal("idle", recorder.MotionName(player.Flags));

        Assert.Equal(["guid", "kind", "x", "y", "z", "hp", "motion", "d"], InsightRecorder.ReplayFields);
    }

    [Fact]
    public void AddJson_RefusesAFrameWithoutASequenceOrEntities_AndRecordsNothing()
    {
        var recorder = new InsightRecorder(long.MaxValue, TimeSpan.FromMinutes(15));

        Assert.False(recorder.AddJson(JsonNode.Parse("""{"op":"frame","clockMs":1,"map":0,"instance":0,"entities":[]}"""), out string? noSeq));
        Assert.False(recorder.AddJson(JsonNode.Parse("""{"op":"frame","seq":1,"clockMs":1,"map":0,"instance":0}"""), out string? noEntities));
        Assert.False(recorder.AddJson(null, out string? nothing));
        Assert.False(string.IsNullOrEmpty(noSeq));
        Assert.False(string.IsNullOrEmpty(noEntities));
        Assert.False(string.IsNullOrEmpty(nothing));
        Assert.Equal(0, recorder.FrameCount);
    }

    [Fact]
    public void AnEmptyRecorder_HasNothingToReplay()
    {
        var recorder = new InsightRecorder(1024, TimeSpan.FromMinutes(1));

        Assert.Equal(0, recorder.FrameCount);
        Assert.Equal(0, recorder.Bytes);
        Assert.Null(recorder.Latest());
        Assert.Null(recorder.Exact(1));
        Assert.Null(recorder.AtClock(0));
    }

    [Fact]
    public void ABudgetSmallerThanOneFrame_KeepsNoFrame_RatherThanExceedingIt()
    {
        var recorder = new InsightRecorder(64, TimeSpan.FromMinutes(1));
        var entities = new RecordedEntity[100];
        for (int i = 0; i < entities.Length; i++)
        {
            entities[i] = E((ulong)i + 1, i, i);
        }

        recorder.Add(1, 0, 0, 0, entities);

        Assert.True(recorder.Bytes <= 64);
        Assert.Equal(0, recorder.FrameCount);
        Assert.Equal(1, recorder.EvictedFrames);
    }
}

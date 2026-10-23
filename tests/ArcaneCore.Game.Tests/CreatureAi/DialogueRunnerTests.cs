using ArcaneCore.Game.Creatures;
using Xunit;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>
/// Pins the DialogueUpdate timer semantics the Ranshalla, Reginald Windsor, In Dreams and Nightmare Manifests scripts each carried as
/// a private copy (positional step, -1 idle sentinel, state committed before the step runs, delay 0 ends the run).
/// </summary>
public sealed class DialogueRunnerTests
{
    [Fact]
    public void NewRunner_IsIdle_AndAdvanceNeverFires()
    {
        var runner = new DialogueRunner([100, 100]);

        Assert.Equal(-1, runner.Step);
        Assert.Equal(-1, runner.Advance(1000));
        Assert.Equal(-1, runner.Step);
    }

    [Fact]
    public void Start_MakesIndexCurrent_AndLoadsItsDelay()
    {
        var runner = new DialogueRunner([100, 200, 300]);

        runner.Start(1);

        Assert.Equal(1, runner.Step);
        Assert.Equal(-1, runner.Advance(199));
        Assert.Equal(1, runner.Step);
        Assert.Equal(2, runner.Advance(1));
        Assert.Equal(2, runner.Step);
    }

    [Fact]
    public void Advance_ExactDelayFires_OneBelowDoesNot()
    {
        var runner = new DialogueRunner([100, 50]);
        runner.Start(0);

        Assert.Equal(-1, runner.Advance(99));
        Assert.Equal(0, runner.Step);
        Assert.Equal(1, runner.Advance(1));
    }

    [Fact]
    public void Advance_AccumulatesPartialDiffs()
    {
        var runner = new DialogueRunner([100, 50]);
        runner.Start(0);

        Assert.Equal(-1, runner.Advance(40));
        Assert.Equal(-1, runner.Advance(40));
        Assert.Equal(1, runner.Advance(20));
    }

    [Fact]
    public void Advance_OvershootDoesNotCarryIntoNextStep()
    {
        var runner = new DialogueRunner([100, 50, 10]);
        runner.Start(0);

        Assert.Equal(1, runner.Advance(10_000));
        Assert.Equal(-1, runner.Advance(49));
        Assert.Equal(2, runner.Advance(1));
    }

    [Fact]
    public void Advance_OnlyOneStepPerCall()
    {
        var runner = new DialogueRunner([1, 1, 1]);
        runner.Start(0);

        Assert.Equal(1, runner.Advance(5));
        Assert.Equal(1, runner.Step);
        Assert.Equal(2, runner.Advance(5));
    }

    [Fact]
    public void ZeroDelayStep_EndsTheRunOnNextAdvance_WithoutFiringTheFollowingStep()
    {
        var runner = new DialogueRunner([100, 0, 10]);
        runner.Start(0);

        Assert.Equal(1, runner.Advance(100));
        Assert.Equal(1, runner.Step);
        Assert.Equal(-1, runner.Advance(0));
        Assert.Equal(-1, runner.Step);
        Assert.Equal(-1, runner.Advance(1000));
    }

    [Fact]
    public void LastStepElapsing_EndsTheRun_WithNoFurtherStep()
    {
        var runner = new DialogueRunner([5, 5]);
        runner.Start(1);

        Assert.Equal(-1, runner.Advance(4));
        Assert.Equal(1, runner.Step);
        Assert.Equal(-1, runner.Advance(1));
        Assert.Equal(-1, runner.Step);
    }

    [Fact]
    public void Advance_ZeroDiff_OnPositiveDelayDoesNothing()
    {
        var runner = new DialogueRunner([5, 5]);
        runner.Start(0);

        Assert.Equal(-1, runner.Advance(0));
        Assert.Equal(0, runner.Step);
    }

    [Fact]
    public void Start_RestartsMidRun_ReplacingPositionAndCountdown()
    {
        var runner = new DialogueRunner([100, 100, 7]);
        runner.Start(0);
        runner.Advance(60);

        runner.Start(2);

        Assert.Equal(2, runner.Step);
        Assert.Equal(-1, runner.Advance(6));
        Assert.Equal(-1, runner.Advance(1));
        Assert.Equal(-1, runner.Step);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void Start_OutsideTable_ThrowsIndexOutOfRange(int index)
    {
        var runner = new DialogueRunner([1, 2, 3]);

        Assert.Throws<IndexOutOfRangeException>(() => runner.Start(index));
    }

    [Fact]
    public void Start_PastEnd_CommitsStepBeforeThrowing()
    {
        // Step is assigned before the delay lookup, as the per-script copies did (_step = index; _stepMs = table[index].Delay).
        var runner = new DialogueRunner([1, 2, 3]);

        Assert.Throws<IndexOutOfRangeException>(() => runner.Start(5));

        Assert.Equal(5, runner.Step);
    }
}

using ArcaneCore.Game.Reload;
using Xunit;

namespace ArcaneCore.Game.Tests.Reload;

public sealed class ReloadTransactionTests
{
    [Fact]
    public void Rollback_UndoesAppliedSteps_NewestFirst()
    {
        var order = new List<string>();
        var tx = new ReloadTransaction();
        tx.Step("a", () => order.Add("apply a"), () => order.Add("undo a"));
        tx.Step("b", () => order.Add("apply b"), () => order.Add("undo b"));

        IReadOnlyList<Exception> failures = tx.Rollback();

        Assert.Empty(failures);
        Assert.Equal(["apply a", "apply b", "undo b", "undo a"], order);
        Assert.Empty(tx.AppliedSteps);
    }

    [Fact]
    public void AStepThatThrows_IsNotRecorded_SoItsOwnUndoNeverRuns()
    {
        var order = new List<string>();
        var tx = new ReloadTransaction();
        tx.Step("a", () => order.Add("apply a"), () => order.Add("undo a"));

        Assert.Throws<InvalidOperationException>(() =>
            tx.Step("b", () => throw new InvalidOperationException("boom"), () => order.Add("undo b")));
        tx.Rollback();

        Assert.Equal(["apply a", "undo a"], order);
    }

    [Fact]
    public void Rollback_ContinuesPastAnUndoThatThrows_AndReportsIt()
    {
        var order = new List<string>();
        var tx = new ReloadTransaction();
        tx.Step("a", () => { }, () => order.Add("undo a"));
        tx.Step("b", () => { }, () => throw new InvalidOperationException("undo failed"));

        IReadOnlyList<Exception> failures = tx.Rollback();

        Assert.Equal(["undo a"], order);
        Assert.Equal("undo failed", Assert.Single(failures).Message);
    }

    [Fact]
    public void Notes_AreKeptInOrder()
    {
        var tx = new ReloadTransaction();
        tx.Note("one");
        tx.Note("two");

        Assert.Equal(["one", "two"], tx.Notes);
    }
}

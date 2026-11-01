using System.Runtime.CompilerServices;
using ArcaneCore.Game;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.World.Tests.Diagnostics;

/// <summary>
/// The stack guards of the recursive relays (spell proc and trigger chains, nested aura apply and remove, the DB, EventAI and SmartAI script
/// relays). A real stack overflow ends the process, so the recursion runs on a thread with a small stack where the guard has to stop it; and
/// the relay entry points are checked to ask the guard (the guard itself is the unit that records the fault).
/// </summary>
public sealed class StackGuardSpellTests
{
    private const int SmallStackBytes = 512 * 1024;

    [Fact]
    public void ASelfRecursingChain_StopsAtTheGuard_AndRecordsOneFaultSite()
    {
        var ledger = new FaultLedger();
        int depth = 0;
        bool completed = RunOnSmallStack(() => depth = Recurse(ledger, "test.stack-guard", 0));

        Assert.True(completed, "the recursion must end on its own");
        Assert.True(depth > 10, "the guard must allow ordinary nesting before it cuts");
        FaultSnapshot fault = Assert.Single(ledger.Snapshot());
        Assert.Equal("test.stack-guard", fault.Site);
        Assert.Equal(nameof(InsufficientExecutionStackException), fault.ExceptionType.Split('.')[^1]);
        Assert.True(fault.Count >= 1);
    }

    [Fact]
    public void ADroppedAction_RecordsEveryCut_AndTheCallerIsTold()
    {
        var ledger = new FaultLedger();
        bool entered = true;
        bool completed = RunOnSmallStack(() => entered = FillThenTry(ledger, 0));

        Assert.True(completed);
        Assert.False(entered, "TryEnter answers false once the stack is nearly used up");
        Assert.Equal(1L, ledger.Snapshot().Sum(s => s.Count));
    }

    [Fact]
    public void WithRoomLeft_TheGuardAnswersTrue_AndRecordsNothing()
    {
        var ledger = new FaultLedger();

        Assert.True(StackGuard.TryEnter("test.stack-guard", ledger));
        Assert.Empty(ledger.Snapshot());
    }

    [Theory]
    [InlineData("src/ArcaneCore.Game/Spells/Procs/SpellSystem.Procs.cs", "spell.proc")]
    [InlineData("src/ArcaneCore.Game/Spells/SpellSystem.cs", "spell.cast-chain")]
    [InlineData("src/ArcaneCore.Game/Spells/SpellSystem.Auras.cs", "spell.aura-apply")]
    [InlineData("src/ArcaneCore.Game/Spells/SpellSystem.Auras.cs", "spell.aura-remove")]
    [InlineData("src/ArcaneCore.Game/Creatures/CreatureMapSystem.RelayScripts.cs", "dbscript.relay")]
    [InlineData("src/ArcaneCore.Game/Creatures/AI/EventAi/EventAiEngine.cs", "eventai.process")]
    [InlineData("src/ArcaneCore.Game/Creatures/AI/SmartAi/SmartScript.cs", "smartai.action")]
    public void EveryRelayEntryPoint_AsksTheStackGuard(string relativePath, string site)
    {
        string text = File.ReadAllText(Path.Combine(RepositorySource.RequireRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

        Assert.Contains($"StackGuard.TryEnter(\"{site}\")", text, StringComparison.Ordinal);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Recurse(FaultLedger ledger, string site, int depth)
    {
        if (!StackGuard.TryEnter(site, ledger))
        {
            return depth;
        }

        return Recurse(ledger, site, depth + 1);
    }

    /// <summary>Recurse until the guard first refuses, then report whether it did (once), by returning its answer.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool FillThenTry(FaultLedger ledger, int depth)
    {
        if (RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            return FillThenTry(ledger, depth + 1);
        }

        return StackGuard.TryEnter("test.stack-guard", ledger);
    }

    private static bool RunOnSmallStack(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }, SmallStackBytes);
        thread.Start();
        bool finished = thread.Join(TimeSpan.FromSeconds(30));
        if (failure is not null)
        {
            throw new InvalidOperationException("the recursion threw", failure);
        }

        return finished;
    }
}

using ArcaneCore.Game.Locomotion;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Diagnostics;

/// <summary>
/// The per-tick catch sites of the world spell update, the movement observers and the playerbot coordinators log through the fault
/// ledger's throttled logger (<c>LogFault</c>), which also records the fault (<c>FaultLedger.Log</c>). One fault must reach the ledger
/// once, under one dotted site (the arcanecore.faults meter and bot-QA issue filing count records), except where a second, genuinely
/// narrower site is kept on purpose (the battleground coordinator's playerbot.bg-coordinator under playerbot.feature).
/// </summary>
public sealed class LogSpamCatchContractTests
{
    [Fact]
    public void AMovementObserverFault_IsRecordedOnce_UnderOneDottedSite()
    {
        string message = "observer fault " + Guid.NewGuid().ToString("N");
        IClientMovementObserver[] observers = [new ThrowingObserver(message)];
        var context = default(MovementObserverContext);
        var previous = default(ArcaneCore.Protocol.MovementInfo);
        var incoming = previous;

        MovementObservers.Before(observers, context, in previous, ref incoming, NullLogger.Instance);
        MovementObservers.After(observers, context, in previous, NullLogger.Instance);
        MovementObservers.Before(observers, context, in previous, ref incoming); // no logger: still recorded once

        FaultSnapshot[] mine = [.. FaultLedger.Shared.Snapshot().Where(s => s.SampleMessage.Contains(message, StringComparison.Ordinal))];
        Assert.Equal(["movement.observer-after", "movement.observer-before"], mine.Select(s => s.Site).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(2, mine.Where(s => s.Site == "movement.observer-before").Sum(s => s.Count));
        Assert.Equal(1, mine.Where(s => s.Site == "movement.observer-after").Sum(s => s.Count));
    }

    [Theory]
    [InlineData("src/ArcaneCore.World/Spells/SpellFeature.cs", "\"spell update failed\"", "\"spell.update\"", 1)]
    [InlineData("src/ArcaneCore.Playerbots/ManagedPlayerbotFeature.cs", "\"Playerbot group coordinator failed", "\"playerbot.feature\"", 1)]
    [InlineData("src/ArcaneCore.Playerbots/ManagedPlayerbotFeature.cs", "\"Playerbot battleground coordinator failed", "\"playerbot.bg-coordinator\"", 2)]
    public void PerTickCatch_LogsThroughTheThrottledFaultLogger_WithoutARedundantRecord(string file, string anchor, string logSite, int ledgerWrites)
    {
        string[] lines = File.ReadAllLines(Path.Combine(RepositorySource.RequireRoot(), file));
        int at = Array.FindIndex(lines, l => l.Contains(anchor, StringComparison.Ordinal));
        Assert.True(at >= 0, $"{file}: no line with {anchor}");

        Assert.Contains(".LogFault(", lines[at], StringComparison.Ordinal);
        Assert.Contains(logSite, lines[at], StringComparison.Ordinal);
        int open = Array.FindLastIndex(lines, at, l => l.Contains("catch (", StringComparison.Ordinal));
        string[] block = lines[open..(at + 1)];
        // LogFault records through FaultLedger.Log; each extra Record is a second count of the same fault.
        Assert.Equal(ledgerWrites, block.Count(l => l.Contains("FaultLedger.Shared.Record(", StringComparison.Ordinal) || l.Contains(".LogFault(", StringComparison.Ordinal)));
        Assert.DoesNotContain(block, l => l.Contains("FaultLedger.Shared.Record(" + logSite, StringComparison.Ordinal));
    }

    private sealed class ThrowingObserver(string message) : IClientMovementObserver
    {
        public void BeforeApply(MovementObserverContext context, in ArcaneCore.Protocol.MovementInfo previous, ref ArcaneCore.Protocol.MovementInfo incoming) => throw new InvalidOperationException(message);

        public void AfterApply(MovementObserverContext context, in ArcaneCore.Protocol.MovementInfo previous) => throw new InvalidOperationException(message);
    }
}

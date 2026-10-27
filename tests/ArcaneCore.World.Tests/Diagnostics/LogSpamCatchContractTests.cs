using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.World.Tests.Diagnostics;

/// <summary>
/// The per-tick catch sites of the world spell update and the playerbot coordinators run every world tick, so a fault that repeats
/// must go through the fault ledger's throttled logger (<c>LogFault</c>): its first lines at the usual level, then the repeats held
/// back with a "(N more since)" count. A direct <c>LogError(ex)</c> / <c>LogWarning(ex)</c> there writes a stack trace every tick.
/// Both writes of the error-handling idiom stay: the dotted <c>FaultLedger.Shared.Record</c> and the dashed <c>LogFault</c>.
/// </summary>
public sealed class LogSpamCatchContractTests
{
    [Theory]
    [InlineData("src/ArcaneCore.World/Spells/SpellFeature.cs", "\"spell update failed\"")]
    [InlineData("src/ArcaneCore.Playerbots/ManagedPlayerbotFeature.cs", "\"Playerbot group coordinator failed")]
    [InlineData("src/ArcaneCore.Playerbots/ManagedPlayerbotFeature.cs", "\"Playerbot battleground coordinator failed")]
    public void PerTickCatch_LogsThroughTheThrottledFaultLogger_AndKeepsTheLedgerRecord(string file, string anchor)
    {
        string[] lines = File.ReadAllLines(Path.Combine(RepositorySource.RequireRoot(), file));
        int at = Array.FindIndex(lines, l => l.Contains(anchor, StringComparison.Ordinal));
        Assert.True(at >= 0, $"{file}: no line with {anchor}");

        Assert.Contains(".LogFault(", lines[at], StringComparison.Ordinal);
        string[] catchBlock = lines[Math.Max(0, at - 4)..at];
        Assert.Contains(catchBlock, l => l.Contains("FaultLedger.Shared.Record(", StringComparison.Ordinal));
    }
}
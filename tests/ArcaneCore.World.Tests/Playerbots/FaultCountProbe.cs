using ArcaneCore.Kernel.Diagnostics;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Counts what one test's own faults added to <see cref="FaultLedger.Shared"/> under one site (issue #1618: <c>logger.LogFault</c> already
/// records, so a catch that also called <c>FaultLedger.Shared.Record</c> with the same site counted each fault twice). The ledger is
/// process-wide and other tests run in parallel, so only the messages that carry this probe's unique marker are counted.
/// </summary>
internal sealed class FaultCountProbe
{
    private readonly string _site;
    private readonly long _before;

    public FaultCountProbe(string site)
    {
        _site = site;
        Marker = site.Replace('.', '-') + "-once-" + Guid.NewGuid().ToString("N");
        _before = Count();
    }

    /// <summary>Text unique to this probe; the fault a test injects must carry it in its message.</summary>
    public string Marker { get; }

    /// <summary>The fault to inject.</summary>
    public InvalidOperationException Fault() => new(Marker);

    private long Count() => FaultLedger.Shared.Snapshot()
        .Where(s => s.Site == _site)
        .Sum(s => s.TopMessages.Where(m => m.Subject.Contains(Marker, StringComparison.Ordinal)).Sum(m => m.Count));

    /// <summary>Fails, naming both counts, unless the injected fault was counted exactly once under the site.</summary>
    public void AssertCountedOnce(string catchSite)
    {
        long counted = Count() - _before;
        Assert.True(counted == 1, $"{catchSite}: expected the fault to be counted 1 time under '{_site}', actual {counted}");
    }
}

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

    /// <param name="site">The ledger site the catch logs under.</param>
    /// <param name="marker">
    /// Text the counted messages contain. Null: a unique one, for a test that injects its own exception (<see cref="Fault"/>); a test whose
    /// fault message is built by the product (the Warden stop bound) names the part of it that is its own.
    /// </param>
    public FaultCountProbe(string site, string? marker = null)
    {
        _site = site;
        Marker = marker ?? site.Replace('.', '-') + "-once-" + Guid.NewGuid().ToString("N");
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

/// <summary>
/// A logger that completes <see cref="Faulted"/> when an Error entry carries an exception whose message holds the marker. A fault logged through
/// <c>LogFault</c> records into the ledger before it reaches the logger, so once this fires the fault is fully counted (no wait for a count to settle).
/// Everything else is dropped.
/// </summary>
internal sealed class FaultSignalLogger<T>(string marker) : Microsoft.Extensions.Logging.ILogger<T>
{
    private readonly TaskCompletionSource _faulted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Faulted => _faulted.Task;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel == Microsoft.Extensions.Logging.LogLevel.Error && exception?.Message.Contains(marker, StringComparison.Ordinal) == true) _faulted.TrySetResult();
    }
}

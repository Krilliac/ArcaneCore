using System.Text.RegularExpressions;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.World.Tests.Architecture;

/// <summary>
/// Forbids new exact-zero allocation assertions: <c>Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before)</c> and its
/// variants (a local named allocated/bytes/least, <c>after - before</c>, a <c>*AllocatedBy(...)</c> helper result). The per-thread
/// counter is not exact under xunit's parallel classes: a window occasionally reads one allocation quantum (7-8 KB) while the code under
/// test allocated nothing, and a runtime cache replaced by another thread's lookup can land on this thread. Use
/// <see cref="AllocationProbe.AssertEveryCleanWindowAtMost(Action, long, string, int)"/>, which discards GC- and cache-affected windows and
/// still holds every clean window to the limit. The probe itself is the one place allowed to read the counter against a limit.
/// <para>
/// Existing offenders are listed in <see cref="Baseline"/> so the rule applies from now on without a rewrite of every project at once
/// (the Kernel and Realm test projects do not reference TestSupport). The list is a ratchet: it may only shrink, and a file that no longer
/// offends must be removed from it.
/// </para>
/// </summary>
public sealed partial class AllocationAssertGrepTests
{
    // An exact-zero assertion over something that looks like an allocation measurement. Assert.Equal(0, <allocation-ish expression>) or
    // Assert.True(<allocation-ish local> == 0 ...). Whitespace and line breaks inside the call are tolerated.
    [GeneratedRegex(
        @"Assert\s*\.\s*Equal\s*\(\s*0L?\s*,[^;]{0,120}?(GetAllocatedBytes\w*|AllocatedBy\w*|\b(?:allocated|least|bytes)\b|\bafter\s*-\s*before\b)"
        + @"|Assert\s*\.\s*True\s*\(\s*(?:allocated|bytes|least)\s*==\s*0L?\s*[,)]",
        RegexOptions.CultureInvariant)]
    private static partial Regex ExactZeroAllocationAssert();

    /// <summary>Test files (relative to tests/) that still assert an exact zero. Shrink only.</summary>
    private static readonly string[] Baseline =
    [
        "ArcaneCore.Kernel.Tests/Diagnostics/InvariantTests.cs",
        "ArcaneCore.Kernel.Tests/Logging/LogSinkFollowupTests.cs",
        "ArcaneCore.Realm.Tests/Logging/LoggingAllocationTests.cs",
        "ArcaneCore.Realm.Tests/Resilience/CircuitBreakerTests.cs",
        "ArcaneCore.Realm.Tests/Security/IpRateTableTests.cs",
        "ArcaneCore.Realm.Tests/Watchdog/CounterRegistryTests.cs",
        "ArcaneCore.Realm.Tests/Watchdog/HeartbeatTests.cs",
        "ArcaneCore.Realm.Tests/Watchdog/ThreadPoolProbeTests.cs",
        "ArcaneCore.Realm.Tests/Watchdog/TickMonitorTests.cs",
        "ArcaneCore.Realm.Tests/Watchdog/TickRingTests.cs",
        "ArcaneCore.World.Tests/PacketHeuristics/PacketHeuristicsPerformanceTests.cs",
    ];

    private const string ThisFile = "ArcaneCore.World.Tests/Architecture/AllocationAssertGrepTests.cs";

    [Fact]
    public void NoNewExactZeroAllocationAssertsOutsideTheProbe()
    {
        string root = Path.Combine(RepositorySource.RequireRoot(), "tests");
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        var scanned = new List<string>();
        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            scanned.Add(relative);
            if (relative == "ArcaneCore.TestSupport/AllocationProbe.cs" || relative == ThisFile)
            {
                continue;
            }

            if (ExactZeroAllocationAssert().IsMatch(File.ReadAllText(file)))
            {
                offenders.Add(relative);
            }
        }

        // A wrong root or enumeration would scan nothing and pass vacuously: the scan must have reached known files.
        Assert.True(scanned.Count > 100, $"the scan visited only {scanned.Count} files under {root}");
        Assert.Contains("ArcaneCore.TestSupport/AllocationProbe.cs", scanned);
        foreach (string known in Baseline)
        {
            Assert.Contains(known, scanned);
        }

        string[] added = [.. offenders.Except(Baseline)];
        Assert.True(added.Length == 0,
            "An exact-zero allocation assert is not stable under parallel test classes; use AllocationProbe.AssertEveryCleanWindowAtMost: " + string.Join(", ", added));

        string[] fixedOnes = [.. Baseline.Except(offenders)];
        Assert.True(fixedOnes.Length == 0,
            "These files no longer assert an exact zero allocation; remove them from the Baseline: " + string.Join(", ", fixedOnes));
    }

    [Theory]
    [InlineData("Assert." + "Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);", true)]
    [InlineData("Assert." + "Equal(0, after - before);", true)]
    [InlineData("Assert." + "Equal(0L, allocated);", true)]
    [InlineData("Assert." + "Equal(0, least);", true)]
    [InlineData("Assert." + "Equal(0, WatchdogTestKit.AllocatedBy(() => x.Run(), 1000));", true)]
    [InlineData("Assert." + "True(allocated == 0, \"msg\");", true)]
    [InlineData("Assert." + "Equal(\n    0,\n    GC.GetAllocatedBytesForCurrentThread() - before);", true)]
    [InlineData("Assert." + "True(GC.GetAllocatedBytesForCurrentThread() - before < 1_000, \"per-call\");", false)]
    [InlineData("Assert." + "True(GC.GetAllocatedBytesForCurrentThread() - before > 0);", false)]
    [InlineData("Assert." + "Equal(0, probe.Calls);", false)]
    [InlineData("Assert." + "Equal(0, snapshot.FrameSamples);", false)]
    [InlineData("AllocationProbe.AssertEveryCleanWindowAtMost(Window, 0, \"idle\");", false)]
    public void ThePatternMatchesTheForbiddenShapeOnly(string line, bool expected)
        => Assert.Equal(expected, ExactZeroAllocationAssert().IsMatch(line));
}

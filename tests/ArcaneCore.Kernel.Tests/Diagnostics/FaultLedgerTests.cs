using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Diagnostics;

/// <summary>The fault ledger: signatures, dedup, bounds, throttled logging, threads, metrics and the crash-hook path.</summary>
[Collection(DiagnosticsCollection.Name)]
public sealed class FaultLedgerTests
{
    private sealed class OtherFaultException(string message) : Exception(message);

    private sealed class UnobservedProbeException(string message) : Exception(message);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Exception ThrowA(string message = "boom")
    {
        try { throw new InvalidOperationException(message); } catch (Exception ex) { return ex; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Exception ThrowB()
    {
        try { throw new InvalidOperationException("boom"); } catch (Exception ex) { return ex; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Exception ThrowOther()
    {
        try { throw new OtherFaultException("boom"); } catch (Exception ex) { return ex; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)] private static void F1() => F2();
    [MethodImpl(MethodImplOptions.NoInlining)] private static void F2() => F3();
    [MethodImpl(MethodImplOptions.NoInlining)] private static void F3() => F4();
    [MethodImpl(MethodImplOptions.NoInlining)] private static void F4() => F5();
    [MethodImpl(MethodImplOptions.NoInlining)] private static void F5() => F6();
    [MethodImpl(MethodImplOptions.NoInlining)] private static void F6() => F7();
    [MethodImpl(MethodImplOptions.NoInlining)] private static void F7() => throw new InvalidOperationException("deep");

    private static async Task<int> AsyncThrow<T>(T value)
    {
        await Task.Yield();
        throw new InvalidOperationException("async " + value);
    }

    private static async Task<Exception> CaptureAsync<T>(T value)
    {
        try { await AsyncThrow(value); } catch (Exception ex) { return ex; }
        throw new InvalidOperationException("unreachable");
    }

    [Fact]
    public void SameThrowSiteTwice_IsOneSignatureWithCountTwo()
    {
        var ledger = new FaultLedger();
        FaultRecording first = ledger.Record("site", ThrowA("one"));
        FaultRecording second = ledger.Record("site", ThrowA("two"));

        FaultSnapshot only = Assert.Single(ledger.Snapshot());
        Assert.Equal(2, only.Count);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(only.Id, first.Id);
        Assert.Equal(2, ledger.Total);
        Assert.Equal(1, ledger.Distinct);
        Assert.Equal("one", only.SampleMessage);
        Assert.Matches("^[0-9a-f]{16}$", only.Id);
    }

    [Fact]
    public void DifferentSiteThrowSiteOrType_AreSeparateSignatures()
    {
        var ledger = new FaultLedger();
        ledger.Record("a", ThrowA());
        ledger.Record("b", ThrowA());
        ledger.Record("a", ThrowB());
        ledger.Record("a", ThrowOther());

        Assert.Equal(4, ledger.Distinct);
        Assert.Equal(4, ledger.Snapshot().Select(s => s.Id).Distinct().Count());
    }

    [Fact]
    public async Task AsyncAndGenericFrames_AreNormalised_SoOneThrowSiteIsOneSignature()
    {
        Exception a = await CaptureAsync(1);
        Exception b = await CaptureAsync("text");

        string sigA = FaultLedger.SignatureOf("s", a, out _, out IReadOnlyList<string> framesA);
        string sigB = FaultLedger.SignatureOf("s", b, out _, out _);

        Assert.Equal(sigA, sigB);
        Assert.Contains(framesA, f => f == "FaultLedgerTests.AsyncThrow");
        Assert.All(framesA, f =>
        {
            Assert.DoesNotContain('<', f);
            Assert.DoesNotContain('`', f);
            Assert.DoesNotContain("MoveNext", f, StringComparison.Ordinal);
            Assert.DoesNotContain("d__", f, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void LambdaFrames_BecomeTheirOwningMethod_AndFramesAreCappedAtFive()
    {
        Action lambda = () => throw new InvalidOperationException("x");
        Exception? caught = null;
        try { lambda(); } catch (Exception ex) { caught = ex; }
        FaultLedger.SignatureOf("s", caught!, out _, out IReadOnlyList<string> frames);
        Assert.Equal(["FaultLedgerTests." + nameof(LambdaFrames_BecomeTheirOwningMethod_AndFramesAreCappedAtFive)], frames);

        try { F1(); } catch (Exception ex) { caught = ex; }
        FaultLedger.SignatureOf("s", caught!, out _, out frames);
        Assert.Equal(FaultLedger.TopFrames, frames.Count);
        Assert.Equal("FaultLedgerTests.F7", frames[0]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)] private static void G1() => F4();

    [Fact]
    public void OneBugReachedFromDifferentCallers_IsOneSignature_ButKeepsItsFramesForDisplay()
    {
        Exception? viaF1 = null;
        Exception? viaG1 = null;
        try { F1(); } catch (Exception ex) { viaF1 = ex; }
        try { G1(); } catch (Exception ex) { viaG1 = ex; }

        Assert.Equal(FaultLedger.SignatureOf("s", viaF1!, out _, out _), FaultLedger.SignatureOf("s", viaG1!, out _, out IReadOnlyList<string> g1Frames));
        Assert.Equal("FaultLedgerTests.G1", g1Frames[4]);
    }

    [Fact]
    public async Task AsyncLambdaFrames_ContainNoCompilerBrackets()
    {
        Func<Task> lambda = async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("x");
        };
        Exception? caught = null;
        try { await lambda(); } catch (Exception ex) { caught = ex; }

        FaultLedger.SignatureOf("s", caught!, out _, out IReadOnlyList<string> frames);
        Assert.NotEmpty(frames);
        Assert.All(frames, f => Assert.DoesNotContain('<', f));
        Assert.All(frames, f => Assert.DoesNotContain('>', f));
        Assert.Contains(nameof(AsyncLambdaFrames_ContainNoCompilerBrackets), frames[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ANewSignatureBeyondTheCap_StillLogsItsFirstOccurrence()
    {
        var ledger = new FaultLedger(new FaultLedgerLimits(MaxSignatures: 1, LogLimit: 1), TimeProvider.System);
        Assert.True(ledger.Record("s", ThrowA()).ShouldLog);
        Assert.False(ledger.Record("s", ThrowA()).ShouldLog);

        Assert.True(ledger.Record("s", ThrowB()).ShouldLog);
        Assert.True(ledger.Record("s", ThrowOther()).ShouldLog); // a different brand-new bug, in the already-busy overflow bucket
        Assert.False(ledger.Record("s", ThrowOther()).ShouldLog); // but only its first occurrence
        Assert.False(ledger.Record("s", ThrowB()).ShouldLog);
    }

    [Fact]
    public void BeyondTheCap_FaultsAreCountedInTheOverflowBucket_NeverDropped()
    {
        var ledger = new FaultLedger(new FaultLedgerLimits(MaxSignatures: 2), TimeProvider.System);
        ledger.Record("s", ThrowA());
        ledger.Record("s", ThrowB());
        ledger.Record("s", ThrowOther());
        ledger.Record("s", ThrowOther());
        ledger.Record("s", ThrowA());

        Assert.Equal(2, ledger.Distinct);
        Assert.Equal(5, ledger.Total);
        IReadOnlyList<FaultSnapshot> all = ledger.Snapshot();
        FaultSnapshot overflow = all[^1];
        Assert.Equal(FaultLedger.OverflowId, overflow.Id);
        Assert.Equal(2, overflow.Count);
        Assert.Equal(5, all.Sum(s => s.Count));
    }

    [Fact]
    public void RepeatLogging_IsThrottledAfterTheLimit_WithAMoreSinceSuffix()
    {
        var time = new Clock();
        var ledger = new FaultLedger(new FaultLedgerLimits(LogLimit: 3), time);
        var log = new CapturingLogger();
        for (int i = 0; i < 10; i++)
        {
            ledger.Log(log, LogLevel.Error, "s", ThrowA(), "it failed");
        }

        Assert.Equal(3, log.Entries.Count);
        Assert.DoesNotContain(log.Entries, e => e.Contains("more since", StringComparison.Ordinal));
        Assert.Equal(10, ledger.Total);

        time.Advance(TimeSpan.FromSeconds(30));
        ledger.Log(log, LogLevel.Error, "s", ThrowA(), "it failed");
        Assert.Equal(3, log.Entries.Count);

        time.Advance(TimeSpan.FromSeconds(31));
        ledger.Log(log, LogLevel.Error, "s", ThrowA(), "it failed");
        Assert.Equal(4, log.Entries.Count);
        Assert.Contains("(8 more since)", log.Entries[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void ParallelRecording_LosesNothing()
    {
        var ledger = new FaultLedger();
        Exception shared = ThrowA();
        Exception[] others = [ThrowB(), ThrowOther()];
        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, worker =>
        {
            for (int i = 0; i < 2000; i++)
            {
                ledger.Record("p", shared);
                ledger.Record("q" + (i % 25), others[worker % 2]);
                _ = ledger.Snapshot();
            }
        });

        Assert.Equal(32_000, ledger.Total);
        Assert.Equal(32_000, ledger.Snapshot().Sum(s => s.Count));
        Assert.Equal(16_000, ledger.Snapshot().Single(s => s.Site == "p").Count);
    }

    [Fact]
    public void Reset_ForgetsEverything()
    {
        var ledger = new FaultLedger();
        ledger.Record("s", ThrowA());
        ledger.Reset();
        Assert.Equal(0, ledger.Total);
        Assert.Empty(ledger.Snapshot());
        Assert.Null(ledger.LastSeen);
    }

    [Fact]
    public void Record_EmitsTheFaultsCounter_WithSiteAndTypeTags_AndTheGauges()
    {
        _ = FaultMeters.Meter;
        var seen = new List<(long Value, string Site, string Type)>();
        var gauges = new Dictionary<string, long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "ArcaneCore.Faults") l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "arcanecore.faults")
            {
                string site = "", type = "";
                foreach (KeyValuePair<string, object?> tag in tags)
                {
                    if (tag.Key == "site") site = (string)tag.Value!;
                    if (tag.Key == "type") type = (string)tag.Value!;
                }

                lock (seen) seen.Add((value, site, type));
            }
            else
            {
                lock (gauges) gauges[instrument.Name] = value;
            }
        });
        listener.Start();

        FaultLedger.Shared.Record("metrics-site", ThrowOther());
        listener.RecordObservableInstruments();

        lock (seen) Assert.Contains(seen, m => m.Value == 1 && m.Site == "metrics-site" && m.Type == typeof(OtherFaultException).FullName);
        lock (gauges)
        {
            Assert.True(gauges["arcanecore.faults.distinct"] >= 1);
            Assert.True(gauges.ContainsKey("arcanecore.invariant.failures"));
        }
    }

    [Fact]
    public void AnUnobservedTaskException_IsRecordedByTheCrashHandler()
    {
        bool created = CrashHandler.Current is null;
        CrashHandler handler = CrashHandler.Install(DumpFree.Options(), new NullSink());
        try
        {
            bool recorded = false;
            for (int attempt = 0; attempt < 40 && !recorded; attempt++)
            {
                AbandonFaultedTask();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                recorded = FaultLedger.Shared.Snapshot().Any(s => s.Site == "unobserved-task" && s.ExceptionType == typeof(UnobservedProbeException).FullName);
            }

            Assert.True(recorded, "the unobserved task exception reached the fault ledger through CrashHandler");
        }
        finally
        {
            if (created) handler.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AbandonFaultedTask()
    {
        Task task = Task.Run(() => throw new UnobservedProbeException("never awaited"));
        SpinWait.SpinUntil(() => task.IsFaulted, TimeSpan.FromSeconds(5));
    }

    private sealed class NullSink : ICrashSink
    {
        public bool IsSynchronous => true;

        public void Write(LogLevel level, string report, Exception? exception)
        {
        }
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan span) => _now += span;
    }

    [Fact]
    public void FormatCondensedStack_And_FormatExceptionChain_IncludeCallChainAndInnerCauses()
    {
        Exception? caught = null;
        try
        {
            F1();
        }
        catch (Exception inner)
        {
            try
            {
                throw new InvalidOperationException("outer failure", inner);
            }
            catch (Exception outer)
            {
                caught = outer;
            }
        }

        string stack = FaultLedger.FormatCondensedStack(caught!);
        Assert.Contains("FaultLedgerTests.F7", stack, StringComparison.Ordinal);
        Assert.Contains("<-", stack, StringComparison.Ordinal);

        string chain = FaultLedger.FormatExceptionChain(caught!);
        Assert.Contains("InvalidOperationException: outer failure <- InvalidOperationException: deep", chain, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordAndLog_PopulateSampleStack_SampleChain_NoticeCondensedStack_AndLogMetadata()
    {
        var ledger = new FaultLedger();
        var log = new CapturingLogger();
        FaultNotice? noticed = null;
        ledger.FaultNoticed += n => noticed = n;

        Exception ex = ThrowA("stack-check");
        ledger.Log(log, LogLevel.Error, "test-site", ex, "failed op");

        FaultSnapshot snap = Assert.Single(ledger.Snapshot());
        Assert.NotEmpty(snap.SampleStack);
        Assert.Contains("FaultLedgerTests.ThrowA", snap.SampleStack, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException: stack-check", snap.SampleChain, StringComparison.Ordinal);

        Assert.NotNull(noticed);
        Assert.Equal(snap.SampleStack, noticed!.CondensedStack);

        string entry = Assert.Single(log.Entries);
        Assert.Contains($"[fault={snap.Id} count=1 stack=", entry, StringComparison.Ordinal);
    }

    [Fact]
    public void SelfDiagnosisReport_And_CrashReport_IncludeRecentFaultsWithStacks()
    {
        var ledger = new FaultLedger();
        ledger.Record("diag-site", ThrowA("diag-boom"));

        SelfDiagnosisValues diag = SelfDiagnosisReport.Capture(ledger);
        Assert.Equal(1, diag.TotalFaults);
        Assert.Equal(1, diag.DistinctFaults);
        IReadOnlyList<string> lines = SelfDiagnosisReport.Format(diag);
        Assert.Contains(lines, l => l.Contains("diag-site", StringComparison.Ordinal) && l.Contains("FaultLedgerTests.ThrowA", StringComparison.Ordinal));

        FaultLedger.Shared.Record("crash-report-site", ThrowA("crash-boom"));
        string crashText = CrashReport.Render(CrashKind.Unhandled, ThrowA("fatal"), [], DateTime.UtcNow);
        Assert.Contains("[faults]", crashText, StringComparison.Ordinal);
        Assert.Contains("crash-report-site", crashText, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordIfUnexpected_SkipsExpectedCancellation_AndRecordsUnexpectedExceptions()
    {
        var ledger = new FaultLedger();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        FaultRecording cancelled = ledger.RecordIfUnexpected("net-session", new OperationCanceledException(cts.Token), cts.Token);
        Assert.Equal(FaultLedger.OverflowId, cancelled.Id);
        Assert.Equal(0, cancelled.Count);
        Assert.False(cancelled.ShouldLog);
        Assert.Equal(0, ledger.Total);

        FaultRecording taskCancelled = ledger.RecordIfUnexpected("db:query", new TaskCanceledException("cancelled"), CancellationToken.None);
        Assert.Equal(FaultLedger.OverflowId, taskCancelled.Id);
        Assert.Equal(0, taskCancelled.Count);
        Assert.False(taskCancelled.ShouldLog);
        Assert.Equal(0, ledger.Total);

        FaultRecording unrequestedOce = ledger.RecordIfUnexpected("world-tick", new OperationCanceledException("unexpected oce"), CancellationToken.None);
        Assert.Equal(1, unrequestedOce.Count);
        Assert.True(unrequestedOce.ShouldLog);
        Assert.Equal(1, ledger.Total);

        FaultRecording boom = ledger.RecordIfUnexpected("map-update", ThrowA("unexpected fault"), cts.Token);
        Assert.Equal(1, boom.Count);
        Assert.Equal(2, ledger.Total);
        Assert.Equal(2, ledger.Distinct);
    }

    [Fact]
    public void BySubsystem_GroupsByCanonicalSubsystem_AndReturnsSortedSummary()
    {
        var ledger = new FaultLedger();
        ledger.Record("map.update", ThrowA("m1"));
        ledger.Record("map.update", ThrowA("m2"));
        ledger.Record("map-deferred", ThrowB());
        ledger.Record("db:save", ThrowA("db1"));
        ledger.Record("worldtick", ThrowOther());

        IReadOnlyDictionary<string, (int Signatures, long TotalCount)> subsystems = ledger.BySubsystem();
        Assert.Equal(["db", "map", "world"], subsystems.Keys);
        Assert.Equal((1, 1L), subsystems["db"]);
        Assert.Equal((2, 3L), subsystems["map"]);
        Assert.Equal((1, 1L), subsystems["world"]);

        IReadOnlyList<string> reportLines = SelfDiagnosisReport.Format(ledger);
        Assert.Contains(reportLines, l => l.Contains("subsystems: db=1 (1 sig), map=3 (2 sig), world=1 (1 sig)", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("characters.delete-complete", "character")]
    [InlineData("writequeue.character-retry", "writequeue")]
    [InlineData("instance-data.update", "instance")]
    [InlineData("outdoorpvp-tick", "outdoor-pvp")]
    [InlineData("outdoor-pvp.feature", "outdoor-pvp")]
    [InlineData("logon/auth/io", "logon")]
    [InlineData("watchdog.check:tick", "watchdog")]
    [InlineData("playerbot", "playerbot")]
    [InlineData("", "unknown")]
    public void DiagnosticSubsystem_OfSite_UsesOneRuleForEveryForm(string site, string expected)
        => Assert.Equal(expected, DiagnosticSubsystem.OfSite(site));
}

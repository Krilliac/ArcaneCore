using System.Runtime.CompilerServices;
using ArcaneCore.Kernel.Diagnostics;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Diagnostics;

/// <summary>The pieces the fault, anomaly and invariant ledgers share: the delta tracker, the reset epochs and the combined subsystem view.</summary>
[Collection(DiagnosticsCollection.Name)]
public sealed class UnifiedDiagnosticsTests
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Exception Throw(string message = "boom")
    {
        try { throw new InvalidOperationException(message); } catch (Exception ex) { return ex; }
    }

    [Fact]
    public void DeltaTracker_ReportsOnlyNewOccurrences_AndForgetsKeysNotSeenInAPass()
    {
        var tracker = new DiagnosticDeltaTracker();
        tracker.BeginPass(0);
        Assert.Equal(3, tracker.Added("a", 3, out bool fromStart));
        Assert.True(fromStart);
        tracker.EndPass();

        tracker.BeginPass(0);
        Assert.Equal(2, tracker.Added("a", 5, out fromStart));
        Assert.False(fromStart);
        tracker.EndPass();

        tracker.BeginPass(0);
        tracker.EndPass(); // "a" not passed: forgotten

        tracker.BeginPass(0);
        Assert.Equal(5, tracker.Added("a", 5));
        tracker.EndPass();
    }

    [Fact]
    public void DeltaTracker_ANewEpochStartsEveryKeyOver_EvenWhenTheCountGrewPastTheOldOne()
    {
        var tracker = new DiagnosticDeltaTracker();
        tracker.BeginPass(1);
        Assert.Equal(2, tracker.Added("a", 2));
        tracker.EndPass();

        // Reset, then three new occurrences before the next pass: the count (3) is above the remembered 2, so only the epoch shows the reset.
        tracker.BeginPass(2);
        Assert.Equal(3, tracker.Added("a", 3, out bool fromStart));
        Assert.True(fromStart);
        tracker.EndPass();
    }

    [Fact]
    public void Ledgers_ChangeTheirEpochOnReset()
    {
        var faults = new FaultLedger();
        var anomalies = new AnomalyLedger();
        long faultEpoch = faults.Epoch;
        long anomalyEpoch = anomalies.Epoch;

        faults.Record("map.update", Throw());
        anomalies.Report("map", "stuck", "creature 1", "detail");
        Assert.Equal(faultEpoch, faults.Epoch);
        Assert.Equal(anomalyEpoch, anomalies.Epoch);

        faults.Reset();
        anomalies.Reset();
        Assert.NotEqual(faultEpoch, faults.Epoch);
        Assert.NotEqual(anomalyEpoch, anomalies.Epoch);
    }

    [Fact]
    public void Overview_GroupsFaultsAndAnomaliesUnderOneCanonicalSubsystem()
    {
        var faults = new FaultLedger();
        var anomalies = new AnomalyLedger();
        faults.Record("instances.manager", Throw("a"));
        faults.Record("instance.update", Throw("b"));
        faults.Record("outdoorpvp-tick", Throw("c"));
        anomalies.Report("outdoor-pvp", "capture-stuck", "zone 1", "detail", AnomalySeverity.Error);
        anomalies.Report("loot", "missing", "creature 2", "detail", AnomalySeverity.Info);
        anomalies.Report("loot", "missing", "creature 3", "detail", AnomalySeverity.Warning);

        IReadOnlyList<DiagnosticSubsystemSummary> rows = DiagnosticOverview.BySubsystem(faults.Snapshot(), anomalies.Snapshot());

        DiagnosticSubsystemSummary instance = Assert.Single(rows, r => r.Subsystem == "instance");
        Assert.Equal(2, instance.Faults);
        Assert.Equal(0, instance.Anomalies);
        Assert.Null(instance.WorstAnomaly);

        DiagnosticSubsystemSummary pvp = Assert.Single(rows, r => r.Subsystem == "outdoor-pvp");
        Assert.Equal(1, pvp.Faults);
        Assert.Equal(1, pvp.Anomalies);
        Assert.Equal(AnomalySeverity.Error, pvp.WorstAnomaly);

        DiagnosticSubsystemSummary loot = Assert.Single(rows, r => r.Subsystem == "loot");
        Assert.Equal(2, loot.Anomalies);
        Assert.Equal(1, loot.AnomalyKeys);
        Assert.Equal(AnomalySeverity.Warning, loot.WorstAnomaly);

        Assert.Equal(["instance", "loot", "outdoor-pvp"], rows.Select(r => r.Subsystem));
    }

    [Fact]
    public void AnomalyLedger_BySubsystem_UsesTheCanonicalName()
    {
        var anomalies = new AnomalyLedger();
        anomalies.Report("bg", "feature-fault", "x", "detail");
        anomalies.Report("battleground", "feature-fault", "y", "detail");

        IReadOnlyDictionary<string, (int Codes, long Total)> by = anomalies.BySubsystem();
        Assert.Equal((2, 2L), by["battleground"]);
        Assert.False(by.ContainsKey("bg"));
    }
}

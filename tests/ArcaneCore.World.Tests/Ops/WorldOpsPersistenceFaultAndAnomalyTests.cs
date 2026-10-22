using System.Collections.Concurrent;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Reload;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Reload;
using ArcaneCore.World.Features;
using ArcaneCore.World.HotCode.Modules;
using ArcaneCore.World.Ops.Announce;
using ArcaneCore.World.Reload;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Ops;

/// <summary>
/// Verifies that runtime/ops/persistence/reload/hotcode catch blocks record to <see cref="FaultLedger.Shared"/>
/// and <see cref="AnomalyLedger.Shared"/>.
/// </summary>
public sealed class WorldOpsPersistenceFaultAndAnomalyTests
{
    [Fact]
    public async Task WorldLifecycleHooks_WhenHookThrows_RecordsFaultAndFeatureAnomaly()
    {
        var faults = new ConcurrentQueue<FaultNotice>();
        var anomalies = new ConcurrentQueue<AnomalyNotice>();
        void OnFault(FaultNotice n) => faults.Enqueue(n);
        void OnAnomaly(AnomalyNotice n) => anomalies.Enqueue(n);

        FaultLedger.Shared.FaultNoticed += OnFault;
        AnomalyLedger.Shared.Reported += OnAnomaly;
        try
        {
            var hooks = new WorldLifecycleHooks(NullLogger<WorldLifecycleHooks>.Instance);
            hooks.Register(WorldLifecycleStage.AfterWorldStart, "broken-hook", _ => throw new InvalidOperationException("hook boom"));

            await Assert.ThrowsAsync<AggregateException>(() => hooks.RunAsync(WorldLifecycleStage.AfterWorldStart));

            Assert.Contains(faults, f => f.Site == "feature.lifecycle" && f.ExceptionType == typeof(InvalidOperationException).FullName);
            Assert.Contains(anomalies, a => a.Subsystem == "feature" && a.Code == "lifecycle-hook-fault" && a.Subject == "AfterWorldStart:broken-hook");
        }
        finally
        {
            FaultLedger.Shared.FaultNoticed -= OnFault;
            AnomalyLedger.Shared.Reported -= OnAnomaly;
        }
    }

    [Fact]
    public void SwapUndo_WhenRollbackStepThrows_RecordsHotcodeUndoFaultAndAnomaly()
    {
        var faults = new ConcurrentQueue<FaultNotice>();
        var anomalies = new ConcurrentQueue<AnomalyNotice>();
        void OnFault(FaultNotice n) => faults.Enqueue(n);
        void OnAnomaly(AnomalyNotice n) => anomalies.Enqueue(n);

        FaultLedger.Shared.FaultNoticed += OnFault;
        AnomalyLedger.Shared.Reported += OnAnomaly;
        try
        {
            var undo = new SwapUndo();
            undo.Add("opcodes", () => throw new InvalidOperationException("rollback boom"));

            string? err = undo.RollBack((_, _) => { });

            Assert.NotNull(err);
            Assert.Contains(faults, f => f.Site == "hotcode.undo" && f.ExceptionType == typeof(InvalidOperationException).FullName);
            Assert.Contains(anomalies, a => a.Subsystem == "hotcode" && a.Code == "undo-failed" && a.Subject == "opcodes");
        }
        finally
        {
            FaultLedger.Shared.FaultNoticed -= OnFault;
            AnomalyLedger.Shared.Reported -= OnAnomaly;
        }
    }

    [Fact]
    public async Task ReloadCoordinator_WhenBuildAndSwapAndRollbackFail_RecordsReloadFaultsAndAnomalies()
    {
        using var world = new WorldRuntime(
            new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 },
            new NullSaveQueue(),
            NullLogger<WorldRuntime>.Instance);
        var coordinator = new ReloadCoordinator(NullLogger.Instance);
        coordinator.Attach(world);
        world.Start();

        var faults = new ConcurrentQueue<FaultNotice>();
        var anomalies = new ConcurrentQueue<AnomalyNotice>();
        void OnFault(FaultNotice n) => faults.Enqueue(n);
        void OnAnomaly(AnomalyNotice n) => anomalies.Enqueue(n);

        FaultLedger.Shared.FaultNoticed += OnFault;
        AnomalyLedger.Shared.Reported += OnAnomaly;
        try
        {
            coordinator.Register(new ThrowingReloadable("broken_table"));
            ReloadResult result = await coordinator.ReloadAsync("broken_table");

            Assert.Equal(ReloadStatus.Failed, result.Status);
            Assert.Contains(faults, f => f.Site == "reload.coordinator");
            Assert.Contains(anomalies, a => a.Subsystem == "reload" && a.Code == "apply-failed" && a.Subject == "broken_table");
            Assert.Contains(anomalies, a => a.Subsystem == "reload" && a.Code == "rollback-failed" && a.Subject == "broken_table");
        }
        finally
        {
            FaultLedger.Shared.FaultNoticed -= OnFault;
            AnomalyLedger.Shared.Reported -= OnAnomaly;
        }
    }

    [Fact]
    public void OpsAnnouncer_WhenSinkThrows_RecordsFaultWithoutInfiniteRecursion()
    {
        var faults = new ConcurrentQueue<FaultNotice>();
        var announcer = new OpsAnnouncer(
            new AnnounceOptions { Enabled = true },
            new ThrowingSink(),
            NullLogger<OpsAnnouncer>.Instance);

        void OnFault(FaultNotice n)
        {
            faults.Enqueue(n);
            // Simulate FaultAnnouncerFeature re-entering Announce when a fault is noticed:
            announcer.Announce(AnnounceCategory.Faults, AnnounceSeverity.Error, $"fault at {n.Site}");
        }

        FaultLedger.Shared.FaultNoticed += OnFault;
        try
        {
            announcer.Announce(AnnounceCategory.HotSwap, AnnounceSeverity.Info, "test reload");

            Assert.Contains(faults, f => f.Site == "ops.announcer" && f.ExceptionType == typeof(InvalidOperationException).FullName);
        }
        finally
        {
            FaultLedger.Shared.FaultNoticed -= OnFault;
        }
    }

    private sealed class NullSaveQueue : ICharacterSaveQueue
    {
        public void Enqueue(CharacterState state)
        {
        }
    }

    private sealed class ThrowingSink : IAnnounceSink
    {
        public void Deliver(AnnounceDelivery delivery) => throw new InvalidOperationException("sink boom");
    }

    private sealed class ThrowingReloadable(string name) : IContentReloadable
    {
        public string Name { get; } = name;

        public bool IncludedInAll => true;

        public IReadOnlyCollection<string> CommitAfter => [];

        public Task<ContentCandidate> BuildAsync(CancellationToken cancellationToken)
            => Task.FromResult<ContentCandidate>(new ThrowingCandidate());

        private sealed class ThrowingCandidate : ContentCandidate
        {
            public override string Summary => "throwing candidate";

            public override IReadOnlyList<string> Validate() => [];

            public override void Commit(WorldRuntime world, ReloadTransaction transaction)
            {
                transaction.Step("first", () => { }, () => throw new InvalidOperationException("undo step boom"));
                transaction.Step("second", () => throw new InvalidOperationException("swap step boom"), () => { });
            }
        }
    }
}

using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Features;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Ops.Announce;

/// <summary>
/// Tells the staff about each fault the <see cref="FaultLedger"/> wants logged (World:Announce:Faults): the first occurrences of a new
/// signature and the throttled repeats, never every occurrence. It subscribes to <see cref="FaultLedger.FaultNoticed"/> at attach and
/// unsubscribes at stop; when the host registered no ledger or no announcer it attaches nothing. The handler runs on whatever thread
/// recorded the fault; <see cref="IOpsAnnouncer.Announce"/> is thread-safe, never blocks and never throws. A fault is always staff-only
/// (<c>gmOnly</c>) and is deduplicated by its signature id. An unhandled exception that ends the process is announced as Critical but its
/// delivery is racing the shutdown: the log line and the fault ledger are the record.
/// </summary>
public sealed class FaultAnnouncerFeature(IServiceProvider services) : IWorldFeature
{
    private FaultLedger? _ledger;
    private IOpsAnnouncer? _announcer;

    public void Attach(WorldRuntime world)
    {
        _announcer = services.GetService<IOpsAnnouncer>();
        _ledger = services.GetService<FaultLedger>();
        if (_announcer is null || _ledger is null)
        {
            return;
        }

        _ledger.FaultNoticed += OnFault;
    }

    public Task StopAsync()
    {
        if (_ledger is { } ledger)
        {
            ledger.FaultNoticed -= OnFault;
        }

        return Task.CompletedTask;
    }

    private void OnFault(FaultNotice notice)
    {
        bool fatal = notice.Site is "unhandled" or "unobserved-task";
        string more = notice.Suppressed > 0 ? $", {notice.Suppressed} repeats held back" : string.Empty;
        _announcer!.Announce(
            AnnounceCategory.Faults,
            fatal ? AnnounceSeverity.Critical : AnnounceSeverity.Warning,
            fatal ? $"Unhandled server exception: {notice.ExceptionType}" : $"Server fault: {notice.ExceptionType} at {notice.Site}",
            $"{notice.Message}; seen {notice.Count} time(s){more}",
            [new AnnounceField("fault", notice.Id)],
            gmOnly: true,
            dedupKey: notice.Id);
    }
}

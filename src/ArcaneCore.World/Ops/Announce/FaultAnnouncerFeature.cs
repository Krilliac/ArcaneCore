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
/// (<c>gmOnly</c>) and is deduplicated by its signature id. Only a fault that ends the process is Critical (which the rate limit never holds
/// back): an unhandled exception, or an unobserved task exception whose <c>Diagnostics:OnUnobservedTask</c> policy terminates
/// (<see cref="FaultNotice.Terminating"/>). Under the Log policy the process lives on, so an unobserved task exception is Error and stays
/// rate limited: a storm of distinct signatures cannot become hundreds of staff lines. A Critical notice's delivery races the shutdown:
/// the log line and the fault ledger are the record.
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
        // An unhandled exception always ends the process (the runtime terminates after the handler, whatever the policy).
        bool fatal = notice.Terminating || notice.Site == "unhandled";
        bool unobserved = !fatal && notice.Site == "unobserved-task";
        string more = notice.Suppressed > 0 ? $", {notice.Suppressed} repeats held back" : string.Empty;
        string stack = string.IsNullOrEmpty(notice.CondensedStack) ? string.Empty : $" [stack: {notice.CondensedStack}]";
        _announcer!.Announce(
            AnnounceCategory.Faults,
            fatal ? AnnounceSeverity.Critical : unobserved ? AnnounceSeverity.Error : AnnounceSeverity.Warning,
            fatal ? $"Unhandled server exception: {notice.ExceptionType}"
                : unobserved ? $"Unobserved task exception: {notice.ExceptionType}"
                : $"Server fault: {notice.ExceptionType} at {notice.Site}",
            $"{notice.Message}; seen {notice.Count} time(s){more}{stack}",
            [new AnnounceField("fault", notice.Id)],
            gmOnly: true,
            dedupKey: notice.Id);
    }
}

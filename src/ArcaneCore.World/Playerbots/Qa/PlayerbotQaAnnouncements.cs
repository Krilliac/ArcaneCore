using ArcaneCore.Kernel.Configuration;
using ArcaneCore.World.Ops.Announce;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>
/// The staff announcement of a new bot QA finding (World:Announce:BotQa). Bot QA issues are public, so the text names no player, bot or
/// GUID: the kind, the subject in words (quest, spell, map cell) and the fingerprint id. Deduplicated by that id, so a finding that is
/// later evicted and seen again inside the dedup window is not announced twice.
/// </summary>
internal static class PlayerbotQaAnnouncements
{
    internal static void Announce(IOpsAnnouncer announcer, QaFindingView view)
    {
        bool fault = view.Kind is QaSignalKind.ActionFault or QaSignalKind.Quarantine or QaSignalKind.Disabled or QaSignalKind.ServerFault;
        announcer.Announce(
            AnnounceCategory.BotQa,
            fault ? AnnounceSeverity.Warning : AnnounceSeverity.Info,
            $"New bot QA finding: {view.Kind}",
            view.Subject,
            [new AnnounceField("finding", view.Id)],
            gmOnly: true,
            dedupKey: view.Id);
    }
}

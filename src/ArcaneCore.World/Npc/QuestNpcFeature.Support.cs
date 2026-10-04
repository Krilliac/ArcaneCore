using ArcaneCore.Game.Npc;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Npc;

public sealed partial class QuestNpcFeature
{
    /// <summary>
    /// Startup report of the quest support gate: how many active quests are offered and, per missing adapter, how
    /// many are withheld. Always evaluates the summary so a duplicate adapter provider fails at startup, not at the
    /// first accept; the log line is Quests:LogWithheld.
    /// </summary>
    private void LogSupportSummary()
    {
        QuestSupportSummary summary = Services.SupportSummary();
        if (!Options.LogWithheld)
        {
            return;
        }

        string reasons = summary.WithheldByReason.Count == 0
            ? "none"
            : string.Join(", ", summary.WithheldByReason.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}"));
        _logger.LogInformation("Quest support: {Supported} of {Active} active quests are offered, {Withheld} withheld (a quest can lack several adapters): {Reasons}",
            summary.Supported, summary.ActiveQuests, summary.Withheld, reasons);
    }
}

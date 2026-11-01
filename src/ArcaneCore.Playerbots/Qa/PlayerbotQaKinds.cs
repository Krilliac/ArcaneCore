namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>
/// What every part of bot QA needs to know about one <see cref="QaSignalKind"/>, in one row: the label people read, the slug of its optional
/// GitHub label, its severity weight, whether bots must be counted before it is reported, and whether one occurrence is enough.
/// </summary>
/// <param name="Kind">The kind.</param>
/// <param name="Label">The words of the report and the issue title (<c>death loop</c>).</param>
/// <param name="Slug">The lower-case, dash-separated name of its GitHub label (<c>bot-qa:death-loop</c>, <see cref="PlayerbotQaGitHubOptions.KindLabels"/>).</param>
/// <param name="Weight">The kind's part of the severity (<see cref="PlayerbotQaClassifier.Severity"/>).</param>
/// <param name="BotIndependent">The finding comes from data rather than from bots: it has no bot count to threshold on.</param>
/// <param name="SingleOccurrence">One occurrence is enough to report it (instead of <see cref="PlayerbotQaGitHubOptions.MinOccurrences"/>).</param>
/// <param name="SpellKind">A spell finding: it carries the spell section in its issue and counts against the spell publish caps.</param>
internal sealed record QaKindInfo(QaSignalKind Kind, string Label, string Slug, double Weight, bool BotIndependent, bool SingleOccurrence, bool SpellKind)
{
    /// <summary>The kind's GitHub label (added to the configured labels when <see cref="PlayerbotQaGitHubOptions.KindLabels"/> is on).</summary>
    public string GitHubLabel => "bot-qa:" + Slug;

    /// <summary>
    /// The finding is the server's own fault (an exception, a hang), not what a bot did: it is published ahead of every other kind and counts against
    /// <see cref="PlayerbotQaGitHubOptions.MaxServerFaultIssuesPerHour"/> instead of the shared hourly cap.
    /// </summary>
    public bool ServerSide => Kind is QaSignalKind.ServerFault or QaSignalKind.TickHang;
}

/// <summary>
/// The one table of the kinds. Adding a <see cref="QaSignalKind"/> means adding its row here and its cases to
/// <see cref="PlayerbotQaFingerprint.Key"/>, <see cref="PlayerbotQaAggregator.Subject"/> and <see cref="PlayerbotQaClassifier.Classify"/>; the table
/// throws at type load when a kind has no row (or two), and PlayerbotQaKindExhaustivenessTests runs every kind through every switch, so a missed
/// case fails a test instead of throwing on the QA task.
/// </summary>
internal static class QaKinds
{
    private static readonly QaKindInfo[] s_rows = Build(
    [
        new(QaSignalKind.Stall, "stall", "stall", 15, BotIndependent: false, SingleOccurrence: false, SpellKind: false),
        new(QaSignalKind.SharedSetAside, "shared set-aside", "shared-set-aside", 35, false, false, false),
        new(QaSignalKind.DeathLoop, "death loop", "death-loop", 30, false, false, false),
        new(QaSignalKind.ActionFault, "action fault", "action-fault", 40, BotIndependent: true, SingleOccurrence: true, SpellKind: false),
        new(QaSignalKind.Quarantine, "quarantine", "quarantine", 45, BotIndependent: true, SingleOccurrence: true, SpellKind: false),
        new(QaSignalKind.Disabled, "disabled", "disabled", 45, BotIndependent: true, SingleOccurrence: true, SpellKind: false),
        new(QaSignalKind.RelayDataMissing, "relay data", "relay-data", 25, BotIndependent: true, SingleOccurrence: false, SpellKind: false),
        new(QaSignalKind.RelayScriptMissing, "relay script", "relay-script", 25, BotIndependent: true, SingleOccurrence: false, SpellKind: false),
        new(QaSignalKind.SpellUnsupported, "spell unsupported", "spell-unsupported", 30, BotIndependent: true, SingleOccurrence: true, SpellKind: true),
        new(QaSignalKind.SpellRefused, "spell refused", "spell-refused", 20, BotIndependent: false, SingleOccurrence: false, SpellKind: true),
        new(QaSignalKind.SpellNoEffect, "spell no effect", "spell-no-effect", 28, BotIndependent: false, SingleOccurrence: false, SpellKind: true),
        new(QaSignalKind.ServerFault, "server fault", "server-fault", 42, BotIndependent: true, SingleOccurrence: true, SpellKind: false),
        // Run failed 24: a give-up is the dungeon system working as designed most of the time (a wipe, a slow walk), so it ranks just under a relay gap (25)
        // and needs MinOccurrences repeats at one stage before it becomes an issue; the count term then lifts a stage that fails again and again.
        new(QaSignalKind.RunFailed, "run failed", "run-failed", 24, BotIndependent: true, SingleOccurrence: false, SpellKind: false),
        // Tick hang 50: the only kind that freezes every player, not just bots, so it outranks the worst bot fault (quarantine and disabled, 45). It needs
        // MinOccurrences like the others: one hang can still be the host (the report is filtered for the host verdict, GC, start-up and shutdown, but not perfectly), and a
        // one-off kind would reopen closed issues on every deploy.
        new(QaSignalKind.TickHang, "tick hang", "tick-hang", 50, BotIndependent: true, SingleOccurrence: false, SpellKind: false),
        new(QaSignalKind.UnreachableGoal, "unreachable goal", "unreachable-goal", 18, BotIndependent: false, SingleOccurrence: false, SpellKind: false),
        new(QaSignalKind.Anomaly, "bot anomaly", "bot-anomaly", 26, BotIndependent: true, SingleOccurrence: true, SpellKind: false),
    ]);

    /// <summary>Every row, in kind order.</summary>
    internal static IReadOnlyList<QaKindInfo> All => s_rows;

    /// <summary>The row of <paramref name="kind"/>; an undefined value throws.</summary>
    internal static QaKindInfo Of(QaSignalKind kind)
        => (uint)kind < (uint)s_rows.Length ? s_rows[(int)kind] : throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a bot QA kind");

    private static QaKindInfo[] Build(QaKindInfo[] rows)
    {
        QaSignalKind[] kinds = Enum.GetValues<QaSignalKind>();
        var byKind = new QaKindInfo[kinds.Length];
        foreach (QaKindInfo row in rows)
        {
            int index = (int)row.Kind;
            if (index < 0 || index >= byKind.Length || byKind[index] is not null)
            {
                throw new InvalidOperationException($"bot QA kind {row.Kind} has an invalid or a second row in QaKinds");
            }

            byKind[index] = row;
        }

        for (int i = 0; i < byKind.Length; i++)
        {
            if (byKind[i] is null) throw new InvalidOperationException($"bot QA kind {(QaSignalKind)i} has no row in QaKinds");
        }

        return byKind;
    }
}

using System.Globalization;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;

namespace ArcaneCore.World.Commands;

/// <summary>
/// <c>.server faults [top N | show ID | summary | reset]</c>: the server's fault ledger (<see cref="FaultLedger"/>) from the console or in game. Without arguments it
/// lists the ten most frequent faults; <c>show</c> prints one fault in full (its id may be abbreviated to four or more characters); <c>summary</c> (or <c>subsystems</c>)
/// groups recorded faults by canonical subsystem, then faults and anomalies together per subsystem (<see cref="DiagnosticOverview"/>), and lists invariant failures; <c>reset</c> clears the ledger and needs Administrator authority (the console has it).
/// Viewing needs GameMaster. The ledger is process-wide state read under its own locks, so the command is safe from the world thread. Output is plain lines (no colour
/// codes) so the console and the chat show it alike. Also formats the one-line summary <c>.server info</c> adds (<see cref="InfoLine"/>).
/// </summary>
public sealed class ServerFaultCommands : ICommandExtension
{
    /// <summary>Faults listed when no count is given.</summary>
    public const int DefaultTop = 10;

    /// <summary>Anomaly keys listed when no subsystem is given.</summary>
    public const int DefaultAnomalyTop = 15;

    /// <summary>The most faults one listing shows.</summary>
    public const int MaxTop = 50;

    /// <summary>The authority <c>reset</c> needs.</summary>
    public const AccountSecurity ResetSecurity = AccountSecurity.Administrator;

    public string Path => "server";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("faults", AccountSecurity.GameMaster,
            "Syntax: .server faults [top #count | show $id | summary | reset]\nList the exceptions the server caught, most frequent first; show one in full; summarize by subsystem and invariants; reset (administrator) clears the list.",
            Faults),
        new ChatCommand("anomalies", AccountSecurity.GameMaster,
            "Syntax: .server anomalies [$subsystem | show $key | reset]\nSummarize outcome anomalies by subsystem and top keys; list keys for one subsystem; show one key in full; reset (administrator) clears the ledger.",
            Anomalies),
    ];

    private static bool Faults(CommandContext context, string args)
        => Run(args, context.Security, FaultLedger.Shared, DateTimeOffset.UtcNow, context.Reply);

    private static bool Anomalies(CommandContext context, string args)
        => RunAnomalies(args, context.Security, AnomalyLedger.Shared, DateTimeOffset.UtcNow, context.Reply);

    /// <summary>The command's logic apart from the invoker (tests call it with their own ledger). False means a usage error.</summary>
    internal static bool Run(string args, AccountSecurity security, FaultLedger ledger, DateTimeOffset now, Action<string> reply, AnomalyLedger? anomalies = null)
    {
        string[] words = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            List(ledger, DefaultTop, now, reply);
            return true;
        }

        switch (words[0].ToLowerInvariant())
        {
            case "top":
            {
                int count = DefaultTop;
                if (words.Length > 2 || words.Length == 2 && !int.TryParse(words[1], NumberStyles.None, CultureInfo.InvariantCulture, out count))
                {
                    return false;
                }

                List(ledger, Math.Clamp(count, 1, MaxTop), now, reply);
                return true;
            }

            case "show":
                return words.Length == 2 && Show(ledger, words[1], now, reply);

            case "summary" or "subsystems":
                if (words.Length != 1)
                {
                    return false;
                }

                Summary(ledger, anomalies ?? AnomalyLedger.Shared, now, reply);
                return true;

            case "reset":
                if (words.Length != 1)
                {
                    return false;
                }

                if (security < ResetSecurity)
                {
                    reply("Only an administrator can reset the fault ledger.");
                    return true;
                }

                long total = ledger.Total;
                int distinct = ledger.Distinct;
                ledger.Reset();
                reply(string.Create(CultureInfo.InvariantCulture, $"Fault ledger cleared ({distinct} distinct, {total} total)."));
                return true;

            default:
                return false;
        }
    }

    /// <summary>The <c>.server anomalies</c> command's logic apart from the invoker (tests call it with their own ledger). False means a usage error.</summary>
    internal static bool RunAnomalies(string args, AccountSecurity security, AnomalyLedger ledger, DateTimeOffset now, Action<string> reply)
    {
        string[] words = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            AnomaliesOverview(ledger, DefaultAnomalyTop, now, reply);
            return true;
        }

        switch (words[0].ToLowerInvariant())
        {
            case "show":
                return words.Length == 2 && ShowAnomaly(ledger, words[1], now, reply);

            case "reset":
                if (words.Length != 1)
                {
                    return false;
                }

                if (security < ResetSecurity)
                {
                    reply("Only an administrator can reset the anomaly ledger.");
                    return true;
                }

                long total = ledger.Total;
                int distinct = ledger.Distinct;
                ledger.Reset();
                reply(string.Create(CultureInfo.InvariantCulture, $"Anomaly ledger cleared ({distinct} distinct, {total} total)."));
                return true;

            default:
                if (words.Length != 1)
                {
                    return false;
                }

                AnomaliesSubsystem(ledger, words[0], now, reply);
                return true;
        }
    }

    /// <summary>The <c>.server info</c> line: <c>faults: 12 (3 distinct, last 5 min ago)</c>.</summary>
    public static string InfoLine(FaultLedger ledger, DateTimeOffset now)
    {
        string last = ledger.LastSeen is { } at ? Age(now - at) + " ago" : "never";
        return string.Create(CultureInfo.InvariantCulture, $"faults: {ledger.Total} ({ledger.Distinct} distinct, last {last})");
    }

    /// <summary>The <c>.server info</c> line including both the fault and anomaly ledgers.</summary>
    public static string InfoLine(FaultLedger ledger, AnomalyLedger anomalies, DateTimeOffset now)
        => string.Create(CultureInfo.InvariantCulture, $"{InfoLine(ledger, now)}; {AnomaliesInfoLine(anomalies)}");

    /// <summary>The anomaly count summary: <c>anomalies: 42 (7 distinct)</c>.</summary>
    public static string AnomaliesInfoLine(AnomalyLedger ledger)
        => string.Create(CultureInfo.InvariantCulture, $"anomalies: {ledger.Total} ({ledger.Distinct} distinct)");

    /// <summary>The rates of both ledgers on one line for `.server info`: <c>rates per hour (last 15 min / last 1 h): faults 0.0/0.0, anomalies 8.0/6.0</c>.</summary>
    public static string RatesLine(FaultLedger faults, AnomalyLedger anomalies)
    {
        LedgerTiming f = faults.Timing();
        LedgerTiming a = anomalies.Timing();
        return string.Create(CultureInfo.InvariantCulture,
            $"rates per hour (last 15 min / last 1 h): faults {f.RatePerHour15Min:F1}/{f.RatePerHour1Hour:F1}, anomalies {a.RatePerHour15Min:F1}/{a.RatePerHour1Hour:F1}");
    }

    /// <summary>A ledger's first and last seen with its rates: <c>first 12:00:01 UTC (2 h ago), rate 4.0/h last 15 min, 3.0/h last 1 h</c>; <c>no events</c> when empty.</summary>
    internal static string TimingLine(LedgerTiming timing, DateTimeOffset now)
    {
        if (timing.FirstSeen is not { } first)
        {
            return "no events";
        }

        return string.Create(CultureInfo.InvariantCulture, $"first {first.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC ({Age(now - first)} ago), {timing.Describe()}");
    }

    private static string Rates(LedgerTiming timing)
        => string.Create(CultureInfo.InvariantCulture, $"{timing.RatePerHour15Min:F1}/h 15 min, {timing.RatePerHour1Hour:F1}/h 1 h");

    private static string CutText(string text, int max)
    {
        string line = text.ReplaceLineEndings(" ");
        return line.Length <= max ? line : line[..(max - 3)] + "...";
    }

    private static string JoinCut(IEnumerable<string> items, string separator, int max)
    {
        string joined = string.Join(separator, items);
        return CutText(joined, max);
    }

    /// <summary>The canonical subsystem of a fault site (<see cref="DiagnosticSubsystem.OfSite"/>), the same grouping the ledger's own summary uses.</summary>
    internal static string SubsystemOf(string site) => DiagnosticSubsystem.OfSite(site);

    private static void Summary(FaultLedger ledger, AnomalyLedger anomalies, DateTimeOffset now, Action<string> reply)
    {
        reply(InfoLine(ledger, now));
        IReadOnlyList<FaultSnapshot> all = ledger.Snapshot();
        if (all.Count == 0)
        {
            reply("Fault subsystems: 0 faults recorded.");
        }
        else
        {
            var groups = all
                .GroupBy(f => SubsystemOf(f.Site), StringComparer.Ordinal)
                .Select(g =>
                {
                    long total = g.Sum(f => f.Count);
                    int distinct = g.Count();
                    FaultSnapshot latest = g.OrderByDescending(f => f.LastSeen).ThenByDescending(f => f.Count).First();
                    return (Subsystem: g.Key, Total: total, Distinct: distinct, Latest: latest);
                })
                .OrderByDescending(g => g.Total)
                .ThenBy(g => g.Subsystem, StringComparer.Ordinal)
                .ToList();

            reply(string.Create(CultureInfo.InvariantCulture, $"Fault subsystems ({groups.Count}):"));
            foreach (var group in groups)
            {
                reply(string.Create(CultureInfo.InvariantCulture,
                    $"  {group.Subsystem}: {group.Total} total ({group.Distinct} distinct), latest {group.Latest.Id} at {group.Latest.Site} ({group.Latest.ExceptionType}, {Age(now - group.Latest.LastSeen)} ago)"));
            }
        }

        // The one view across both ledgers: each canonical subsystem with its faults and its anomalies.
        IReadOnlyList<DiagnosticSubsystemSummary> combined = DiagnosticOverview.BySubsystem(all, anomalies.Snapshot());
        if (combined.Count > 0)
        {
            reply(string.Create(CultureInfo.InvariantCulture, $"Faults and anomalies by subsystem ({combined.Count}):"));
            foreach (DiagnosticSubsystemSummary row in combined)
            {
                string anomalyText = row.Anomalies == 0
                    ? "0 anomalies"
                    : string.Create(CultureInfo.InvariantCulture, $"{row.Anomalies} anomalies ({row.AnomalyKeys} keys, worst {row.WorstAnomaly})");
                reply(string.Create(CultureInfo.InvariantCulture,
                    $"  {row.Subsystem}: {row.Faults} faults ({row.FaultSignatures} signatures), {anomalyText}, last {Age(now - row.LastSeen)} ago"));
            }
        }

        long invTotal = Invariant.FailureCount;
        var invPolicy = Invariant.Policy;
        IReadOnlyList<InvariantFailure> invFailures = Invariant.Failures();
        if (invTotal == 0 && invFailures.Count == 0)
        {
            reply(string.Create(CultureInfo.InvariantCulture, $"Invariants: 0 failures recorded (policy={invPolicy})."));
        }
        else
        {
            reply(string.Create(CultureInfo.InvariantCulture, $"Invariants: {invTotal} failure(s) recorded ({invFailures.Count} site(s), policy={invPolicy}):"));
            int shown = 0;
            foreach (InvariantFailure inv in invFailures)
            {
                if (shown++ == DefaultTop)
                {
                    break;
                }

                reply(string.Create(CultureInfo.InvariantCulture, $"  {inv.Member} ({inv.File}:{inv.Line}) count={inv.Count}: {inv.LastMessage}"));
            }

            if (invFailures.Count > DefaultTop)
            {
                reply(string.Create(CultureInfo.InvariantCulture, $"  ... {invFailures.Count - DefaultTop} more site(s); use .debug invariants for more."));
            }
        }
    }

    private static void List(FaultLedger ledger, int top, DateTimeOffset now, Action<string> reply)
    {
        reply(InfoLine(ledger, now));
        IReadOnlyList<FaultSnapshot> all = ledger.Snapshot();
        int shown = 0;
        foreach (FaultSnapshot fault in all)
        {
            if (shown++ == top)
            {
                break;
            }

            reply(string.Create(CultureInfo.InvariantCulture,
                $"{shown,2}. {fault.Id} x{fault.Count} {fault.Site} {fault.ExceptionType}, last {Age(now - fault.LastSeen)} ago, {Rates(fault.Timing)}"));
        }

        if (all.Count > top)
        {
            reply(string.Create(CultureInfo.InvariantCulture, $"... {all.Count - top} more; .server faults top {Math.Min(MaxTop, all.Count)} lists more."));
        }

        if (all.Count > 0)
        {
            reply("Use .server faults show <id> for the frames and the message.");
            reply("Ledger: " + TimingLine(ledger.Timing(), now));
        }
    }

    private static bool Show(FaultLedger ledger, string id, DateTimeOffset now, Action<string> reply)
    {
        string wanted = id.ToLowerInvariant();
        if (wanted.Length < 4 || !wanted.All(char.IsAsciiHexDigit))
        {
            return false;
        }

        FaultSnapshot[] matches = [.. ledger.Snapshot().Where(f => f.Id.StartsWith(wanted, StringComparison.Ordinal))];
        if (matches.Length == 0)
        {
            reply("No fault with that id.");
            return true;
        }

        if (matches.Length > 1)
        {
            reply("That id matches " + string.Join(", ", matches.Select(f => f.Id)) + "; give more characters.");
            return true;
        }

        FaultSnapshot fault = matches[0];
        reply(string.Create(CultureInfo.InvariantCulture, $"Fault {fault.Id}: {fault.ExceptionType} at {fault.Site}"));
        reply(string.Create(CultureInfo.InvariantCulture,
            $"Count {fault.Count}; first {fault.FirstSeen.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC, last {fault.LastSeen.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC ({Age(now - fault.LastSeen)} ago)"));
        reply("Rate: " + fault.Timing.Describe());
        if (fault.TopMessages.Count > 0)
        {
            reply("Top messages: " + JoinCut(fault.TopMessages.Select(m => string.Create(CultureInfo.InvariantCulture, $"{CutText(m.Subject, 70)} x{m.Count}")), "; ", 230));
        }

        if (fault.RecentMessages.Count > 0)
        {
            reply("Recent distinct messages: " + JoinCut(fault.RecentMessages.Select(m => CutText(m, 70)), " | ", 230));
        }

        reply("Message: " + (fault.SampleMessage.Length == 0 ? "(none)" : fault.SampleMessage.ReplaceLineEndings(" ")));
        if (!string.IsNullOrEmpty(fault.SampleStack))
        {
            reply("Stack: " + fault.SampleStack);
        }

        if (!string.IsNullOrEmpty(fault.SampleChain) && fault.SampleChain.Contains(" <- ", StringComparison.Ordinal))
        {
            reply("Chain: " + fault.SampleChain);
        }

        if (fault.Frames.Count == 0)
        {
            reply("Frames: (none inside ArcaneCore)");
        }

        foreach (string frame in fault.Frames)
        {
            reply("  at " + frame);
        }

        return true;
    }

    private static void AnomaliesOverview(AnomalyLedger ledger, int top, DateTimeOffset now, Action<string> reply)
    {
        reply(AnomaliesInfoLine(ledger));
        IReadOnlyDictionary<string, (int Codes, long Total)> bySubsystem = ledger.BySubsystem();
        if (bySubsystem.Count == 0)
        {
            reply("Anomaly subsystems: 0 anomalies recorded.");
            return;
        }

        reply(string.Create(CultureInfo.InvariantCulture, $"Anomaly subsystems ({bySubsystem.Count}):"));
        foreach (KeyValuePair<string, (int Codes, long Total)> pair in bySubsystem.OrderByDescending(p => p.Value.Total).ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            reply(string.Create(CultureInfo.InvariantCulture, $"  {pair.Key}: {pair.Value.Total} total ({pair.Value.Codes} distinct)"));
        }

        IReadOnlyList<AnomalySnapshot> all = ledger.Snapshot();
        int shown = 0;
        foreach (AnomalySnapshot snap in all)
        {
            if (shown++ == top)
            {
                break;
            }

            string subject = snap.LastSubject.Length == 0 ? "(none)" : snap.LastSubject;
            reply(string.Create(CultureInfo.InvariantCulture,
                $"{shown,2}. {snap.Key} x{snap.Count} ({snap.SubjectsCountText} subjects, {snap.Severity}), last {Age(now - snap.LastSeen)} ago, {Rates(snap.Timing)} [{subject}]"));
        }

        if (all.Count > top)
        {
            reply(string.Create(CultureInfo.InvariantCulture, $"... {all.Count - top} more; use .server anomalies <subsystem> or .server anomalies show <key>."));
        }

        reply("Ledger: " + TimingLine(ledger.Timing(), now));
    }

    private static void AnomaliesSubsystem(AnomalyLedger ledger, string subsystem, DateTimeOffset now, Action<string> reply)
    {
        string normalized = AnomalyLedger.Normalise(subsystem);
        IReadOnlyList<AnomalySnapshot> snapshots = ledger.Snapshot(normalized);
        if (snapshots.Count == 0)
        {
            reply(string.Create(CultureInfo.InvariantCulture, $"No anomalies recorded for subsystem '{normalized}'."));
            return;
        }

        long total = snapshots.Sum(s => s.Count);
        reply(string.Create(CultureInfo.InvariantCulture, $"Subsystem '{normalized}': {total} total ({snapshots.Count} distinct):"));
        int index = 0;
        foreach (AnomalySnapshot snap in snapshots)
        {
            index++;
            string subject = snap.LastSubject.Length == 0 ? "(none)" : snap.LastSubject;
            reply(string.Create(CultureInfo.InvariantCulture,
                $"{index,2}. {snap.Key} x{snap.Count} ({snap.SubjectsCountText} subjects, {snap.Severity}), last {Age(now - snap.LastSeen)} ago, {Rates(snap.Timing)} [{subject}]"));
        }
    }

    private static bool ShowAnomaly(AnomalyLedger ledger, string key, DateTimeOffset now, Action<string> reply)
    {
        if (string.IsNullOrWhiteSpace(key) || !key.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        AnomalySnapshot? snap = ledger.Find(key);
        if (snap is null)
        {
            reply("No anomaly recorded for key '" + key + "'.");
            return true;
        }

        reply(string.Create(CultureInfo.InvariantCulture, $"Anomaly {snap.Key} (subsystem {snap.Subsystem}, code {snap.Code}, severity {snap.Severity})"));
        reply(string.Create(CultureInfo.InvariantCulture,
            $"Count {snap.Count} ({snap.SubjectsCountText} distinct subjects); first {snap.FirstSeen.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC, last {snap.LastSeen.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC ({Age(now - snap.LastSeen)} ago)"));
        reply("Subject: " + (snap.LastSubject.Length == 0 ? "(none)" : snap.LastSubject.ReplaceLineEndings(" ")));
        reply("Detail: " + (snap.LastDetail.Length == 0 ? "(none)" : snap.LastDetail.ReplaceLineEndings(" ")));
        reply("Rate: " + snap.Timing.Describe());
        if (snap.TopSubjects.Count > 0)
        {
            reply("Top subjects: " + JoinCut(snap.TopSubjects.Select(s => string.Create(CultureInfo.InvariantCulture, $"{CutText(s.Subject, 60)} x{s.Count}")), "; ", 230));
        }

        if (snap.RecentDetails.Count > 0)
        {
            reply("Recent distinct details: " + JoinCut(snap.RecentDetails.Select(d => CutText(d, 70)), " | ", 230));
        }

        return true;
    }

    private static string Age(TimeSpan span)
        => GmDuration.SecsToTimeString((long)Math.Max(0, span.TotalSeconds), shortText: true).Trim();
}

using System.Globalization;
using System.Security.Cryptography;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>What the publisher needs of the findings: a current view of one, and a way to record what it did.</summary>
internal interface IQaPublicationState
{
    QaFindingView? Get(string id);

    void Apply(string id, QaPublicationUpdate update);
}

/// <summary>The aggregator's persisted state: the findings and, per finding, the salted hashes of the bots that met it.</summary>
internal sealed record QaAggregatorState(IReadOnlyList<QaFindingView> Findings, IReadOnlyDictionary<string, ulong[]> Bots);

/// <summary>
/// Folds signals into findings. One lock guards everything (microseconds per signal); every read returns immutable views.
/// Bots are counted by a salted SHA-256 of their id (the salt is persisted, so counts survive a restart), at most
/// <see cref="QaFinding.MaxTrackedBots"/> per finding: beyond that the count saturates.
/// A finding's id is the hash of the v1 key of its FIRST signal and never changes (it is the marker of its GitHub issue); which finding a
/// stall or death loop belongs to is decided by a coarser identity, so one real problem is one finding wherever in its area it was met:
/// a Quest-goal stall is its quest and target, a Combat-goal stall its target, any other stall its goal in a <see cref="PlayerbotQaFingerprint.ZoneYards"/>
/// zone cell (a stall just across a cell border still folds in when it lies within <see cref="AnchorYards"/> of the finding's anchor, the first
/// sample's position, which never moves); a death loop is a map and zone cell (or anchor radius) plus its attackers: a loop that shares an attacker entry
/// with a finding of the area is that finding, a loop that bridges several findings merges them, and with no attacker on either side the death source decides.
/// At the finding cap the lowest-value unpublished finding is evicted (a Count 1 finding of one bot first, then lowest severity, then oldest);
/// a published or known-closed finding is never evicted (a new finding is refused when nothing can be). A finding not seen for
/// <c>staleAfter</c> and not published is dropped by <see cref="AgeOut"/> and on load. Loading an older store merges the findings that are one
/// problem now, keeping the published one's id and publication state.
/// Thread-safe.
/// </summary>
internal sealed class PlayerbotQaAggregator(int maxFindings, int maxSamples, byte[]? salt = null, TimeSpan? staleAfter = null) : IQaPublicationState
{
    /// <summary>How far from a finding's anchor (its first sample, in yards, measured on the ground plane) a signal still folds into it across a zone cell border.</summary>
    internal const float AnchorYards = 160f;

    private readonly object _gate = new();
    private readonly Dictionary<string, QaFinding> _byId = new(StringComparer.Ordinal);
    private readonly byte[] _salt = salt is { Length: > 0 } ? salt : RandomNumberGenerator.GetBytes(16);
    private IReadOnlyList<QaFindingView>? _snapshot;
    private long _rejected;

    /// <summary>Findings the last <see cref="Restore"/> or <see cref="AgeOut"/> dropped for not being seen within the age limit (running total).</summary>
    internal long AgedOut { get; private set; }

    /// <summary>Findings folded into another one by <see cref="Restore"/> or a bridging signal (running total).</summary>
    internal long Merged { get; private set; }

    /// <summary>The salt for the persisted bot hashes.</summary>
    internal byte[] Salt => _salt;

    /// <summary>New findings refused because the cap was reached and every finding is published.</summary>
    internal long Rejected => Interlocked.Read(ref _rejected);

    internal int Count
    {
        get { lock (_gate) return _byId.Count; }
    }

    /// <summary>Fold one signal in; the finding's id, or null when it was refused.</summary>
    internal string? Ingest(QaSignal signal, IQaContentFacts facts, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(facts);
        string key = PlayerbotQaFingerprint.Key(signal);
        string id = PlayerbotQaFingerprint.Hash(key);
        QaClassification cause = PlayerbotQaClassifier.Classify(signal, facts); // content reads stay outside the lock
        ulong? bot = signal.BotKey is { } guid ? BotHash(guid) : null;
        Probe? probe = signal.FromCoverage ? null : ProbeOf(signal);
        lock (_gate)
        {
            QaFinding? finding;
            if (probe is { } wanted)
            {
                finding = Resolve(wanted);
            }
            else
            {
                _byId.TryGetValue(id, out finding);
            }

            if (finding is null)
            {
                if (_byId.ContainsKey(id))
                {
                    // The v1 key of this signal names a finding that is another death loop (other attackers): this loop gets a variant key of its own.
                    key = VariantKey(key, signal);
                    id = PlayerbotQaFingerprint.Hash(key);
                    for (int n = 2; _byId.ContainsKey(id); n++)
                    {
                        key = VariantKey(PlayerbotQaFingerprint.Key(signal), signal) + "|n=" + n.ToString(CultureInfo.InvariantCulture);
                        id = PlayerbotQaFingerprint.Hash(key);
                    }
                }

                if (_byId.Count >= maxFindings && !EvictOne(signal.FromCoverage))
                {
                    Interlocked.Increment(ref _rejected);
                    return null;
                }

                finding = new QaFinding { Id = id, Key = key, Kind = signal.Kind, FirstSeenUtc = signal.AtUtc, LastSeenUtc = signal.AtUtc };
                if (probe is { Kind: QaSignalKind.Stall } stall)
                {
                    finding.Group = stall.Group;
                    finding.GroupBase = stall.Base;
                }

                _byId[id] = finding;
            }

            if (signal.FromCoverage)
            {
                // A static fact (the start-up spell coverage): it marks the finding, adds no occurrence, sample or bot.
                finding.Coverage = true;
            }
            else
            {
                finding.Count += Math.Max(1, signal.Occurrences);
                if (signal.AtUtc > finding.LastSeenUtc) finding.LastSeenUtc = signal.AtUtc;
                if (finding.FirstSeenUtc > signal.AtUtc) finding.FirstSeenUtc = signal.AtUtc;
                if (bot is { } hash)
                {
                    if (finding.Bots.Count < QaFinding.MaxTrackedBots) finding.Bots.Add(hash);
                }
                else if (signal.BotCount > finding.AnonymousBots)
                {
                    finding.AnonymousBots = signal.BotCount;
                }

                if (signal.BotClass is > 0 and < 32) finding.ClassMask |= 1u << signal.BotClass;
                if (finding.Anchor is null && signal.HasPosition) finding.Anchor = new QaAnchor(signal.Map, signal.X, signal.Y);
                if (signal.Kind == QaSignalKind.DeathLoop)
                {
                    // The loop's attackers are the union of its signals' (a later signal that shares one is this loop).
                    foreach (uint attacker in signal.Attackers)
                    {
                        if (attacker != 0 && finding.Attackers.Count < QaFinding.MaxAttackers) finding.Attackers.Add(attacker);
                    }

                    if (finding.DeathSource.Length == 0) finding.DeathSource = SourceOf(signal);
                }

                finding.Samples.Add(new QaSample(signal.AtUtc, signal.HasPosition, signal.Map, signal.X, signal.Y, signal.Z, [.. signal.Attackers], signal.Message)
                    { Bot = signal.Bot });
                while (finding.Samples.Count > maxSamples) finding.Samples.RemoveAt(0);
            }

            CopyIds(finding, signal);
            // A signal folded in from another cell (its key is not the finding's) keeps what the finding's first signal said; only an empty field is filled.
            bool own = string.Equals(finding.Key, key, StringComparison.Ordinal);
            if (own ? signal.Quest != 0 || signal.Kind == QaSignalKind.DeathLoop : finding.Quest == 0) finding.Quest = signal.Quest;
            if (signal.Target != 0 && (own || finding.Target == 0)) finding.Target = signal.Target;
            if (signal.HasPosition) finding.Map = signal.Map;
            if (own)
            {
                finding.QuestTitle = signal.Kind == QaSignalKind.DeathLoop ? cause.QuestTitle : cause.QuestTitle ?? finding.QuestTitle;
                finding.Cause = cause.Cause;
                finding.CauseDetail = cause.Detail;
                finding.Goal = signal.Goal;
                // A death loop keeps the subject of its first signal: its attackers vary from one signal to the next.
                if (signal.Kind != QaSignalKind.DeathLoop || finding.Subject.Length == 0) finding.Subject = Subject(signal, finding.QuestTitle);
            }
            else
            {
                finding.QuestTitle ??= cause.QuestTitle;
                if (finding.Cause == QaCause.Unknown && finding.CauseDetail.Length == 0)
                {
                    finding.Cause = cause.Cause;
                    finding.CauseDetail = cause.Detail;
                }

                if (finding.Goal.Length == 0) finding.Goal = signal.Goal;
                if (finding.Subject.Length == 0) finding.Subject = Subject(signal, finding.QuestTitle);
            }

            finding.Severity = PlayerbotQaClassifier.Severity(finding.Kind, finding.Count, finding.DistinctBots, finding.LastSeenUtc, now);
            _snapshot = null;
            return finding.Id;
        }
    }

    /// <summary>What decides which finding a stall or death loop is: a signal's, or a finding's own (kept for it).</summary>
    private readonly record struct Probe(QaSignalKind Kind, string Group, string? Base, bool HasPosition, uint Map, float X, float Y, uint[] Attackers, string Source);

    /// <summary>The probe of a signal; null for a kind that is identified by its key alone.</summary>
    private static Probe? ProbeOf(QaSignal signal)
    {
        switch (signal.Kind)
        {
            case QaSignalKind.Stall:
                (string group, string? baseKey) = PlayerbotQaFingerprint.StallGroup(signal);
                return new Probe(QaSignalKind.Stall, group, baseKey, signal.HasPosition, signal.Map, signal.X, signal.Y, [], string.Empty);
            case QaSignalKind.DeathLoop:
                return new Probe(QaSignalKind.DeathLoop, string.Empty, null, signal.HasPosition, signal.Map, signal.X, signal.Y,
                    [.. signal.Attackers.Where(a => a != 0).Distinct()], SourceOf(signal));
            default:
                return null;
        }
    }

    /// <summary>The probe of a finding already held (its anchor is where it began).</summary>
    private static Probe? ProbeOf(QaFinding f)
    {
        QaAnchor? anchor = f.Anchor;
        bool has = anchor is not null;
        return f.Kind switch
        {
            QaSignalKind.Stall when f.Group.Length > 0 => new Probe(QaSignalKind.Stall, f.Group, f.GroupBase, has, anchor?.Map ?? 0, anchor?.X ?? 0, anchor?.Y ?? 0, [], string.Empty),
            QaSignalKind.DeathLoop => new Probe(QaSignalKind.DeathLoop, string.Empty, null, has, anchor?.Map ?? 0, anchor?.X ?? 0, anchor?.Y ?? 0, [.. f.Attackers], f.DeathSource),
            _ => null,
        };
    }

    /// <summary>The finding a probe belongs to: the one finding, a merge of the several it bridges, or null. Call under the lock.</summary>
    private QaFinding? Resolve(Probe probe)
    {
        List<QaFinding> matches = Matches(probe);
        if (matches.Count == 0) return null;
        if (matches.Count == 1) return matches[0];
        return MergeAll(matches);
    }

    /// <summary>
    /// The findings a probe is the same problem as. A stall: the finding of its group, else the nearest of its group base whose anchor lies within
    /// <see cref="AnchorYards"/> (one finding at most). A death loop: every loop finding of its area (same zone cell, or anchor within
    /// <see cref="AnchorYards"/>) that shares an attacker entry, or - with no attacker on either side - the same death source.
    /// </summary>
    private List<QaFinding> Matches(Probe probe)
    {
        var matches = new List<QaFinding>();
        QaFinding? nearest = null;
        double nearestDistance = double.MaxValue;
        foreach (QaFinding f in _byId.Values)
        {
            if (f.Kind != probe.Kind) continue;
            if (probe.Kind == QaSignalKind.Stall)
            {
                if (string.Equals(f.Group, probe.Group, StringComparison.Ordinal)) return [f];
                if (probe.Base is null || !string.Equals(f.GroupBase, probe.Base, StringComparison.Ordinal)) continue;
                if (f.Anchor is { } a && probe.HasPosition && a.Map == probe.Map)
                {
                    double d = Distance2(a.X, a.Y, probe.X, probe.Y);
                    if (d <= (double)AnchorYards * AnchorYards && d < nearestDistance)
                    {
                        nearest = f;
                        nearestDistance = d;
                    }
                }
            }
            else if (SameArea(f.Anchor, probe) && SameLoop(f.Attackers, f.DeathSource, probe))
            {
                matches.Add(f);
            }
        }

        if (nearest is not null) matches.Add(nearest);
        return matches;
    }

    private static double Distance2(float ax, float ay, float bx, float by)
    {
        double dx = (double)ax - bx, dy = (double)ay - by;
        return dx * dx + dy * dy;
    }

    /// <summary>Whether a finding's anchor is in the probe's area: the same map and zone cell, or within <see cref="AnchorYards"/>; no place matches no place.</summary>
    private static bool SameArea(QaAnchor? anchor, Probe probe)
    {
        if (anchor is not { } a || !probe.HasPosition) return anchor is null && !probe.HasPosition;
        if (a.Map != probe.Map) return false;
        return (PlayerbotQaFingerprint.ZoneCell(a.X) == PlayerbotQaFingerprint.ZoneCell(probe.X) && PlayerbotQaFingerprint.ZoneCell(a.Y) == PlayerbotQaFingerprint.ZoneCell(probe.Y))
            || Distance2(a.X, a.Y, probe.X, probe.Y) <= (double)AnchorYards * AnchorYards;
    }

    /// <summary>A loop is the finding's when they share an attacker entry; with none on either side, when they share the death source.</summary>
    private static bool SameLoop(IReadOnlyCollection<uint> attackers, string source, Probe probe)
    {
        if (probe.Attackers.Length == 0) return attackers.Count == 0 && string.Equals(source, probe.Source, StringComparison.Ordinal);
        foreach (uint attacker in probe.Attackers)
        {
            foreach (uint own in attackers)
            {
                if (own == attacker) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Fold several findings that turned out to be one problem into the best of them: a published or known-closed one (the lowest issue number),
    /// else the busiest. The survivor keeps its id, key and publication state; the others go. Call under the lock.
    /// </summary>
    private QaFinding MergeAll(List<QaFinding> findings)
    {
        QaFinding survivor = findings
            .OrderByDescending(static f => f.Publication.Pinned)
            .ThenBy(static f => f.Publication.Pinned ? f.Publication.Issue : 0)
            .ThenByDescending(static f => f.Count)
            .ThenBy(static f => f.Id, StringComparer.Ordinal)
            .First();
        foreach (QaFinding other in findings)
        {
            if (ReferenceEquals(other, survivor)) continue;
            Absorb(survivor, other);
            if (_byId.TryGetValue(other.Id, out QaFinding? held) && ReferenceEquals(held, other)) _byId.Remove(other.Id);
            Merged++;
        }

        _snapshot = null;
        return survivor;
    }

    private void Absorb(QaFinding into, QaFinding from)
    {
        into.Count += from.Count;
        if (from.FirstSeenUtc < into.FirstSeenUtc) into.FirstSeenUtc = from.FirstSeenUtc;
        if (from.LastSeenUtc > into.LastSeenUtc) into.LastSeenUtc = from.LastSeenUtc;
        foreach (ulong hash in from.Bots)
        {
            if (into.Bots.Count < QaFinding.MaxTrackedBots) into.Bots.Add(hash);
        }

        into.AnonymousBots = Math.Max(into.AnonymousBots, from.AnonymousBots);
        into.ClassMask |= from.ClassMask;
        into.Coverage |= from.Coverage;
        foreach (uint attacker in from.Attackers)
        {
            if (into.Attackers.Count < QaFinding.MaxAttackers) into.Attackers.Add(attacker);
        }

        if (into.DeathSource.Length == 0) into.DeathSource = from.DeathSource;
        into.Anchor ??= from.Anchor;
        if (into.Quest == 0) into.Quest = from.Quest;
        if (into.Target == 0) into.Target = from.Target;
        into.QuestTitle ??= from.QuestTitle;
        if (into.Cause == QaCause.Unknown && into.CauseDetail.Length == 0)
        {
            into.Cause = from.Cause;
            into.CauseDetail = from.CauseDetail;
        }

        if (!into.Publication.Pinned) into.OccurrencesReported = Math.Max(into.OccurrencesReported, from.OccurrencesReported);
        if (from.LastCommentUtc is { } commented && (into.LastCommentUtc is null || commented > into.LastCommentUtc)) into.LastCommentUtc = commented;
        if (into.Publication.State is QaPublicationState.Pending && from.Publication.State is not QaPublicationState.Pending) into.Publication = from.Publication;
        List<QaSample> samples = [.. into.Samples, .. from.Samples];
        samples.Sort(static (x, y) => x.AtUtc.CompareTo(y.AtUtc));
        into.Samples.Clear();
        into.Samples.AddRange(samples.Skip(Math.Max(0, samples.Count - maxSamples)));
        into.Severity = Math.Max(into.Severity, from.Severity);
    }

    private static string SourceOf(QaSignal signal) => signal.Bot?.Source is { Length: > 0 } source ? source : "Unobserved";

    /// <summary>The key of a death loop beside another loop in the same cell: the v1 key plus its attackers (sorted, distinct), or its death source.</summary>
    private static string VariantKey(string key, QaSignal signal)
    {
        uint[] attackers = [.. signal.Attackers.Where(a => a != 0).Distinct().Order().Take(QaFinding.MaxAttackers)];
        return attackers.Length > 0
            ? key + "|attackers=" + string.Join('/', attackers)
            : key + "|source=" + SourceOf(signal).Replace('|', '/');
    }

    /// <summary>Immutable views of every finding (unordered; <see cref="PlayerbotQaReport.Rank"/> orders them).</summary>
    internal IReadOnlyList<QaFindingView> Snapshot()
    {
        lock (_gate)
        {
            return _snapshot ??= [.. _byId.Values.Select(f => f.ToView())];
        }
    }

    public QaFindingView? Get(string id)
    {
        lock (_gate) return _byId.TryGetValue(id, out QaFinding? finding) ? finding.ToView() : null;
    }

    public void Apply(string id, QaPublicationUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out QaFinding? finding)) return;
            finding.Publication = update.Publication;
            if (update.LastCommentUtc is { } at) finding.LastCommentUtc = at;
            if (update.OccurrencesReported is { } reported) finding.OccurrencesReported = reported;
            _snapshot = null;
        }
    }

    /// <summary>Forget one finding (<paramref name="id"/>, exact) or all of them (null); how many went.</summary>
    internal int Reset(string? id)
    {
        lock (_gate)
        {
            int removed;
            if (id is null)
            {
                removed = _byId.Count;
                _byId.Clear();
            }
            else
            {
                removed = _byId.Remove(id) ? 1 : 0;
            }

            if (removed > 0) _snapshot = null;
            return removed;
        }
    }

    /// <summary>The state to persist.</summary>
    internal QaAggregatorState Export()
    {
        lock (_gate)
        {
            return new QaAggregatorState([.. _byId.Values.Select(f => f.ToView())],
                _byId.Values.Where(f => f.Bots.Count > 0).ToDictionary(f => f.Id, f => f.Bots.Order().ToArray(), StringComparer.Ordinal));
        }
    }

    /// <summary>
    /// Load persisted findings (start-up, before signals flow). A finding whose id is not the hash of its key is dropped (a damaged or edited file
    /// must not plant a marker); a finding not seen within the age limit and not published or known-closed is dropped (<see cref="AgedOut"/>);
    /// findings that are one problem under the current identity are merged into one (<see cref="Merged"/>: the published one survives with its
    /// id and publication state); the published ones, then the highest-severity ones, are kept when the file holds more than the cap. The number dropped as invalid or over the cap.
    /// </summary>
    internal int Restore(QaAggregatorState state, DateTime? now = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        int dropped = 0;
        DateTime at = now ?? DateTime.UtcNow;
        lock (_gate)
        {
            _byId.Clear();
            foreach (QaFindingView view in state.Findings
                .OrderByDescending(v => (v.Publication ?? QaPublication.Pending).Pinned).ThenByDescending(v => v.Severity).ThenBy(v => v.Id, StringComparer.Ordinal))
            {
                if (view.Id is not { Length: PlayerbotQaFingerprint.FullChars } || view.Key is null
                    || !string.Equals(PlayerbotQaFingerprint.Hash(view.Key), view.Id, StringComparison.Ordinal)
                    || !Enum.IsDefined(view.Kind) || _byId.ContainsKey(view.Id))
                {
                    dropped++;
                    continue;
                }

                if (IsStale(view.Publication ?? QaPublication.Pending, view.Coverage, view.LastSeenUtc, at))
                {
                    AgedOut++;
                    continue;
                }

                // A stall's goal and subject come from its key (files before 2026-10-11 have no goal, and titled a stale quest as the subject);
                // only a key that rebuilds to itself is trusted, otherwise the saved text stays. The key and so the id never change.
                QaSignal? stall = view.Kind == QaSignalKind.Stall ? PlayerbotQaFingerprint.StallOfKey(view.Key) : null;
                var finding = new QaFinding
                {
                    Id = view.Id, Key = view.Key, Kind = view.Kind, Count = Math.Max(0, view.Count),
                    Subject = stall is not null ? Subject(stall, view.QuestTitle) : view.Subject ?? string.Empty,
                    Goal = stall?.Goal ?? (string.IsNullOrEmpty(view.Goal) ? string.Empty : view.Goal),
                    FirstSeenUtc = view.FirstSeenUtc, LastSeenUtc = view.LastSeenUtc, Quest = view.Quest, Target = view.Target, Map = view.Map,
                    QuestTitle = view.QuestTitle, Cause = view.Cause, CauseDetail = view.CauseDetail ?? string.Empty, Severity = view.Severity,
                    Publication = view.Publication ?? QaPublication.Pending, LastCommentUtc = view.LastCommentUtc,
                    OccurrencesReported = Math.Max(0, view.OccurrencesReported), Spell = view.Spell, SpellName = view.SpellName ?? string.Empty,
                    SpellRank = view.SpellRank ?? string.Empty, EffectIndex = view.EffectIndex, EffectName = view.EffectName ?? string.Empty,
                    EffectValue = view.EffectValue, Aura = view.Aura, Reason = view.Reason ?? string.Empty, ReasonDetail = view.ReasonDetail ?? string.Empty,
                    Item = view.Item, Npc = view.Npc, GameObject = view.GameObject, ClassMask = view.ClassMask, Coverage = view.Coverage,
                    // Files before 2026-10-12 have no anchor: the first saved sample with a position, else the centre of the key's cell.
                    Anchor = view.Anchor ?? (view.Samples ?? []).Where(sm => sm.HasPosition).Select(sm => new QaAnchor(sm.Map, sm.X, sm.Y)).FirstOrDefault()
                        ?? PlayerbotQaFingerprint.CellCentreOfKey(view.Key),
                };
                if (view.Kind == QaSignalKind.DeathLoop) RestoreLoop(finding, view);
                if (state.Bots.TryGetValue(view.Id, out ulong[]? bots)) finding.Bots.UnionWith(bots.Take(QaFinding.MaxTrackedBots));
                if (finding.Bots.Count < view.DistinctBots) finding.AnonymousBots = view.DistinctBots;
                foreach (QaSample sample in (view.Samples ?? []).TakeLast(maxSamples)) finding.Samples.Add(sample);
                if (stall is not null)
                {
                    // The group is computed where the finding began (its anchor), exactly as the signal that opened it would have.
                    QaSignal placed = finding.Anchor is { } anchor ? stall with { HasPosition = true, Map = anchor.Map, X = anchor.X, Y = anchor.Y } : stall;
                    (finding.Group, finding.GroupBase) = PlayerbotQaFingerprint.StallGroup(placed);
                }

                if (ProbeOf(finding) is { } probe)
                {
                    List<QaFinding> same = Matches(probe);
                    if (same.Count > 0)
                    {
                        same.Add(finding);
                        finding = MergeAll(same);
                        _byId[finding.Id] = finding;
                        continue;
                    }
                }

                if (_byId.Count >= maxFindings)
                {
                    dropped++;
                    continue;
                }

                _byId[finding.Id] = finding;
            }

            _snapshot = null;
        }

        return dropped;
    }

    /// <summary>
    /// A death loop's identity (its first signal's attackers and death source): as saved; for an older file without one, the union of
    /// the saved samples' attackers (that finding already stood for every loop in its cell), else the subject's <c>attackers 1/2</c>.
    /// </summary>
    private static void RestoreLoop(QaFinding finding, QaFindingView view)
    {
        IEnumerable<uint> attackers = view.Attackers ?? [];
        // No saved identity means a file from before anchors, where one finding per cell merged every death loop seen
        // there; its samples are the last few signals, not the first. The union of their attackers keeps those loops
        // matching the existing finding (and its issue) instead of opening variants.
        if (!attackers.Any()) attackers = (view.Samples ?? []).SelectMany(sm => sm.Attackers ?? []).Distinct().ToList();
        if (!attackers.Any() && view.Subject is { } subject && subject.LastIndexOf("attackers ", StringComparison.Ordinal) is var at and >= 0)
        {
            attackers = subject[(at + "attackers ".Length)..].Split('/').Select(t => uint.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out uint a) ? a : 0u);
        }

        foreach (uint attacker in attackers)
        {
            if (attacker != 0 && finding.Attackers.Count < QaFinding.MaxAttackers) finding.Attackers.Add(attacker);
        }

        finding.DeathSource = !string.IsNullOrEmpty(view.DeathSource) ? view.DeathSource
            : (view.Samples ?? []).Select(sm => sm.Bot?.Source).FirstOrDefault(src => !string.IsNullOrEmpty(src)) ?? "Unobserved";
    }

    /// <summary>The optional ids of a direct signal; a field the signal leaves empty keeps what the finding had.</summary>
    private static void CopyIds(QaFinding finding, QaSignal signal)
    {
        if (signal.Spell != 0) finding.Spell = signal.Spell;
        if (signal.SpellName.Length != 0) finding.SpellName = signal.SpellName;
        if (signal.SpellRank.Length != 0) finding.SpellRank = signal.SpellRank;
        if (signal.EffectIndex >= 0)
        {
            finding.EffectIndex = signal.EffectIndex;
            finding.EffectValue = signal.EffectValue;
        }

        if (signal.EffectName.Length != 0) finding.EffectName = signal.EffectName;
        if (signal.Aura != 0) finding.Aura = signal.Aura;
        if (signal.Reason.Length != 0) finding.Reason = signal.Reason;
        if ((QaKinds.Of(signal.Kind).SpellKind || signal.Kind == QaSignalKind.ServerFault) && signal.Detail.Length != 0) finding.ReasonDetail = signal.Detail;
        if (signal.Item != 0) finding.Item = signal.Item;
        if (signal.Npc != 0) finding.Npc = signal.Npc;
        if (signal.GameObject != 0) finding.GameObject = signal.GameObject;
    }

    /// <summary>
    /// Make room for one finding. A finding no bot met (Count 0: only the start-up spell coverage lists it, and the next start lists it again) goes
    /// first; a coverage signal may only evict such a finding, so the coverage never pushes out a finding of real occurrences.
    /// </summary>
    private bool EvictOne(bool coverageOnly)
    {
        QaFinding? victim = Victim(static f => f.Count == 0);
        if (victim is null && !coverageOnly) victim = Victim(static _ => true);
        return victim is not null && _byId.Remove(victim.Id);
    }

    /// <summary>
    /// The cheapest finding to lose among the eligible unpublished ones: one that is a single occurrence of a single bot first (noise), then the
    /// lowest severity, then the one not seen for longest. A published or known-closed finding is never a victim.
    /// </summary>
    private QaFinding? Victim(Func<QaFinding, bool> eligible)
    {
        QaFinding? victim = null;
        foreach (QaFinding f in _byId.Values)
        {
            if (f.Publication.Pinned || !eligible(f)) continue;
            if (victim is null || Cheaper(f, victim)) victim = f;
        }

        return victim;
    }

    private static bool OneOff(QaFinding f) => f.Count <= 1 && f.DistinctBots <= 1;

    private static bool Cheaper(QaFinding f, QaFinding than)
    {
        if (OneOff(f) != OneOff(than)) return OneOff(f);
        if (f.Severity != than.Severity) return f.Severity < than.Severity;
        return f.LastSeenUtc < than.LastSeenUtc;
    }

    private bool IsStale(QaPublication publication, bool coverage, DateTime lastSeenUtc, DateTime now)
        => staleAfter is { } limit && !publication.Pinned && !coverage && now - lastSeenUtc > limit;

    /// <summary>
    /// Drop every finding not seen within the age limit that is not published or known-closed (and not a start-up coverage finding, which a
    /// running server lists only once); how many went. Does nothing without an age limit.
    /// </summary>
    internal int AgeOut(DateTime now)
    {
        lock (_gate)
        {
            string[] stale = [.. _byId.Values.Where(f => IsStale(f.Publication, f.Coverage, f.LastSeenUtc, now)).Select(f => f.Id)];
            foreach (string id in stale) _byId.Remove(id);
            if (stale.Length > 0)
            {
                AgedOut += stale.Length;
                _snapshot = null;
            }

            return stale.Length;
        }
    }

    private ulong BotHash(Guid bot)
    {
        Span<byte> input = stackalloc byte[16 + 16];
        _salt.AsSpan(0, Math.Min(16, _salt.Length)).CopyTo(input);
        bot.TryWriteBytes(input[16..]);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        return BitConverter.ToUInt64(hash[..8]);
    }

    /// <summary>The finding's subject in words (the issue title after the kind).</summary>
    internal static string Subject(QaSignal s, string? questTitle)
    {
        string quest = s.Quest != 0 ? string.Create(CultureInfo.InvariantCulture, $"quest {s.Quest}") + (questTitle is null ? string.Empty : $" ({questTitle})") : string.Empty;
        string near = s.HasPosition
            ? string.Create(CultureInfo.InvariantCulture, $" map {s.Map} near {PlayerbotQaFingerprint.Cell(s.X) * PlayerbotQaFingerprint.CellYards},{PlayerbotQaFingerprint.Cell(s.Y) * PlayerbotQaFingerprint.CellYards}")
            : string.Empty;
        return s.Kind switch
        {
            // A stall names its goal first. Its quest= is the last quest the brain remembers, which is the subject only of a Quest goal: for any
            // other goal it is stale (GitHub #154: goal=Explore quest=109 was titled "quest 109 (Report to Gryan Stoutmantle)").
            QaSignalKind.Stall => $"goal {s.Goal}" + (s.Quest != 0 && QaFindingView.QuestIsGoalOf(s.Kind, s.Goal) ? ", " + quest : string.Empty)
                + (s.Target != 0 ? string.Create(CultureInfo.InvariantCulture, $" target {s.Target}") : string.Empty) + near,
            // "for every bot after N stalled": the line names the bots that stalled (BotsToShare), not every bot of the world.
            QaSignalKind.SharedSetAside => (s.Quest != 0 ? quest : string.Create(CultureInfo.InvariantCulture, $"entry {s.Target}"))
                + string.Create(CultureInfo.InvariantCulture, $" set aside for every bot after {s.BotCount} stalled"),
            QaSignalKind.DeathLoop => near.TrimStart() + (s.Attackers.Count == 0 ? " attackers none" : " attackers " + string.Join('/', s.Attackers)),
            QaSignalKind.ActionFault => (s.ExceptionType.Length != 0 ? s.ExceptionType : "fault") + " " + (s.FaultCode.Length != 0 ? s.FaultCode : Short(s.Detail)),
            QaSignalKind.Quarantine => s.FaultCode.Length != 0 ? s.FaultCode : Short(s.Detail),
            QaSignalKind.Disabled => s.FaultCode.Length != 0 ? s.FaultCode : Short(s.Detail),
            QaSignalKind.RelayDataMissing when s.Entry != 0 => string.Create(CultureInfo.InvariantCulture, $"relay {s.Relay} {s.Detail} path {s.PathId} missing entry {s.Entry}"),
            QaSignalKind.RelayDataMissing => string.Create(CultureInfo.InvariantCulture, $"relay {s.Relay} unsupported ") + Short(s.Detail),
            QaSignalKind.RelayScriptMissing => string.Create(CultureInfo.InvariantCulture, $"relay {s.Relay} does not exist"),
            QaSignalKind.SpellUnsupported => SpellOf(s) + string.Create(CultureInfo.InvariantCulture, $" effect {s.EffectIndex} ")
                + QaSpellGaps.Describe(s.Reason, s.Detail),
            QaSignalKind.SpellRefused => SpellOf(s) + " refused: " + (s.Reason.Length != 0 ? s.Reason : "unknown result"),
            QaSignalKind.SpellNoEffect => SpellOf(s) + " lands with no damage, healing or aura",
            QaSignalKind.ServerFault => (s.Goal.Length != 0 ? s.Goal : "server") + ": " + (s.ExceptionType.Length != 0 ? s.ExceptionType : "exception"),
            _ => s.Kind.ToString(),
        };
    }

    /// <summary><c>spell 133 Fireball (Rank 1)</c>.</summary>
    internal static string SpellOf(QaSignal s)
        => string.Create(CultureInfo.InvariantCulture, $"spell {s.Spell}") + (s.SpellName.Length != 0 ? " " + s.SpellName : string.Empty)
            + (s.SpellRank.Length != 0 ? " (" + s.SpellRank + ")" : string.Empty);

    private static string Short(string text)
    {
        string flat = PlayerbotQaFingerprint.Normalise(text);
        return flat.Length <= 60 ? flat : flat[..60];
    }
}

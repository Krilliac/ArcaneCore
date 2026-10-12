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
/// Folds signals into findings by fingerprint. One lock guards everything (microseconds per signal); every read returns immutable views.
/// Bots are counted by a salted SHA-256 of their id (the salt is persisted, so counts survive a restart), at most
/// <see cref="QaFinding.MaxTrackedBots"/> per finding: beyond that the count saturates. At the finding cap the lowest-severity, then
/// oldest unpublished finding is evicted; a published or known-closed finding is never evicted (a new finding is refused when nothing can be).
/// The fingerprint puts a location in a 50-yard cell, but bots drift much further within one problem area, so a stall or death loop with no
/// finding of its own cell folds into an existing finding of the same kind, map and non-location identity whose anchor (the first sample's position,
/// persisted; it never moves, so the radius does not chain) lies within <see cref="AnchorYards"/>. A death loop's identity is its attackers
/// (overlapping non-zero entries) or, with none on either side, its death source; a signal whose own cell has a finding of other attackers gets a
/// variant key (<c>|attackers=..</c>) and so its own finding. The v1 keys of existing findings are untouched.
/// Thread-safe.
/// </summary>
internal sealed class PlayerbotQaAggregator(int maxFindings, int maxSamples, byte[]? salt = null) : IQaPublicationState
{
    /// <summary>How far from a finding's anchor (its first sample, in yards, measured on the ground plane) a signal still folds into it.</summary>
    internal const float AnchorYards = 160f;

    private readonly object _gate = new();
    private readonly Dictionary<string, QaFinding> _byId = new(StringComparer.Ordinal);
    private readonly byte[] _salt = salt is { Length: > 0 } ? salt : RandomNumberGenerator.GetBytes(16);
    private IReadOnlyList<QaFindingView>? _snapshot;
    private long _rejected;

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
        lock (_gate)
        {
            _byId.TryGetValue(id, out QaFinding? finding);
            if (finding is null)
            {
                finding = Nearby(signal);
            }
            else if (!SameProblem(finding, signal))
            {
                // The cell's finding is another death loop (other attackers): fold into a nearby one that is this loop, else give this loop its own key.
                finding = Nearby(signal);
                if (finding is null)
                {
                    key = VariantKey(key, signal);
                    id = PlayerbotQaFingerprint.Hash(key);
                    _byId.TryGetValue(id, out finding);
                }
            }

            if (finding is null)
            {
                if (_byId.Count >= maxFindings && !EvictOne(signal.FromCoverage))
                {
                    Interlocked.Increment(ref _rejected);
                    return null;
                }

                finding = new QaFinding { Id = id, Key = key, Kind = signal.Kind, FirstSeenUtc = signal.AtUtc, LastSeenUtc = signal.AtUtc };
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
            if (signal.Quest != 0 || signal.Kind == QaSignalKind.DeathLoop) finding.Quest = signal.Quest;
            if (signal.Target != 0) finding.Target = signal.Target;
            if (signal.HasPosition) finding.Map = signal.Map;
            finding.QuestTitle = signal.Kind == QaSignalKind.DeathLoop ? cause.QuestTitle : cause.QuestTitle ?? finding.QuestTitle;
            finding.Cause = cause.Cause;
            finding.CauseDetail = cause.Detail;
            finding.Goal = signal.Goal;
            if (finding.Subject.Length == 0 || string.Equals(finding.Key, key, StringComparison.Ordinal)) finding.Subject = Subject(signal, finding.QuestTitle);
            finding.Severity = PlayerbotQaClassifier.Severity(finding.Kind, finding.Count, finding.DistinctBots, finding.LastSeenUtc, now);
            _snapshot = null;
            return finding.Id;
        }
    }

    /// <summary>
    /// The finding this signal belongs to by place rather than by key: same kind and map, the same non-location identity, and an anchor within
    /// <see cref="AnchorYards"/> (the nearest wins); null when there is none. Call under the lock.
    /// </summary>
    private QaFinding? Nearby(QaSignal signal)
    {
        if (signal.FromCoverage || !signal.HasPosition || signal.Kind is not (QaSignalKind.Stall or QaSignalKind.DeathLoop)) return null;
        string? identity = null;
        QaFinding? best = null;
        double bestDistance = double.MaxValue;
        foreach (QaFinding f in _byId.Values)
        {
            if (f.Kind != signal.Kind || f.Anchor is not { } anchor || anchor.Map != signal.Map) continue;
            double dx = (double)anchor.X - signal.X, dy = (double)anchor.Y - signal.Y;
            double distance = dx * dx + dy * dy;
            if (!(distance <= (double)AnchorYards * AnchorYards) || distance >= bestDistance) continue;
            if (signal.Kind == QaSignalKind.DeathLoop
                ? !SameProblem(f, signal)
                : !string.Equals(PlayerbotQaFingerprint.LocationFreeKey(f.Key), identity ??= PlayerbotQaFingerprint.LocationFreeKey(PlayerbotQaFingerprint.Key(signal)), StringComparison.Ordinal))
            {
                continue;
            }

            best = f;
            bestDistance = distance;
        }

        return best;
    }

    /// <summary>Whether a finding of the signal's own key is the signal's problem: always, except a death loop, which needs its attackers (or death source).</summary>
    private static bool SameProblem(QaFinding finding, QaSignal signal)
    {
        if (signal.Kind != QaSignalKind.DeathLoop) return true;
        bool any = false;
        foreach (uint attacker in signal.Attackers)
        {
            if (attacker == 0) continue;
            any = true;
            if (finding.Attackers.Contains(attacker)) return true;
        }

        return !any && finding.Attackers.Count == 0 && string.Equals(finding.DeathSource, SourceOf(signal), StringComparison.Ordinal);
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
    /// must not plant a marker); the highest-severity ones are kept when the file holds more than the cap. The number dropped.
    /// </summary>
    internal int Restore(QaAggregatorState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        int dropped = 0;
        lock (_gate)
        {
            _byId.Clear();
            foreach (QaFindingView view in state.Findings.OrderByDescending(v => v.Severity).ThenBy(v => v.Id, StringComparer.Ordinal))
            {
                if (view.Id is not { Length: PlayerbotQaFingerprint.FullChars } || view.Key is null
                    || !string.Equals(PlayerbotQaFingerprint.Hash(view.Key), view.Id, StringComparison.Ordinal)
                    || !Enum.IsDefined(view.Kind) || _byId.Count >= maxFindings || _byId.ContainsKey(view.Id))
                {
                    dropped++;
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
                _byId[finding.Id] = finding;
            }

            _snapshot = null;
        }

        return dropped;
    }

    /// <summary>
    /// A death loop's attackers and death source: as saved, else the saved samples', else (attackers only) the subject's <c>attackers 1/2</c>.
    /// </summary>
    private static void RestoreLoop(QaFinding finding, QaFindingView view)
    {
        IEnumerable<uint> attackers = view.Attackers ?? [];
        if (!attackers.Any()) attackers = (view.Samples ?? []).SelectMany(sm => sm.Attackers ?? []);
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

    private QaFinding? Victim(Func<QaFinding, bool> eligible)
    {
        QaFinding? victim = null;
        foreach (QaFinding f in _byId.Values)
        {
            if (f.Publication.Pinned || !eligible(f)) continue;
            if (victim is null || f.Severity < victim.Severity || (f.Severity == victim.Severity && f.LastSeenUtc < victim.LastSeenUtc)) victim = f;
        }

        return victim;
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

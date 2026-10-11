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
/// Thread-safe.
/// </summary>
internal sealed class PlayerbotQaAggregator(int maxFindings, int maxSamples, byte[]? salt = null) : IQaPublicationState
{
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
            if (!_byId.TryGetValue(id, out QaFinding? finding))
            {
                if (_byId.Count >= maxFindings && !EvictOne())
                {
                    Interlocked.Increment(ref _rejected);
                    return null;
                }

                finding = new QaFinding { Id = id, Key = key, Kind = signal.Kind, FirstSeenUtc = signal.AtUtc };
                _byId[id] = finding;
            }

            finding.Count++;
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

            finding.Samples.Add(new QaSample(signal.AtUtc, signal.HasPosition, signal.Map, signal.X, signal.Y, signal.Z, [.. signal.Attackers], signal.Message));
            while (finding.Samples.Count > maxSamples) finding.Samples.RemoveAt(0);
            if (signal.Quest != 0) finding.Quest = signal.Quest;
            if (signal.Target != 0) finding.Target = signal.Target;
            if (signal.HasPosition) finding.Map = signal.Map;
            finding.QuestTitle = cause.QuestTitle ?? finding.QuestTitle;
            finding.Cause = cause.Cause;
            finding.CauseDetail = cause.Detail;
            finding.Goal = signal.Goal;
            finding.Subject = Subject(signal, finding.QuestTitle);
            finding.Severity = PlayerbotQaClassifier.Severity(finding.Kind, finding.Count, finding.DistinctBots, finding.LastSeenUtc, now);
            _snapshot = null;
            return id;
        }
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
                    OccurrencesReported = Math.Max(0, view.OccurrencesReported),
                };
                if (state.Bots.TryGetValue(view.Id, out ulong[]? bots)) finding.Bots.UnionWith(bots.Take(QaFinding.MaxTrackedBots));
                if (finding.Bots.Count < view.DistinctBots) finding.AnonymousBots = view.DistinctBots;
                foreach (QaSample sample in (view.Samples ?? []).TakeLast(maxSamples)) finding.Samples.Add(sample);
                _byId[finding.Id] = finding;
            }

            _snapshot = null;
        }

        return dropped;
    }

    private bool EvictOne()
    {
        QaFinding? victim = null;
        foreach (QaFinding f in _byId.Values)
        {
            if (f.Publication.Pinned) continue;
            if (victim is null || f.Severity < victim.Severity || (f.Severity == victim.Severity && f.LastSeenUtc < victim.LastSeenUtc)) victim = f;
        }

        return victim is not null && _byId.Remove(victim.Id);
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
            _ => s.Kind.ToString(),
        };
    }

    private static string Short(string text)
    {
        string flat = PlayerbotQaFingerprint.Normalise(text);
        return flat.Length <= 60 ? flat : flat[..60];
    }
}

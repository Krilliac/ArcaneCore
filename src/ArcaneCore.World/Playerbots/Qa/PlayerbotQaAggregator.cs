using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>What the publisher needs of the findings: a current view of one, and a way to record what it did.</summary>
internal interface IQaPublicationState
{
    QaFindingView? Get(string id);

    void Apply(string id, QaPublicationUpdate update);

    /// <summary>The publisher is working on <paramref name="id"/> (it read the finding and will <see cref="Apply"/> an outcome): the finding is not aged out or evicted until <see cref="Unpin"/>.</summary>
    void Pin(string id)
    {
    }

    /// <summary>
    /// Whether <paramref name="build"/> was first run by this store AFTER <paramref name="reference"/> (build strings do not order, so a rollback is not "newer"). False when either is
    /// unknown: nothing is reopened on doubt.
    /// </summary>
    bool IsBuildNewer(string build, string reference) => false;

    void Unpin(string id)
    {
    }

    /// <summary>Findings merged into another whose issue is still open and not yet closed as a duplicate of the survivor's.</summary>
    IReadOnlyList<QaAbsorbed> PendingDuplicates() => [];

    /// <summary>The absorbed finding's issue got its duplicate comment and was closed (or needs neither).</summary>
    void MarkDuplicateHandled(string id)
    {
    }

    /// <summary>The absorbed finding's issue got its duplicate comment (a close that fails afterwards is retried without commenting again).</summary>
    void MarkDuplicateCommented(string id)
    {
    }
}

/// <summary>
/// A finding that was merged into another (<see cref="Survivor"/>). Its id stays recorded so it is never reused for a different problem and an
/// outcome the publisher still reports for it reaches the survivor. <see cref="Issue"/> is its GitHub issue when it had one that the survivor's
/// does not replace (0 otherwise); <see cref="Handled"/> says the issue was closed as a duplicate of the survivor's (or needs no closing).
/// </summary>
internal sealed record QaAbsorbed(string Id, string Survivor, int Issue = 0, bool Handled = true, bool Commented = false);

/// <summary>The aggregator's persisted state: the findings, per finding the salted hashes of the bots that met it, and the merged-away ids.</summary>
internal sealed record QaAggregatorState(IReadOnlyList<QaFindingView> Findings, IReadOnlyDictionary<string, ulong[]> Bots, IReadOnlyList<QaAbsorbed>? Absorbed = null,
    IReadOnlyList<QaTombstone>? Tombstones = null, IReadOnlyList<QaBuildSeen>? Builds = null);

/// <summary>
/// What <see cref="PlayerbotQaAggregator.Prune"/> removed (or, for a dry run, would remove): how many (<see cref="Count"/>), by kind label, the first
/// <see cref="PlayerbotQaAggregator.PruneListed"/> highest-ranked of them (bounded), how many findings are (or would be) left, and how many old ones were kept
/// because they hold a GitHub issue.
/// </summary>
internal sealed record QaPruneResult(int Count, IReadOnlyList<KeyValuePair<string, int>> Kinds, IReadOnlyList<QaFindingView> Listed, int Remaining, bool Dry, int Kept);

/// <summary>
/// A finding that <c>prune closed</c> forgot, or a merge alias of one: the issue it stood for (closed). A new finding with this id takes the issue back
/// as KnownClosed. A death loop also keeps its attackers and death source, so a different loop that now hashes to the same id is not given its issue.
/// </summary>
internal sealed record QaTombstone(string Id, QaSignalKind Kind, int Issue, uint[] Attackers, string DeathSource, DateTime PrunedUtc);

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
internal sealed class PlayerbotQaAggregator(int maxFindings, int maxSamples, byte[]? salt = null, TimeSpan? staleAfter = null, ILogger? log = null, string? build = null) : IQaPublicationState
{
    /// <summary>The build every occurrence is stamped with (<see cref="PlayerbotQaIssueComposer.BuildVersion"/> unless a test or the feature gave one).</summary>
    internal string Build { get; } = QaFinding.CleanBuild(build) ?? PlayerbotQaIssueComposer.BuildVersion();

    /// <summary>The builds the registry remembers (the most recent by first sighting).</summary>
    internal const int MaxKnownBuilds = 32;

    private readonly Dictionary<string, DateTime> _buildSeen = new(StringComparer.Ordinal);
    private bool _buildRegistered;

    /// <summary>Remember the running build with the time it was first seen, unless the registry already knows it; trim to the newest <see cref="MaxKnownBuilds"/>. Call under the lock.</summary>
    private void RegisterBuild(DateTime at)
    {
        if (_buildRegistered) return;
        _buildRegistered = true;
        if (!_buildSeen.TryAdd(Build, at)) return;
        while (_buildSeen.Count > MaxKnownBuilds)
        {
            string oldest = _buildSeen.Where(p => !string.Equals(p.Key, Build, StringComparison.Ordinal)).OrderBy(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).First().Key;
            _buildSeen.Remove(oldest);
        }
    }

    public bool IsBuildNewer(string build, string reference)
    {
        lock (_gate) return _buildSeen.TryGetValue(build, out DateTime now) && _buildSeen.TryGetValue(reference, out DateTime before) && now > before;
    }

    /// <summary>How far from a finding's anchor (its first sample, in yards, measured on the ground plane) a signal still folds into it across a zone cell border.</summary>
    internal const float AnchorYards = 160f;

    private const uint NoMap = uint.MaxValue;

    /// <summary>The most findings <see cref="Prune"/> returns as views.</summary>
    internal const int PruneListed = 10;

    /// <summary>The most tombstones kept (the oldest go first).</summary>
    internal const int TombstoneCap = 1000;

    private readonly object _gate = new();
    private readonly Dictionary<string, QaFinding> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, QaAbsorbed> _absorbed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _inFlight = new(StringComparer.Ordinal);
    private readonly Dictionary<string, QaTombstone> _tombs = new(StringComparer.Ordinal);

    // Lookup indexes over _byId (kept by Add/Remove): a signal looks at its group, its group base or the loops of its map, never at every finding.
    private readonly Dictionary<string, QaFinding> _stallByGroup = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<QaFinding>> _stallByBase = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, List<QaFinding>> _loopsByMap = [];
    private readonly byte[] _salt = salt is { Length: > 0 } ? salt : RandomNumberGenerator.GetBytes(16);
    private IReadOnlyList<QaFindingView>? _snapshot;
    private long _rejected;

    /// <summary>Findings the last <see cref="Restore"/> or <see cref="AgeOut"/> dropped for not being seen within the age limit (running total).</summary>
    internal long AgedOut { get; private set; }

    /// <summary>Findings folded into another one by <see cref="Restore"/> or a bridging signal (running total).</summary>
    internal long Merged { get; private set; }

    /// <summary>
    /// Called (outside the lock, on the ingesting thread) with the view of each finding that did not exist before and came from a real
    /// occurrence (not the start-up coverage). Must be quick and must not throw; a throwing callback is ignored.
    /// </summary>
    internal Action<QaFindingView>? FindingCreated { get; set; }

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
        QaFindingView? created = null;
        bool isNew = false;
        string? ingested;
        lock (_gate)
        {
            RegisterBuild(now);
            QaFinding? finding;
            if (probe is { } wanted)
            {
                finding = Resolve(wanted);
                // A stall that belongs to a merged-away finding (its v1 id is recorded) is the survivor's.
                if (finding is null && wanted.Kind == QaSignalKind.Stall && _absorbed.TryGetValue(id, out QaAbsorbed? alias)
                    && _byId.TryGetValue(alias.Survivor, out QaFinding? survivor) && survivor.Kind == QaSignalKind.Stall)
                {
                    finding = survivor;
                }
            }
            else
            {
                _byId.TryGetValue(id, out finding);
            }

            if (finding is null)
            {
                if (Taken(id, probe, out QaTombstone? tomb))
                {
                    // The v1 key of this signal names a finding that is another death loop (other attackers): this loop gets a variant key of its own.
                    key = VariantKey(key, signal);
                    id = PlayerbotQaFingerprint.Hash(key);
                    for (int n = 2; Taken(id, probe, out tomb); n++)
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

                if (!signal.FromCoverage && signal.HasPosition) finding.Anchor = new QaAnchor(signal.Map, signal.X, signal.Y);
                if (tomb is not null)
                {
                    // A finding forgotten by `prune closed` whose issue was closed: it takes the issue number back instead of looking the issue up again.
                    finding.Publication = new QaPublication(QaPublicationState.KnownClosed, tomb.Issue);
                    _tombs.Remove(tomb.Id);
                }

                Add(finding);
                isNew = !signal.FromCoverage;
            }

            if (signal.FromCoverage)
            {
                // A static fact (the start-up spell coverage): it marks the finding, adds no occurrence, sample or bot.
                finding.Coverage = true;
            }
            else
            {
                long added = Math.Max(1, signal.Occurrences);
                finding.Count += added;
                // A closed finding whose build was never recorded (a file from before format 2, a tombstone's revival) is taken as closed on the build that last met
                // it, else on this one: a recurrence on a later build is then a regression, one on this build is not.
                if (finding.Publication.State == QaPublicationState.KnownClosed && finding.ClosedBuild is null) finding.ClosedBuild = finding.LastBuild ?? Build;
                finding.AddBuild(Build, added);
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
                if (finding.Anchor is null && signal.HasPosition)
                {
                    Remove(finding);
                    finding.Anchor = new QaAnchor(signal.Map, signal.X, signal.Y);
                    Add(finding);
                }

                if (signal.Kind == QaSignalKind.DeathLoop)
                {
                    // The loop's attackers are the union of its signals' (a later signal that shares one is this loop).
                    foreach (uint attacker in signal.Attackers)
                    {
                        if (attacker != 0 && finding.Attackers.Count < QaFinding.MaxLoopAttackers) finding.Attackers.Add(attacker);
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
            // The map follows the anchor: a finding keeps the map it began on.
            if (signal.HasPosition && (finding.Anchor is null || finding.Anchor.Map == signal.Map)) finding.Map = signal.Map;
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
            ingested = finding.Id;
            if (isNew)
            {
                created = finding.ToView(); // once, after the signal has been folded in
            }
        }

        if (created is not null && FindingCreated is { } callback)
        {
            try
            {
                callback(created);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The ingest path must never fail because a subscriber did.
            }
        }

        return ingested;
    }

    /// <summary>
    /// Whether <paramref name="id"/> cannot be the id of a new finding: a finding or merge alias holds it, or a tombstone of a DIFFERENT death loop does
    /// (the variant key is then used, so a loop never takes another loop's issue). A tombstone that fits the signal is returned for the new finding to claim.
    /// Call under the lock.
    /// </summary>
    private bool Taken(string id, Probe? probe, out QaTombstone? tomb)
    {
        tomb = null;
        if (_byId.ContainsKey(id) || _absorbed.ContainsKey(id)) return true;
        if (!_tombs.TryGetValue(id, out QaTombstone? held)) return false;
        if (held.Kind == QaSignalKind.DeathLoop && probe is { Kind: QaSignalKind.DeathLoop } loop && !SameLoop(held.Attackers, held.DeathSource, loop)) return true;
        tomb = held;
        return false;
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
        if (probe.Kind == QaSignalKind.Stall)
        {
            if (_stallByGroup.TryGetValue(probe.Group, out QaFinding? exact)) return [exact];
            if (probe.Base is null || !probe.HasPosition || !_stallByBase.TryGetValue(probe.Base, out List<QaFinding>? sameBase)) return [];
            QaFinding? nearest = null;
            double nearestDistance = double.MaxValue;
            foreach (QaFinding f in sameBase)
            {
                if (f.Anchor is not { } a || a.Map != probe.Map) continue;
                double d = Distance2(a.X, a.Y, probe.X, probe.Y);
                if (d > (double)AnchorYards * AnchorYards) continue;
                // The nearest wins; equal distances go to the lower id, so the choice never depends on insertion order.
                if (d < nearestDistance || (d == nearestDistance && string.CompareOrdinal(f.Id, nearest!.Id) < 0))
                {
                    nearest = f;
                    nearestDistance = d;
                }
            }

            return nearest is null ? [] : [nearest];
        }

        var matches = new List<QaFinding>();
        if (_loopsByMap.TryGetValue(probe.HasPosition ? probe.Map : NoMap, out List<QaFinding>? loops))
        {
            foreach (QaFinding f in loops)
            {
                if (SameArea(f.Anchor, probe) && SameLoop(f.Attackers, f.DeathSource, probe)) matches.Add(f);
            }
        }

        return matches;
    }

    /// <summary>Put a finding in the id map and the lookup indexes. Call under the lock.</summary>
    private void Add(QaFinding f)
    {
        _byId[f.Id] = f;
        if (f.Kind == QaSignalKind.Stall && f.Group.Length > 0)
        {
            _stallByGroup[f.Group] = f;
            if (f.GroupBase is { } baseKey)
            {
                if (!_stallByBase.TryGetValue(baseKey, out List<QaFinding>? list)) _stallByBase[baseKey] = list = [];
                list.Add(f);
            }
        }
        else if (f.Kind == QaSignalKind.DeathLoop)
        {
            f.IndexedMap = f.Anchor?.Map ?? NoMap;
            if (!_loopsByMap.TryGetValue(f.IndexedMap, out List<QaFinding>? list)) _loopsByMap[f.IndexedMap] = list = [];
            list.Add(f);
        }
    }

    /// <summary>Take a finding out of the id map and the indexes; false when it was not the one held under its id. Call under the lock.</summary>
    private bool Remove(QaFinding f)
    {
        if (!_byId.TryGetValue(f.Id, out QaFinding? held) || !ReferenceEquals(held, f)) return false;
        _byId.Remove(f.Id);
        if (f.Kind == QaSignalKind.Stall && f.Group.Length > 0)
        {
            if (_stallByGroup.TryGetValue(f.Group, out QaFinding? byGroup) && ReferenceEquals(byGroup, f)) _stallByGroup.Remove(f.Group);
            if (f.GroupBase is { } baseKey && _stallByBase.TryGetValue(baseKey, out List<QaFinding>? list))
            {
                list.Remove(f);
                if (list.Count == 0) _stallByBase.Remove(baseKey);
            }
        }
        else if (f.Kind == QaSignalKind.DeathLoop && _loopsByMap.TryGetValue(f.IndexedMap, out List<QaFinding>? loops))
        {
            loops.Remove(f);
            if (loops.Count == 0) _loopsByMap.Remove(f.IndexedMap);
        }

        return true;
    }

    private void ClearFindings()
    {
        _byId.Clear();
        _stallByGroup.Clear();
        _stallByBase.Clear();
        _loopsByMap.Clear();
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

    private static int PublicationRank(QaPublication p) => p.State switch
    {
        QaPublicationState.Published => 0,
        QaPublicationState.KnownClosed => 1,
        _ when p.HoldsIssue => 2, // Failed: the issue exists, only the last call failed
        _ => 3,
    };

    /// <summary>
    /// Fold several findings that turned out to be one problem into the best of them: one with an OPEN issue before one whose issue a person
    /// closed, the lowest issue number first, else the busiest. The survivor keeps its id, key and publication state. Every other id is recorded
    /// as absorbed (<see cref="QaAbsorbed"/>): never reused, and the issue of an absorbed published finding is left for the publisher to close as
    /// a duplicate of the survivor's. Call under the lock.
    /// </summary>
    private QaFinding MergeAll(List<QaFinding> findings)
    {
        QaFinding survivor = findings
            .OrderBy(static f => PublicationRank(f.Publication))
            .ThenBy(static f => f.Publication.Issue)
            .ThenByDescending(static f => f.Count)
            .ThenBy(static f => f.Id, StringComparer.Ordinal)
            .First();
        Remove(survivor);
        foreach (QaFinding other in findings)
        {
            if (ReferenceEquals(other, survivor)) continue;
            Absorb(survivor, other);
            Remove(other);
            Merged++;
            bool issue = other.Publication.HoldsIssue; // any state: a Failed publication keeps the number of an issue that exists
            _absorbed[other.Id] = new QaAbsorbed(other.Id, survivor.Id, issue ? other.Publication.Issue : 0, !issue || other.Publication.State == QaPublicationState.KnownClosed);
            foreach (string earlier in _absorbed.Where(p => p.Value.Survivor == other.Id).Select(p => p.Key).ToList()) _absorbed[earlier] = _absorbed[earlier] with { Survivor = survivor.Id, Commented = false }; // the comment named the old survivor
            if (issue)
            {
                log?.LogInformation("Bot QA: issue #{Absorbed} (finding {AbsorbedId}) absorbed into #{Survivor} (finding {SurvivorId}); one problem, one issue",
                    other.Publication.Issue, PlayerbotQaFingerprint.ShortId(other.Id), survivor.Publication.Issue, PlayerbotQaFingerprint.ShortId(survivor.Id));
            }
        }

        Add(survivor);
        _snapshot = null;
        return survivor;
    }

    private void Absorb(QaFinding into, QaFinding from)
    {
        into.AbsorbBuilds(from, from.LastSeenUtc > into.LastSeenUtc);
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
            if (into.Attackers.Count < QaFinding.MaxLoopAttackers) into.Attackers.Add(attacker);
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

        if (!into.Publication.HoldsIssue) into.OccurrencesReported = Math.Max(into.OccurrencesReported, from.OccurrencesReported);
        if (from.LastCommentUtc is { } commented && (into.LastCommentUtc is null || commented > into.LastCommentUtc)) into.LastCommentUtc = commented;
        if (into.Publication.State is QaPublicationState.Pending && from.Publication.State is not QaPublicationState.Pending)
        {
            into.Publication = from.Publication;
            into.ClosedBuild = from.ClosedBuild;
            into.ReportedBuild = from.ReportedBuild;
        }

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
            if (!_byId.TryGetValue(id, out QaFinding? finding))
            {
                // The finding was merged into another while the publisher worked on it: its outcome is the survivor's (or, when the survivor has an
                // issue of its own, the absorbed issue is recorded to be closed as a duplicate), never lost.
                if (_absorbed.TryGetValue(id, out QaAbsorbed? gone) && update.Publication.HoldsIssue) ApplyToAbsorbed(gone, update);
                return;
            }

            bool wasClosed = finding.Publication.State == QaPublicationState.KnownClosed;
            finding.Publication = update.Publication;
            // The build the finding was first observed closed on; cleared once the issue is open again (or the publish failed), so a later closure is stamped afresh.
            if (update.Publication.State != QaPublicationState.KnownClosed) finding.ClosedBuild = null;
            else if (!wasClosed || finding.ClosedBuild is null) finding.ClosedBuild = Build;
            if (update.LastCommentUtc is { } at)
            {
                finding.LastCommentUtc = at;
                finding.ReportedBuild = Build;
            }

            if (update.Reported) finding.ReportedBuild = Build;
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
                ClearFindings();
            }
            else
            {
                removed = _byId.TryGetValue(id, out QaFinding? held) && Remove(held) ? 1 : 0;
            }

            if (removed > 0) _snapshot = null;
            return removed;
        }
    }

    /// <summary>
    /// Forget (or, with <paramref name="dry"/>, only list) the findings last seen more than <paramref name="age"/> before <paramref name="nowUtc"/> and, with
    /// <paramref name="closed"/>, those whose GitHub issue is known closed whatever their age. A finding that holds an issue (Published, Failed, KnownClosed) is
    /// NEVER forgotten by age: its issue number is the only local memory of the issue (a marker lookup cannot always find it again: an issue handed over by a
    /// merged finding, a death-loop variant, a transport without search). Only <paramref name="closed"/> forgets a KnownClosed finding, and it leaves a bounded
    /// <see cref="QaTombstone"/> for it and for its merge aliases, so a recurrence takes the issue number back and stays KnownClosed. A start-up coverage finding is not
    /// pruned by age, a finding the publisher is working on and one with a duplicate issue still to close are skipped. <paramref name="floorUtc"/> (the automatic prune
    /// passes the process start) is the earliest a finding can count as last seen, so nothing is forgotten before the age has passed in this process.
    /// Local only. Pruned findings that recur are created anew (an issue-less one is found again by its marker).
    /// </summary>
    internal QaPruneResult Prune(DateTime nowUtc, TimeSpan age, bool closed, bool dry, DateTime? floorUtc = null)
    {
        DateTime cutoff = nowUtc - age;
        lock (_gate)
        {
            int kept = 0;
            var victims = new List<QaFinding>();
            foreach (QaFinding f in _byId.Values)
            {
                if (IsProtected(f.Id)) continue;
                DateTime seen = floorUtc is { } floor && floor > f.LastSeenUtc ? floor : f.LastSeenUtc;
                bool close = closed && f.Publication.State == QaPublicationState.KnownClosed && !HasPendingDuplicate(f.Id);
                bool old = !f.Coverage && seen < cutoff;
                if (old && f.Publication.HoldsIssue && !close)
                {
                    kept++;
                    continue;
                }

                if (old || close) victims.Add(f);
            }

            QaFinding[] listed = [.. victims.OrderByDescending(static f => f.Severity).ThenByDescending(static f => f.Count).ThenByDescending(static f => f.LastSeenUtc)
                .ThenBy(static f => f.Id, StringComparer.Ordinal).Take(PruneListed)];
            KeyValuePair<string, int>[] kinds = [.. victims.GroupBy(static f => QaFindingView.LabelOf(f.Kind))
                .Select(static g => new KeyValuePair<string, int>(g.Key, g.Count()))
                .OrderByDescending(static p => p.Value).ThenBy(static p => p.Key, StringComparer.Ordinal)];
            var result = new QaPruneResult(victims.Count, kinds, [.. listed.Select(static f => f.ToView())], _byId.Count - victims.Count, dry, kept);
            if (dry || victims.Count == 0) return result;
            foreach (QaFinding f in victims)
            {
                if (f.Publication.HoldsIssue) Tombstone(f, nowUtc);
                Remove(f);
            }

            int excess = _tombs.Count - TombstoneCap;
            if (excess > 0)
            {
                // One sort, then one pass: the oldest go first.
                foreach (QaTombstone oldest in _tombs.Values.OrderBy(static t => t.PrunedUtc).ThenBy(static t => t.Id, StringComparer.Ordinal).Take(excess).ToList()) _tombs.Remove(oldest.Id);
            }

            PruneAbsorbed();
            _snapshot = null;
            return result;
        }
    }

    /// <summary>Remember that <paramref name="f"/> and every id merged into it stood for issue <c>f.Publication.Issue</c>, closed. Call under the lock.</summary>
    private void Tombstone(QaFinding f, DateTime nowUtc)
    {
        int issue = f.Publication.Issue;
        uint[] attackers = [.. f.Attackers.Order()];
        _tombs[f.Id] = new QaTombstone(f.Id, f.Kind, issue, attackers, f.DeathSource, nowUtc);
        foreach (QaAbsorbed alias in _absorbed.Values.Where(a => a.Survivor == f.Id))
        {
            _tombs[alias.Id] = new QaTombstone(alias.Id, f.Kind, issue, attackers, f.DeathSource, nowUtc);
        }
    }

    private bool HasPendingDuplicate(string id) => _absorbed.Values.Any(a => a.Survivor == id && a.Issue > 0 && !a.Handled);

    /// <summary>The state to persist.</summary>
    internal QaAggregatorState Export()
    {
        lock (_gate)
        {
            return new QaAggregatorState([.. _byId.Values.Select(f => f.ToView())],
                _byId.Values.Where(f => f.Bots.Count > 0).ToDictionary(f => f.Id, f => f.Bots.Order().ToArray(), StringComparer.Ordinal),
                [.. _absorbed.Values.OrderBy(a => a.Id, StringComparer.Ordinal)],
                [.. _tombs.Values.OrderBy(t => t.Id, StringComparer.Ordinal)],
                [.. _buildSeen.OrderBy(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => new QaBuildSeen(p.Key, p.Value))]);
        }
    }

    public void Pin(string id)
    {
        lock (_gate) _inFlight[id] = _inFlight.GetValueOrDefault(id) + 1;
    }

    public void Unpin(string id)
    {
        lock (_gate)
        {
            if (!_inFlight.TryGetValue(id, out int n)) return;
            if (n <= 1) _inFlight.Remove(id);
            else _inFlight[id] = n - 1;
        }
    }

    public IReadOnlyList<QaAbsorbed> PendingDuplicates()
    {
        lock (_gate) return [.. _absorbed.Values.Where(a => a.Issue > 0 && !a.Handled).OrderBy(a => a.Id, StringComparer.Ordinal)];
    }

    public void MarkDuplicateHandled(string id)
    {
        lock (_gate)
        {
            if (_absorbed.TryGetValue(id, out QaAbsorbed? a)) _absorbed[id] = a with { Handled = true };
        }
    }

    public void MarkDuplicateCommented(string id)
    {
        lock (_gate)
        {
            if (_absorbed.TryGetValue(id, out QaAbsorbed? a)) _absorbed[id] = a with { Commented = true };
        }
    }

    private void ApplyToAbsorbed(QaAbsorbed gone, QaPublicationUpdate update)
    {
        if (!_byId.TryGetValue(gone.Survivor, out QaFinding? survivor))
        {
            // The survivor is gone as well: the alias protects nothing now, and the issue is left as it is (open, with its marker, as before any merge).
            _absorbed.Remove(gone.Id);
            log?.LogWarning("Bot QA: issue #{Issue} was opened for finding {Id}, which was merged into a finding that no longer exists; it is left open", update.Publication.Issue, PlayerbotQaFingerprint.ShortId(gone.Id));
            return;
        }

        if (!survivor.Publication.HoldsIssue)
        {
            survivor.Publication = update.Publication;
            if (update.LastCommentUtc is { } at) survivor.LastCommentUtc = at;
            if (update.OccurrencesReported is { } reported) survivor.OccurrencesReported = Math.Min(reported, survivor.Count);
            _absorbed[gone.Id] = gone with { Issue = 0, Handled = true };
            _snapshot = null;
            return;
        }

        bool closed = update.Publication.State == QaPublicationState.KnownClosed;
        _absorbed[gone.Id] = gone with { Issue = update.Publication.Issue, Handled = closed };
        if (survivor.Publication.Issue is > 0 and int survivorIssue && update.Publication.Issue > 0)
        {
            log?.LogInformation("Bot QA: issue #{Absorbed} (finding {AbsorbedId}) absorbed into #{Survivor} (finding {SurvivorId}); one problem, one issue",
                update.Publication.Issue, PlayerbotQaFingerprint.ShortId(gone.Id), survivorIssue, PlayerbotQaFingerprint.ShortId(gone.Survivor));
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
            ClearFindings();
            _absorbed.Clear();
            _tombs.Clear();
            // The build registry: as saved (a file without one, from before it, knows no build), then this build joins it with the time of this start-up.
            _buildSeen.Clear();
            foreach (QaBuildSeen seen in (state.Builds ?? []).Where(static b => b is not null && QaFinding.CleanBuild(b.Build) is not null).Take(MaxKnownBuilds))
            {
                _buildSeen[QaFinding.CleanBuild(seen.Build)!] = seen.FirstSeenUtc;
            }

            _buildRegistered = false;
            RegisterBuild(at);
            foreach (QaTombstone t in (state.Tombstones ?? []).Where(static t => t?.Id is { Length: PlayerbotQaFingerprint.FullChars } && t.Issue > 0 && Enum.IsDefined(t.Kind)).Take(TombstoneCap))
            {
                _tombs[t.Id] = t with { Attackers = [.. (t.Attackers ?? []).Take(QaFinding.MaxLoopAttackers)], DeathSource = t.DeathSource ?? string.Empty };
            }

            foreach (QaAbsorbed a in state.Absorbed ?? [])
            {
                if (a.Id is { Length: PlayerbotQaFingerprint.FullChars } && a.Survivor is { Length: PlayerbotQaFingerprint.FullChars } && a.Id != a.Survivor) _absorbed[a.Id] = a with { Issue = Math.Max(0, a.Issue) };
            }

            foreach (QaFindingView view in state.Findings
                .OrderBy(v => PublicationRank(v.Publication ?? QaPublication.Pending))
                .ThenByDescending(v => v.Severity).ThenBy(v => v.Id, StringComparer.Ordinal))
            {
                if (view.Id is not { Length: PlayerbotQaFingerprint.FullChars } || view.Key is null
                    || !string.Equals(PlayerbotQaFingerprint.Hash(view.Key), view.Id, StringComparison.Ordinal)
                    || !Enum.IsDefined(view.Kind) || _byId.ContainsKey(view.Id) || _absorbed.ContainsKey(view.Id))
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
                    // Format 1 files carry no builds: they stay unknown (null / empty) until the first occurrence on this build stamps them.
                    FirstBuild = QaFinding.CleanBuild(view.FirstBuild), LastBuild = QaFinding.CleanBuild(view.LastBuild),
                    ClosedBuild = QaFinding.CleanBuild(view.ClosedBuild), ReportedBuild = QaFinding.CleanBuild(view.ReportedBuild),
                    OccurrencesReported = Math.Max(0, view.OccurrencesReported), Spell = view.Spell, SpellName = view.SpellName ?? string.Empty,
                    SpellRank = view.SpellRank ?? string.Empty, EffectIndex = view.EffectIndex, EffectName = view.EffectName ?? string.Empty,
                    EffectValue = view.EffectValue, Aura = view.Aura, Reason = view.Reason ?? string.Empty, ReasonDetail = view.ReasonDetail ?? string.Empty,
                    Item = view.Item, Npc = view.Npc, GameObject = view.GameObject, ClassMask = view.ClassMask, Coverage = view.Coverage,
                    // Files before 2026-10-12 have no anchor: the first saved sample with a position, else the centre of the key's cell.
                    Anchor = view.Anchor ?? (view.Samples ?? []).Where(sm => sm.HasPosition).Select(sm => new QaAnchor(sm.Map, sm.X, sm.Y)).FirstOrDefault()
                        ?? PlayerbotQaFingerprint.CellCentreOfKey(view.Key),
                };
                foreach (QaBuildCount entry in (view.BuildCounts ?? []).Where(e => QaFinding.CleanBuild(e.Build) is not null && e.Count > 0)
                    .DistinctBy(e => QaFinding.CleanBuild(e.Build), StringComparer.Ordinal).Take(QaFinding.MaxTrackedBuilds))
                {
                    finding.BuildCounts.Add(new QaBuildCount(QaFinding.CleanBuild(entry.Build)!, entry.Count));
                }

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
                        MergeAll(same);
                        continue;
                    }
                }

                // The cap never drops a published or known-closed finding: its issue number is the only local memory of the issue.
                if (_byId.Count >= maxFindings && !finding.Publication.HoldsIssue)
                {
                    dropped++;
                    continue;
                }

                Add(finding);
            }

            PruneAbsorbed();

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
            if (attacker != 0 && finding.Attackers.Count < QaFinding.MaxLoopAttackers) finding.Attackers.Add(attacker);
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
        return victim is not null && Remove(victim);
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
            if (f.Publication.HoldsIssue || IsProtected(f.Id) || !eligible(f)) continue;
            if (victim is null || Cheaper(f, victim)) victim = f;
        }

        return victim;
    }

    /// <summary>A finding the publisher is working on, or the survivor of one it is working on that was merged away meanwhile (its outcome lands on the survivor).</summary>
    private bool IsProtected(string id)
    {
        if (_inFlight.Count == 0) return false;
        if (_inFlight.ContainsKey(id)) return true;
        foreach (string working in _inFlight.Keys)
        {
            if (_absorbed.TryGetValue(working, out QaAbsorbed? a) && string.Equals(a.Survivor, id, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    private static bool OneOff(QaFinding f) => f.Count <= 1 && f.DistinctBots <= 1;

    private static bool Cheaper(QaFinding f, QaFinding than)
    {
        if (OneOff(f) != OneOff(than)) return OneOff(f);
        if (f.Severity != than.Severity) return f.Severity < than.Severity;
        return f.LastSeenUtc < than.LastSeenUtc;
    }

    private bool IsStale(QaPublication publication, bool coverage, DateTime lastSeenUtc, DateTime now)
        => staleAfter is { } limit && !publication.HoldsIssue && !coverage && now - lastSeenUtc > limit;

    /// <summary>
    /// Drop every finding not seen within the age limit that is not published or known-closed (and not a start-up coverage finding, which a
    /// running server lists only once); how many went. Does nothing without an age limit.
    /// </summary>
    internal int AgeOut(DateTime now)
    {
        lock (_gate)
        {
            // A finding the publisher is working on stays until it is done with it, or its issue number would have nowhere to go.
            QaFinding[] stale = [.. _byId.Values.Where(f => !IsProtected(f.Id) && IsStale(f.Publication, f.Coverage, f.LastSeenUtc, now))];
            foreach (QaFinding f in stale) Remove(f);
            if (stale.Length > 0)
            {
                AgedOut += stale.Length;
                _snapshot = null;
            }

            PruneAbsorbed();
            return stale.Length;
        }
    }

    /// <summary>
    /// Forget the merged-away ids whose survivor is gone (reset by hand, or dropped): the alias protects nothing, and an issue it was to close is left open as it was
    /// before the merge. The rest are bounded by twice the finding cap, those without an issue first. Call under the lock.
    /// </summary>
    private void PruneAbsorbed()
    {
        foreach (string id in _absorbed.Where(p => !_byId.ContainsKey(p.Value.Survivor) && !IsProtected(p.Key)).Select(p => p.Key).ToList()) _absorbed.Remove(id);
        int excess = _absorbed.Count - maxFindings * 2;
        if (excess <= 0) return;
        foreach (string id in _absorbed.Where(p => p.Value.Issue == 0).Select(p => p.Key).Order(StringComparer.Ordinal).Take(excess).ToList()) _absorbed.Remove(id);
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

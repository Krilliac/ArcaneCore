using System.Globalization;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Playerbots;

/// <summary>One timestamped bot anomaly notice stored in <see cref="PlayerbotAnomalyHistory"/>.</summary>
public sealed record PlayerbotAnomalyRecord(AnomalyNotice Notice, DateTimeOffset Timestamp)
{
    public string Key => Notice.Key;
    public string Subsystem => Notice.Subsystem;
    public string Code => Notice.Code;
    public AnomalySeverity Severity => Notice.Severity;
    public string Subject => Notice.Subject;
    public string Detail => Notice.Detail;
    public long Count => Notice.Count;
}

/// <summary>
/// Bounded per-bot ring buffer of recent <c>bot-*</c> anomaly notices observed on <see cref="AnomalyLedger.Reported"/>.
/// Keeps up to <see cref="MaxNoticesPerBot"/> notices per bot subject and at most <see cref="MaxBots"/> bot subjects (LRU eviction).
/// Thread-safe: <see cref="AnomalyLedger.Reported"/> may fire from any thread while commands and inspection read on the world thread.
/// </summary>
public sealed class PlayerbotAnomalyHistory : IDisposable
{
    /// <summary>Maximum recent anomaly notices retained per bot subject.</summary>
    public const int MaxNoticesPerBot = 16;

    /// <summary>Maximum distinct bot subjects tracked before the least-recently-updated subject is evicted.</summary>
    public const int MaxBots = 2048;

    private readonly AnomalyLedger _ledger;
    private readonly TimeProvider _time;
    private readonly int _maxPerBot;
    private readonly int _maxBots;
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<BotBucket>> _bySubject = new(StringComparer.Ordinal);
    private readonly LinkedList<BotBucket> _lru = new();
    private bool _disposed;

    /// <summary>The process-wide history subscribed to <see cref="AnomalyLedger.Shared"/>.</summary>
    public static PlayerbotAnomalyHistory Shared { get; } = new(AnomalyLedger.Shared, TimeProvider.System);

    public PlayerbotAnomalyHistory(
        AnomalyLedger ledger,
        TimeProvider? time = null,
        int maxNoticesPerBot = MaxNoticesPerBot,
        int maxBots = MaxBots)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        _ledger = ledger;
        _time = time ?? TimeProvider.System;
        _maxPerBot = Math.Max(1, maxNoticesPerBot);
        _maxBots = Math.Max(1, maxBots);
        _ledger.Reported += OnReported;
    }

    /// <summary>Number of bot subjects currently held in the ring buffer.</summary>
    public int TrackedSubjectCount
    {
        get
        {
            lock (_gate)
            {
                return _bySubject.Count;
            }
        }
    }

    /// <summary>Recent anomalies recorded for <paramref name="player"/> (oldest to newest, up to <see cref="MaxNoticesPerBot"/>).</summary>
    public IReadOnlyList<PlayerbotAnomalyRecord> ForPlayer(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        string subject = PlayerbotAnomalies.SubjectOf(player);
        IReadOnlyList<PlayerbotAnomalyRecord> exact = ForSubject(subject);
        if (exact.Count > 0 || string.IsNullOrWhiteSpace(player.Name))
        {
            return exact;
        }

        return ForBot(player.Name);
    }

    /// <summary>Recent anomalies recorded for an exact <paramref name="subject"/> string (oldest to newest).</summary>
    public IReadOnlyList<PlayerbotAnomalyRecord> ForSubject(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return [];
        }

        lock (_gate)
        {
            return _bySubject.TryGetValue(subject.Trim(), out LinkedListNode<BotBucket>? node)
                ? node.Value.Snapshot()
                : [];
        }
    }

    /// <summary>
    /// Recent anomalies recorded for a bot identified by character name or full subject string (oldest to newest).
    /// Matches <c>bot &lt;name&gt; (guid ...)</c> case-insensitively when given a character name.
    /// </summary>
    public IReadOnlyList<PlayerbotAnomalyRecord> ForBot(string nameOrSubject)
    {
        if (string.IsNullOrWhiteSpace(nameOrSubject))
        {
            return [];
        }

        string trimmed = nameOrSubject.Trim();
        lock (_gate)
        {
            if (_bySubject.TryGetValue(trimmed, out LinkedListNode<BotBucket>? exact))
            {
                return exact.Value.Snapshot();
            }

            string prefixWithGuid = "bot " + trimmed + " (";
            string exactBotName = "bot " + trimmed;
            BotBucket? best = null;
            for (LinkedListNode<BotBucket>? node = _lru.Last; node is not null; node = node.Previous)
            {
                string subj = node.Value.Subject;
                if (subj.StartsWith(prefixWithGuid, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(subj, exactBotName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(subj, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    best = node.Value;
                    break;
                }
            }

            return best is not null ? best.Snapshot() : [];
        }
    }

    /// <summary>Clear all recorded bot anomaly history.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _bySubject.Clear();
            _lru.Clear();
        }
    }

    /// <summary>Formats a compact single-line summary for <c>.playerbot inspect</c> (<c>none</c> when empty).</summary>
    public static string FormatInspectSummary(IReadOnlyList<PlayerbotAnomalyRecord> records, DateTimeOffset now, int maxShown = 4)
    {
        if (records is null || records.Count == 0)
        {
            return "none";
        }

        int start = Math.Max(0, records.Count - Math.Max(1, maxShown));
        var parts = new List<string>(records.Count - start);
        for (int i = start; i < records.Count; i++)
        {
            PlayerbotAnomalyRecord rec = records[i];
            long ageSec = (long)Math.Max(0, (now - rec.Timestamp).TotalSeconds);
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{rec.Key}({rec.Severity},-{ageSec}s)"));
        }

        return string.Create(CultureInfo.InvariantCulture, $"count={records.Count} recent=[{string.Join(", ", parts)}]");
    }

    /// <summary>Executes <c>.bot anomalies &lt;name&gt;</c> / <c>.playerbot anomalies &lt;name&gt;</c>. Returns false on syntax error.</summary>
    internal static bool RunCommand(
        string args,
        ManagedPlayerbotFeature? feature,
        PlayerbotAnomalyHistory history,
        DateTimeOffset now,
        Action<string> reply)
    {
        string query = args.Trim();
        if (query.Length == 0 || query.Contains(' ', StringComparison.Ordinal))
        {
            return false;
        }

        IReadOnlyList<PlayerbotAnomalyRecord> records = [];
        string displayTarget = query;

        if (feature is not null && feature.TryResolveBotId(query, out Guid botId, out string? resolvedName))
        {
            if (!string.IsNullOrWhiteSpace(resolvedName))
            {
                displayTarget = resolvedName;
            }

            WorldSession? session = feature.FindSession(botId);
            if (session?.Player is { } botPlayer)
            {
                displayTarget = botPlayer.Name;
                records = history.ForPlayer(botPlayer);
            }
            else if (!string.IsNullOrWhiteSpace(resolvedName))
            {
                records = history.ForBot(resolvedName);
            }
        }

        if (records.Count == 0)
        {
            records = history.ForBot(query);
        }

        if (records.Count == 0)
        {
            reply(string.Create(CultureInfo.InvariantCulture, $"No recent anomalies recorded for bot '{displayTarget}'."));
            return true;
        }

        string subjectLabel = records[^1].Subject;
        reply(string.Create(CultureInfo.InvariantCulture, $"Bot anomalies for {subjectLabel} ({records.Count} recent):"));
        for (int i = 0; i < records.Count; i++)
        {
            PlayerbotAnomalyRecord rec = records[i];
            string age = GmDuration.SecsToTimeString((long)Math.Max(0, (now - rec.Timestamp).TotalSeconds), shortText: true).Trim();
            string detail = rec.Detail.Length == 0 ? "(none)" : rec.Detail.ReplaceLineEndings(" ");
            reply(string.Create(CultureInfo.InvariantCulture,
                $"{i + 1,2}. [{rec.Timestamp.UtcDateTime:HH:mm:ss} UTC, {age} ago] {rec.Key} ({rec.Severity}, #{rec.Count}): {detail}"));
        }

        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _ledger.Reported -= OnReported;
    }

    private void OnReported(AnomalyNotice notice)
    {
        if (!notice.Subsystem.StartsWith("bot-", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(notice.Subject))
        {
            return;
        }

        DateTimeOffset now = _time.GetUtcNow();
        var record = new PlayerbotAnomalyRecord(notice.Origin is null ? notice : notice with { Origin = null }, now); // the origin token identifies a world; the history outlives worlds

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_bySubject.TryGetValue(notice.Subject, out LinkedListNode<BotBucket>? node))
            {
                _lru.Remove(node);
                _lru.AddLast(node);
            }
            else
            {
                if (_bySubject.Count >= _maxBots && _lru.First is { } oldest)
                {
                    _lru.RemoveFirst();
                    _bySubject.Remove(oldest.Value.Subject);
                }

                node = new LinkedListNode<BotBucket>(new BotBucket(notice.Subject, _maxPerBot));
                _lru.AddLast(node);
                _bySubject[notice.Subject] = node;
            }

            node.Value.Add(record);
        }
    }

    private sealed class BotBucket(string subject, int capacity)
    {
        private readonly PlayerbotAnomalyRecord[] _ring = new PlayerbotAnomalyRecord[capacity];
        private int _head;
        private int _count;

        public string Subject { get; } = subject;

        public void Add(PlayerbotAnomalyRecord record)
        {
            _ring[_head] = record;
            _head = (_head + 1) % _ring.Length;
            if (_count < _ring.Length)
            {
                _count++;
            }
        }

        public IReadOnlyList<PlayerbotAnomalyRecord> Snapshot()
        {
            if (_count == 0)
            {
                return [];
            }

            var result = new PlayerbotAnomalyRecord[_count];
            int start = (_head + _ring.Length - _count) % _ring.Length;
            for (int i = 0; i < _count; i++)
            {
                result[i] = _ring[(start + i) % _ring.Length];
            }

            return result;
        }
    }
}

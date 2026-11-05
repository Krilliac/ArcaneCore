using System.Globalization;
using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.World.Gm.TicketAssistant;

/// <summary>
/// The recent position anomalies the server's movement observers reported for each character: <c>move/under-map</c> with
/// <c>reason=below-terrain</c> (<c>UndermapObserver</c>: the server's own terrain floor against the reported position), heard from
/// <see cref="AnomalyLedger.Reported"/> (every report, not only the logged ones). <c>move/fall-without-landing</c> is not accepted: its
/// duration is the larger of the server's clock and the fall time the client reports, so one crafted packet produces it; and an under-map
/// report of <c>reason=below-void-height</c> is not accepted either, because a stock client that jumps into the void causes it. The ledger's
/// subject is "player (guid N)" (a name before the guid is tolerated); the guid is read from it. Memory is
/// bounded: at most <see cref="MaxCharacters"/> characters with <see cref="MaxPerCharacter"/> signals each. Thread-safe (the ledger
/// reports from the world and map threads).
/// </summary>
public sealed class TicketEvidenceLog
{
    /// <summary>The most characters tracked; the oldest is dropped past it.</summary>
    public const int MaxCharacters = 512;

    /// <summary>The most signals kept per character.</summary>
    public const int MaxPerCharacter = 4;

    private const string GuidMarker = "(guid ";

    private readonly Lock _gate = new();
    private readonly Dictionary<uint, List<TicketSignal>> _signals = [];
    private readonly TimeProvider _clock;
    private readonly AnomalyLedger _ledger;
    private bool _listening;

    public TicketEvidenceLog(TimeProvider clock, AnomalyLedger? ledger = null)
    {
        _clock = clock;
        _ledger = ledger ?? AnomalyLedger.Shared;
    }

    /// <summary>Start hearing the ledger (idempotent).</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_listening)
            {
                return;
            }

            _listening = true;
        }

        _ledger.Reported += OnReported;
    }

    /// <summary>Stop hearing the ledger and forget everything.</summary>
    public void Stop()
    {
        _ledger.Reported -= OnReported;
        lock (_gate)
        {
            _listening = false;
            _signals.Clear();
        }
    }

    /// <summary>Whether a ledger report is a position signal (the codes the assistant accepts as evidence).</summary>
    public static bool IsPositionSignal(string subsystem, string code)
        => subsystem == "move" && code == "under-map";

    /// <summary>The detail <c>UndermapObserver</c> gives for a position under the terrain floor (not the void-height report).</summary>
    public const string BelowTerrainReason = "reason=below-terrain";

    private void OnReported(AnomalyNotice notice)
    {
        if (!IsPositionSignal(notice.Subsystem, notice.Code)
            || !notice.Detail.Contains(BelowTerrainReason, StringComparison.Ordinal)
            || GuidOf(notice.Subject) is not { } guid)
        {
            return;
        }

        Note(guid, notice.Code, notice.Detail);
    }

    /// <summary>Record a signal for the character with this low guid (also the test seam).</summary>
    public void Note(uint guid, string code, string detail)
    {
        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        lock (_gate)
        {
            if (!_signals.TryGetValue(guid, out List<TicketSignal>? list))
            {
                if (_signals.Count >= MaxCharacters)
                {
                    uint oldest = _signals.OrderBy(pair => pair.Value[^1].AtUnix).First().Key;
                    _signals.Remove(oldest);
                }

                _signals[guid] = list = [];
            }

            if (list.Count >= MaxPerCharacter)
            {
                list.RemoveAt(0);
            }

            list.Add(new TicketSignal(code, now, TicketClassifier.Clean(detail)));
        }
    }

    /// <summary>The newest signal of the character no older than <paramref name="windowSeconds"/>, or null.</summary>
    public TicketSignal? Recent(uint guid, int windowSeconds)
    {
        long cutoff = _clock.GetUtcNow().ToUnixTimeSeconds() - windowSeconds;
        lock (_gate)
        {
            return _signals.TryGetValue(guid, out List<TicketSignal>? list) && list[^1].AtUnix >= cutoff ? list[^1] : null;
        }
    }

    /// <summary>The horizontal position a signal's detail names (<c>pos=(x,y,z)</c>), or false when it names none.</summary>
    public static bool TryPositionOf(string detail, out float x, out float y)
    {
        x = y = 0f;
        const string marker = "pos=(";
        int start = detail.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        start += marker.Length;
        int end = detail.IndexOf(')', start);
        if (end < 0)
        {
            return false;
        }

        string[] parts = detail[start..end].Split(',');
        return parts.Length >= 2
            && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
            && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
            && float.IsFinite(x) && float.IsFinite(y);
    }

    internal static uint? GuidOf(string subject)
    {
        int start = subject.LastIndexOf(GuidMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += GuidMarker.Length;
        int end = subject.IndexOf(')', start);
        return end > start && uint.TryParse(subject.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out uint guid) ? guid : null;
    }
}

using System.Globalization;
using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.World.Gm.TicketAssistant;

/// <summary>
/// The recent position anomalies the server's movement observers reported for each character: <c>move/under-map</c>
/// (<c>UndermapObserver</c>) and <c>move/fall-without-landing</c> (<c>FallObserver</c>), heard from <see cref="AnomalyLedger.Reported"/>
/// (every report, not only the logged ones). The ledger's subject is "player NAME (guid N)"; the guid is read from it. Memory is
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
        => subsystem == "move" && code is "under-map" or "fall-without-landing";

    private void OnReported(AnomalyNotice notice)
    {
        if (!IsPositionSignal(notice.Subsystem, notice.Code) || GuidOf(notice.Subject) is not { } guid)
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

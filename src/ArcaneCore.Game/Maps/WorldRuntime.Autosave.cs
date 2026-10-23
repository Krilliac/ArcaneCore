using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Maps;

/// <summary>
/// The periodic per-character autosave (<see cref="Autosave"/>).
/// </summary>
public sealed partial class WorldRuntime
{
    private ulong _autosaveClockMs; // world thread: the sum of tick diffs, the clock the per-character autosave timers run on
    private readonly Dictionary<ObjectGuid, ulong> _autosaveDue = []; // world thread: each online character's next autosave on _autosaveClockMs
    private ulong _autosaveNextDue; // world thread: the earliest entry of _autosaveDue at the last scan (a removal can leave it early: one extra scan)
    private bool _autosaveRescan = true; // world thread: a player came online since the last scan, whose timer is not set yet

    /// <summary>
    /// The periodic save, one character at a time on its own timer (vmangos <c>Player::m_nextSave</c>, counted down from
    /// <c>PlayerSave.Interval</c> in <c>Player::Update</c>): each online character is snapshotted once per <paramref name="interval"/>.
    /// A character's first save comes between half an interval and an interval after the tick that first sees it, at a point fixed
    /// by its id (<see cref="AutosaveStagger"/>), so characters that entered the world together (a server start, a bot fleet) are
    /// spread over the interval instead of all being snapshotted in one tick (vmangos spreads the first save randomly over half an interval
    /// to one and a half; see <see cref="AutosaveStagger"/> for the difference) (a whole-world pass queues one write per online character
    /// at once: about 1000 with the live bot fleet). A character whose previous snapshot is still waiting in the queue is skipped until
    /// its next turn (the queue is behind; another snapshot would only supersede the waiting one and cost a copy).
    /// Unlike vmangos, whose <c>SaveToDB</c> resets <c>m_nextSave</c>, a settlement or other save does not restart the timer.
    /// The timers are only scanned when the earliest one is due or a player came online since the last scan, so most ticks cost
    /// nothing. Returns whether it scanned; <paramref name="queued"/> is the number of snapshots queued.
    /// </summary>
    private bool Autosave(ulong interval, out int queued)
    {
        queued = 0;
        ulong clock = _autosaveClockMs;
        if (!_autosaveRescan && clock < _autosaveNextDue)
        {
            return false;
        }

        _autosaveRescan = false;
        ulong next = ulong.MaxValue;
        uint now = NowMs;
        foreach (KeyValuePair<ObjectGuid, Player> entry in _online) // not .Values: that locks the whole dictionary and copies it
        {
            Player player = entry.Value;
            ref ulong due = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_autosaveDue, player.Guid, out bool seen);
            bool turn = false;
            if (!seen)
            {
                due = clock + interval - AutosaveStagger(player.Guid.Low, interval);
            }
            else if (due <= clock)
            {
                // The next turn is one interval after this one (not after now), so the stagger survives late ticks; a stall longer than
                // an interval skips the turns it missed instead of saving the character again on every tick until it catches up.
                do
                {
                    due += interval;
                }
                while (due <= clock);
                turn = true;
            }

            next = Math.Min(next, due);
            if (!turn)
            {
                continue;
            }

            if (player.IsQuestSettlementPending || _saveQueue.IsPending(unchecked((int)player.Guid.Low)))
            {
                continue;
            }

            try
            {
                _saveQueue.Enqueue(player.CreateSnapshot(now));
                queued++;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("world.character-save", ex);
                _logger.LogFault("character.save-snapshot", ex,
                    "failed to snapshot player {Player} ({Guid}) on map {MapId} at ({X:F1},{Y:F1},{Z:F1})",
                    player.Name, player.Guid, player.MapId, player.X, player.Y, player.Z);
            }
        }

        _autosaveNextDue = next;
        return true;
    }

    /// <summary>
    /// How much earlier than a full interval a character's first autosave comes: a point in [0, interval / 2) fixed by its id
    /// (Fibonacci hashing), so consecutive ids are spread evenly. vmangos staggers too, but randomly and wider: its Player constructor
    /// sets <c>m_nextSave = urand(interval / 2, interval * 3 / 2)</c>; here the window is [interval / 2, interval) and deterministic.
    /// </summary>
    internal static ulong AutosaveStagger(uint characterLow, ulong interval)
        => (unchecked(characterLow * 2654435769u) * (interval / 2)) >> 32;
}

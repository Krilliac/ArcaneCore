using ArcaneCore.Game.AntiCheat;
using ArcaneCore.Kernel.AntiCheat;

namespace ArcaneCore.World.AntiCheat;

/// <summary>
/// The in-memory side of the violation log (docs/areas/anticheat.md): findings are queued, repeats of one type by one
/// character within <see cref="AntiCheatLogOptions.CoalesceMs"/> are folded into the row already queued, and the queue is
/// bounded (beyond <see cref="AntiCheatLogOptions.MaxQueuedRows"/> new rows are dropped and counted). A flush takes at most
/// <see cref="AntiCheatLogOptions.MaxRowsPerFlush"/> rows for one batched insert, never one INSERT per violation.
/// World thread only.
/// </summary>
public sealed class AntiCheatLogQueue
{
    private readonly List<Pending> _rows = [];
    private readonly Dictionary<(int CharacterId, AntiCheatViolation Type), Pending> _open = [];

    /// <summary>Rows waiting.</summary>
    public int Count => _rows.Count;

    /// <summary>Rows dropped because the queue was full (diagnostics).</summary>
    public long Dropped { get; private set; }

    /// <summary>Queue one finding, folding it into the open row of its character and type when that is recent enough.</summary>
    public void Add(AntiCheatLogEntry entry, AntiCheatViolation type, uint nowMs, AntiCheatLogOptions options)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(options);
        (int, AntiCheatViolation) key = (entry.CharacterId, type);
        if (_open.TryGetValue(key, out Pending? open) && unchecked(nowMs - open.OpenedMs) <= (uint)options.CoalesceMs)
        {
            open.Entry = open.Entry with
            {
                Count = open.Entry.Count + entry.Count,
                Weight = open.Entry.Weight + entry.Weight,
                Score = entry.Score,
                MapId = entry.MapId,
                X = entry.X,
                Y = entry.Y,
                Z = entry.Z,
                LastAt = entry.LastAt,
            };
            return;
        }

        if (_rows.Count >= options.MaxQueuedRows)
        {
            Dropped++;
            return;
        }

        var pending = new Pending(entry, nowMs);
        _rows.Add(pending);
        _open[key] = pending;
    }

    /// <summary>Take up to <paramref name="max"/> rows, oldest first (a taken row is closed: later findings open a new one).</summary>
    public IReadOnlyList<AntiCheatLogEntry> Take(int max)
    {
        int count = Math.Min(Math.Max(0, max), _rows.Count);
        var taken = new List<AntiCheatLogEntry>(count);
        for (int i = 0; i < count; i++)
        {
            Pending pending = _rows[i];
            taken.Add(pending.Entry);
            (int, AntiCheatViolation) key = (pending.Entry.CharacterId, (AntiCheatViolation)pending.Entry.Type);
            if (_open.TryGetValue(key, out Pending? open) && ReferenceEquals(open, pending))
            {
                _open.Remove(key);
            }
        }

        _rows.RemoveRange(0, count);
        return taken;
    }

    /// <summary>Forget the queued rows of a character (<c>.anticheat delete</c>).</summary>
    public void Remove(int characterId)
    {
        _rows.RemoveAll(p => p.Entry.CharacterId == characterId);
        foreach ((int, AntiCheatViolation) key in _open.Keys.Where(k => k.CharacterId == characterId).ToList())
        {
            _open.Remove(key);
        }
    }

    private sealed class Pending(AntiCheatLogEntry entry, uint openedMs)
    {
        public AntiCheatLogEntry Entry { get; set; } = entry;

        public uint OpenedMs { get; } = openedMs;
    }
}

using System.Globalization;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// The dead-end pin cells every bot of the world avoids: a 10-yard cell (<see cref="PlayerbotStallWatch.PinKey"/>) that
/// <see cref="BotsToShare"/> different bots each dead-end-pinned (two stalls of their own in it, <see cref="PlayerbotStallWatch.PinStallsToEscalate"/>)
/// within <see cref="WindowMs"/> is a world pin for <see cref="PinMs"/>: explore candidates keep out of it
/// (<see cref="PlayerbotNavigation.IsWorldPin"/>), and a bot's first stall inside it already counts as a dead-end pin, so it escapes at once.
/// World pins never refuse a plan (<see cref="PlayerbotNavigation.IsDeadEndPin"/> is per bot): an NPC or a flag in the cell stays reachable.
/// The caller reports only from open-world maps outside cities (PlayerbotBrain.DeadEndPinShareBlock, the area rule); cells are kept per map instance anyway.
/// A report from a cut region (<see cref="PlayerbotNavigation.IsCutRegion"/>) is weak evidence: bots in a tunnel stall in each other's way, so such reports
/// make a world pin only as a crowd of <see cref="CutRegionBotsToShare"/> different bots. A world pin cannot shut a tunnel, since it refuses no plan, though a first stall in it teleports the bot out.
/// Live 2026-10-25: 60 bots were pinned at two Deadwind Pass spawn points; the pin ledger is per bot, so each of them needed two stalls of
/// two minutes or more to learn what the first had already found. A repeat after a pin ended lasts twice as long (at most
/// <see cref="MaxPinMs"/>); bounded by <see cref="MaxKeys"/> (the one reported longest ago goes first). Kept in memory only: a restart relearns it.
/// <para>
/// Thread affinity: any thread. The bots of different maps think in parallel, so every access takes <see cref="_gate"/>; the critical
/// sections are a dictionary lookup (and, on a report, a scan of at most <see cref="CutRegionBotsToShare"/> stalls).
/// </para>
/// </summary>
internal sealed class PlayerbotSharedPins
{
    /// <summary>Different bots that must dead-end-pin one cell for it to be a world pin.</summary>
    internal const int BotsToShare = 2;

    /// <summary>
    /// Different bots that must dead-end-pin one cell for it to be a world pin when their stalls lie in a cut region. Polygon size cannot tell a tunnel from open
    /// ground: Timbermaw Hold's largest region holds 4882 polygons and cut regions hold about 11% of each continent. The region of the Deadwind Pass cell of audit
    /// cid 44 is unknown: elimination says only some cut region of at most 5000 polygons, and a neighbouring point 71 yards away lies in one of 4323.
    /// The value is a judgement, not a measurement. The logs sampled start after the Timbermaw fix of 2026-10-25 (the first Timbermaw line is 2026-10-26 19:47, so the
    /// incident day is not in them) and are heavily throttled: they show at most 2 different bots in a Timbermaw cell within <see cref="WindowMs"/> and 21 in the Deadwind
    /// cell. Both are floors, and a floor bounds no maximum, so whether 4 keeps tunnel cells unpinned is unproven. The downside is bounded: a false tunnel pin refuses
    /// no plan, so walks through keep their route, but explore candidates keep out of the cell and a bot's first stall in it teleports the bot home or to its bind point.
    /// </summary>
    internal const int CutRegionBotsToShare = 4;

    /// <summary>How long a bot's dead-end pin counts towards <see cref="BotsToShare"/>.</summary>
    internal const uint WindowMs = 1_800_000;

    /// <summary>How long the first world pin of a cell lasts.</summary>
    internal const uint PinMs = 1_800_000;

    /// <summary>The longest world pin (repeats double it).</summary>
    internal const uint MaxPinMs = 7_200_000;

    /// <summary>After a day without a report, a cell's next world pin starts at the first duration again.</summary>
    internal const uint RepeatQuietMs = 86_400_000;

    /// <summary>The most cells remembered.</summary>
    internal const int MaxKeys = 256;

    private readonly object _gate = new();
    private readonly Dictionary<(ulong Cell, uint Instance), Record> _records = [];

    /// <summary>
    /// The world whose 64-bit uptime is the records' clock (<see cref="PlayerbotWorldClock.Ms(ArcaneCore.Game.Maps.WorldRuntime, uint)"/>).
    /// Without a world (tests only; the feature sets it on attach) the given time is used as it is, with no wrap handling.
    /// </summary>
    internal ArcaneCore.Game.Maps.WorldRuntime? World { get; set; }

    private long T(uint nowMs) => World is { } world ? PlayerbotWorldClock.Ms(world, nowMs) : nowMs;

    /// <summary>Cells remembered (bounded by <see cref="MaxKeys"/>).</summary>
    internal int Count
    {
        get
        {
            lock (_gate) return _records.Count;
        }
    }

    /// <summary>
    /// Bot <paramref name="bot"/> dead-end-pinned the cell containing <c>(x, y)</c> on <paramref name="map"/> at world time
    /// <paramref name="nowMs"/>. Returns a description (cell and length) when the cell newly became a world pin, else null.
    /// <paramref name="inCutRegion"/>: the stall lay in a cut region, so it counts only towards <see cref="CutRegionBotsToShare"/>.
    /// </summary>
    internal string? Report(uint map, uint instance, float x, float y, ulong bot, uint nowMs, bool inCutRegion = false)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y)) return null;
        (ulong, uint) key = (PlayerbotStallWatch.PinKey(map, x, y).Id, instance);
        long now = T(nowMs);
        lock (_gate)
        {
            if (!_records.TryGetValue(key, out Record? record))
            {
                if (_records.Count >= MaxKeys) _records.Remove(Victim(now));
                _records[key] = record = new Record();
            }

            if (record.Times != 0 && now - record.LastMs > RepeatQuietMs && !Active(record, now))
            {
                record.Times = 0;
                record.UntilMs = 0;
            }

            record.LastMs = now;
            if (Active(record, now)) return null; // already a world pin: nobody is kept waiting to make it one
            record.Bots.RemoveAll(seen => now - seen.AtMs > WindowMs || seen.Bot == bot);
            record.Bots.Add(new Seen(bot, now, inCutRegion));
            if (!Enough(record.Bots)) return null;
            uint length = (uint)Math.Min((ulong)PinMs << Math.Min(record.Times, 8), MaxPinMs);
            record.Times = Math.Min(record.Times + 1, 8);
            record.UntilMs = now + length;
            record.Bots.Clear();
            float cell = PlayerbotStallWatch.PinCellYards;
            return string.Create(CultureInfo.InvariantCulture,
                $"map {map} cell {MathF.Floor(x / cell) * cell:F0},{MathF.Floor(y / cell) * cell:F0} for {length / 60_000} min");
        }
    }

    /// <summary>Whether the cell containing <c>(x, y)</c> on <paramref name="map"/> is a world pin at world time <paramref name="nowMs"/>.</summary>
    internal bool IsWorldPin(uint map, uint instance, float x, float y, uint nowMs)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y)) return false;
        (ulong, uint) key = (PlayerbotStallWatch.PinKey(map, x, y).Id, instance);
        long now = T(nowMs);
        lock (_gate) return _records.TryGetValue(key, out Record? record) && Active(record, now);
    }

    /// <summary>The record to drop for a new cell: the one reported longest ago among those whose pin is not active (it protects nobody), else the one reported longest ago. Linear; caller holds the gate.</summary>
    private (ulong Cell, uint Instance) Victim(long now)
    {
        (ulong, uint) best = default, bestInactive = default;
        long oldest = long.MaxValue, oldestInactive = long.MaxValue;
        foreach (KeyValuePair<(ulong Cell, uint Instance), Record> pair in _records)
        {
            if (pair.Value.LastMs < oldest) { oldest = pair.Value.LastMs; best = pair.Key; }
            if (!Active(pair.Value, now) && pair.Value.LastMs < oldestInactive) { oldestInactive = pair.Value.LastMs; bestInactive = pair.Key; }
        }

        return oldestInactive != long.MaxValue ? bestInactive : best;
    }

    private static bool Active(Record record, long now) => record.UntilMs != 0 && record.UntilMs > now;

    /// <summary>Whether the bots seen in a cell make it a world pin: <see cref="BotsToShare"/> outside a cut region, or a crowd of <see cref="CutRegionBotsToShare"/> counting those in one. A cell can hold both, on two floors.</summary>
    private static bool Enough(List<Seen> bots)
    {
        if (bots.Count >= CutRegionBotsToShare) return true;
        int outside = 0;
        foreach (Seen seen in bots)
            if (!seen.InCutRegion) outside++;
        return outside >= BotsToShare;
    }

    private readonly record struct Seen(ulong Bot, long AtMs, bool InCutRegion);

    private sealed class Record
    {
        public List<Seen> Bots { get; } = [];
        public long UntilMs { get; set; }
        public int Times { get; set; }
        public long LastMs { get; set; }
    }
}

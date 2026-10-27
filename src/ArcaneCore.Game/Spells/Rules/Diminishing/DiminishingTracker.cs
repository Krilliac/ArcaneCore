namespace ArcaneCore.Game.Spells.Rules.Diminishing;

/// <summary>
/// The diminishing-return entries of one unit (vmangos Unit::m_Diminishing with GetDiminishing,
/// IncrDiminishing and ApplyDiminishingAura, Unit.cpp:7618-7690). The level is read at hit time; the
/// first hit creates the entry already at level 2; the window to the reset starts when the LAST aura of
/// the group is removed (so a refreshed or still running aura cannot reset it).
/// </summary>
public sealed class DiminishingTracker
{
    private readonly List<Entry> _entries = [];

    /// <summary>
    /// The level a new hit would get at <paramref name="nowMs"/>: level 1 without an entry, level 1 and a reset
    /// entry when no aura of the group is up and more than <paramref name="resetMs"/> passed since the last
    /// one ended, otherwise the entry's count.
    /// </summary>
    public DiminishingLevel GetLevel(DiminishingGroup group, uint nowMs, uint resetMs) => GetLevel(group, nowMs, resetMs, resetSweepMs: 0);

    /// <summary>
    /// <see cref="GetLevel(DiminishingGroup, uint, uint)"/> with an optional reset sweep. <paramref name="resetSweepMs"/> 0 (retail, patch 1.4.0:
    /// "within 15 seconds of the *end* of the duration") resets as soon as more than <paramref name="resetMs"/> passed since the last aura ended.
    /// A positive value models a server that only looks for expired entries every <paramref name="resetSweepMs"/> (on the clock's multiples of it),
    /// so the reset lands between <paramref name="resetMs"/> and <paramref name="resetMs"/> + <paramref name="resetSweepMs"/> after the end
    /// (the "15-20 s" reading, <see cref="SpellRuleOptions.DiminishingResetSweepMs"/>). No random draw either way.
    /// </summary>
    public DiminishingLevel GetLevel(DiminishingGroup group, uint nowMs, uint resetMs, uint resetSweepMs)
    {
        foreach (Entry entry in _entries)
        {
            if (entry.Group != group)
            {
                continue;
            }

            if (entry.HitCount == 0)
            {
                return DiminishingLevel.Level1;
            }

            if (entry.Stack == 0 && IsResetDue(unchecked(nowMs - entry.HitTime), nowMs, resetMs, resetSweepMs))
            {
                entry.HitCount = 0;
                return DiminishingLevel.Level1;
            }

            return (DiminishingLevel)entry.HitCount;
        }

        return DiminishingLevel.Level1;
    }

    /// <summary>
    /// Whether the reset is due <paramref name="elapsedMs"/> after the last aura ended. With a sweep, the last sweep at or before
    /// <paramref name="nowMs"/> (<paramref name="nowMs"/> minus its remainder) must itself be more than <paramref name="resetMs"/> after the end.
    /// </summary>
    private static bool IsResetDue(uint elapsedMs, uint nowMs, uint resetMs, uint resetSweepMs)
    {
        if (resetSweepMs == 0)
        {
            return elapsedMs > resetMs;
        }

        uint sinceSweep = nowMs % resetSweepMs;
        return elapsedMs >= sinceSweep && elapsedMs - sinceSweep > resetMs;
    }

    /// <summary>One more hit: bump an existing entry up to immune, or create it at level 2.</summary>
    public void Increment(DiminishingGroup group, uint nowMs)
    {
        foreach (Entry entry in _entries)
        {
            if (entry.Group == group)
            {
                if (entry.HitCount < (int)DiminishingLevel.Immune)
                {
                    entry.HitCount++;
                }

                return;
            }
        }

        _entries.Add(new Entry(group, nowMs, (int)DiminishingLevel.Level2));
    }

    /// <summary>An aura of the group was added (<paramref name="applied"/>) or removed; removing the last one stamps the time.</summary>
    public void AuraChanged(DiminishingGroup group, bool applied, uint nowMs)
    {
        foreach (Entry entry in _entries)
        {
            if (entry.Group != group)
            {
                continue;
            }

            if (applied)
            {
                entry.Stack++;
            }
            else if (entry.Stack > 0)
            {
                entry.Stack--;
                if (entry.Stack == 0)
                {
                    entry.HitTime = nowMs;
                }
            }

            return;
        }
    }

    /// <summary>vmangos Unit::ClearDiminishings (on death, Unit.cpp:7363).</summary>
    public void Clear() => _entries.Clear();

    private sealed class Entry(DiminishingGroup group, uint hitTime, int hitCount)
    {
        public DiminishingGroup Group { get; } = group;

        public int Stack { get; set; }

        public uint HitTime { get; set; } = hitTime;

        public int HitCount { get; set; } = hitCount;
    }
}

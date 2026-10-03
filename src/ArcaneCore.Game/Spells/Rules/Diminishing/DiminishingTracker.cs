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
    public DiminishingLevel GetLevel(DiminishingGroup group, uint nowMs, uint resetMs)
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

            if (entry.Stack == 0 && unchecked(nowMs - entry.HitTime) > resetMs)
            {
                entry.HitCount = 0;
                return DiminishingLevel.Level1;
            }

            return (DiminishingLevel)entry.HitCount;
        }

        return DiminishingLevel.Level1;
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

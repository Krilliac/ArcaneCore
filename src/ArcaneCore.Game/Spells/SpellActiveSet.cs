namespace ArcaneCore.Game.Spells;

/// <summary>
/// Why a <see cref="UnitSpellState"/> is woken into <see cref="SpellSystem"/>'s active set (see SpellSystem.ActiveSet.cs). Each member names
/// the <c>SpellSystem.UpdateState</c> branch (or the <c>SpellSystem.IsDormant</c> clause) the change can feed. A reason can be switched off with
/// <c>SpellSystem.DisabledWakeReasons</c> so a test can prove the audit notices the missing wake.
/// </summary>
[Flags]
internal enum SpellWakeReason
{
    None = 0,

    /// <summary>The state was just created (<c>GetOrCreateState</c>). Feeds the final <c>IsIdle</c> removal in UpdateState: an empty new state is dropped on its first visit.</summary>
    StateCreated = 1 << 0,

    /// <summary>A holder was added to <see cref="UnitSpellState.Auras"/>. Feeds UpdateAuras (durations, periodic ticks) and UpdateAreaAuras.</summary>
    AuraAdded = 1 << 1,

    /// <summary>A holder left <see cref="UnitSpellState.Auras"/>. Feeds the <c>IsIdle</c> removal (the last holder gone) and UpdateAreaAuras.</summary>
    AuraRemoved = 1 << 2,

    /// <summary>A field of a held holder or aura that <c>HolderNeedsTick</c> or the area-source clause reads changed. Feeds UpdateAuras.</summary>
    HolderChanged = 1 << 3,

    /// <summary><see cref="UnitSpellState.CurrentCast"/> was set. Feeds the <c>UpdateCast</c> branch and <c>VerifySpellStateInvariants</c>.</summary>
    CastStarted = 1 << 4,

    /// <summary><see cref="UnitSpellState.AutoRepeatCast"/> was set. Feeds the <c>UpdateAutoRepeat</c> branch.</summary>
    AutoRepeatStarted = 1 << 5,

    /// <summary><see cref="UnitSpellState.MeleeCast"/> was set. Feeds the <c>IsIdle</c> test (a queued next-swing spell keeps the state).</summary>
    MeleeQueued = 1 << 6,

    /// <summary><see cref="UnitSpellState.PendingChannelResetMs"/> became non-zero. Feeds the <c>UpdatePendingChannelReset</c> branch.</summary>
    ChannelResetPending = 1 << 7,

    /// <summary>A spell, category or global cooldown entry was written or removed. Feeds <c>ExpireCooldowns</c> and the <c>IsIdle</c> removal.</summary>
    Cooldown = 1 << 8,

    /// <summary>A school lockout entry was written or removed. Feeds <c>ExpireCooldowns</c> and the <c>IsIdle</c> removal.</summary>
    SchoolLockout = 1 << 9,

    /// <summary>The unit's <c>Map</c> was written (enter, leave, transit). Feeds the <c>IsInWorld</c>/<c>IsInTransit</c>/<c>Forget</c> branch at the top of UpdateState.</summary>
    MapChanged = 1 << 10,

    /// <summary>The audit found a dormant state that was not dormant and woke it (a missed wake site). Feeds whichever branch the failed dormancy clause names.</summary>
    AuditHeal = 1 << 11,
}

/// <summary>
/// A cooldown or lockout table whose every mutation wakes the owning <see cref="UnitSpellState"/>, including removals: removing the last entry
/// can make the state <c>IsIdle</c>, and the visit is what drops it from the spell system. The mutators are hidden with <c>new</c>, so a write
/// through the <see cref="Dictionary{TKey, TValue}"/> base type would skip the wake; nothing in the code base does that for a state that is
/// not already active (the static <c>ExpireCooldowns.Expire</c> removes during the state's own visit).
/// </summary>
internal sealed class WakingTimerMap<TKey> : Dictionary<TKey, uint> where TKey : notnull
{
    private readonly UnitSpellState _owner;
    private readonly SpellWakeReason _reason;

    internal WakingTimerMap(UnitSpellState owner, SpellWakeReason reason)
    {
        _owner = owner;
        _reason = reason;
    }

    public new uint this[TKey key]
    {
        get => base[key];
        set
        {
            base[key] = value;
            _owner.Wake(_reason);
        }
    }

    public new void Add(TKey key, uint value)
    {
        base.Add(key, value);
        _owner.Wake(_reason);
    }

    public new bool TryAdd(TKey key, uint value)
    {
        bool added = base.TryAdd(key, value);
        if (added)
        {
            _owner.Wake(_reason);
        }

        return added;
    }

    public new bool Remove(TKey key)
    {
        bool removed = base.Remove(key);
        if (removed)
        {
            _owner.Wake(_reason);
        }

        return removed;
    }

    public new bool Remove(TKey key, out uint value)
    {
        bool removed = base.Remove(key, out value);
        if (removed)
        {
            _owner.Wake(_reason);
        }

        return removed;
    }

    public new void Clear()
    {
        if (Count > 0)
        {
            base.Clear();
            _owner.Wake(_reason);
        }
    }
}

/// <summary>
/// <see cref="UnitSpellState.Auras"/>: a holder list whose every mutation wakes the owning <see cref="UnitSpellState"/>
/// (<see cref="SpellWakeReason.AuraAdded"/> / <see cref="SpellWakeReason.AuraRemoved"/>). The mutators are hidden with <c>new</c>; the spell
/// system only mutates through the declared type.
/// </summary>
internal sealed class WakingHolderList : List<SpellAuraHolder>
{
    private readonly UnitSpellState _owner;

    internal WakingHolderList(UnitSpellState owner) => _owner = owner;

    public new void Add(SpellAuraHolder item)
    {
        base.Add(item);
        _owner.Wake(SpellWakeReason.AuraAdded);
    }

    public new void Insert(int index, SpellAuraHolder item)
    {
        base.Insert(index, item);
        _owner.Wake(SpellWakeReason.AuraAdded);
    }

    public new bool Remove(SpellAuraHolder item)
    {
        bool removed = base.Remove(item);
        if (removed)
        {
            _owner.Wake(SpellWakeReason.AuraRemoved);
        }

        return removed;
    }

    public new void RemoveAt(int index)
    {
        base.RemoveAt(index);
        _owner.Wake(SpellWakeReason.AuraRemoved);
    }

    public new int RemoveAll(Predicate<SpellAuraHolder> match)
    {
        int removed = base.RemoveAll(match);
        if (removed > 0)
        {
            _owner.Wake(SpellWakeReason.AuraRemoved);
        }

        return removed;
    }

    public new void Clear()
    {
        if (Count > 0)
        {
            base.Clear();
            _owner.Wake(SpellWakeReason.AuraRemoved);
        }
    }
}

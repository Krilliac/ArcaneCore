namespace ArcaneCore.World.Playerbots;

/// <summary>
/// What a bot defers (puts off until it may succeed, or until a deadline). Append-only: values are never renumbered or reused
/// (they index <see cref="PlayerbotDeferLedger"/>'s policy table, so a new kind is appended to both). The persisted row kind of a
/// deferral is a separate number (<see cref="PlayerbotDeferLedger.PersistAs"/>, <c>PlayerbotSetAsideKind</c>).
/// </summary>
internal enum DeferKind : byte
{
    Entry = 0,
    Quest = 1,
    Training = 2,
    UnreachableQuest = 3,
    PinCell = 4,
}

/// <summary>The changes in a bot or in the world that make a deferral worth trying again before its deadline. Append-only.</summary>
[Flags]
internal enum DeferWake : ushort
{
    None = 0,
    /// <summary>The bot gained a level.</summary>
    Level = 1 << 0,
    /// <summary>The navigation dataset was swapped (fleet-wide, jittered).</summary>
    NavDataset = 1 << 1,
    /// <summary>The creature definitions were swapped (fleet-wide, jittered).</summary>
    Definitions = 1 << 2,
    /// <summary>The quest store was replaced (fleet-wide, jittered).</summary>
    QuestStore = 1 << 3,
}

/// <summary>What is deferred: a kind and an id.</summary>
internal readonly record struct DeferKey(DeferKind Kind, ulong Id);

/// <summary>
/// What <see cref="PlayerbotDeferLedger.Defer"/> decided: the length of the deferral from the call (<paramref name="Ms"/>), the strikes
/// the key has now, and whether this deferral began an episode (strikes went 0 to 1).
/// </summary>
internal readonly record struct DeferResult(uint Ms, int Strikes, bool NewEpisode);

/// <summary>
/// A deferral of a persisted kind, as <see cref="PlayerbotDeferLedger.Persistable"/> lists it: the time it has left (0 for a kept
/// strike count whose deferral ended) and its strikes.
/// </summary>
internal readonly record struct DeferRecord(DeferKey Key, uint RemainingMs, int Strikes);

/// <summary>
/// One bot's deferrals: things it set aside (a creature entry, a quest, a trainer visit, an unreachable ender, a dead-end pin cell)
/// until a deadline, or until something about the bot or the world changes (<see cref="DeferWake"/>). One primitive replaces the
/// ad-hoc timers each goal kept.
/// <para>
/// Strikes: a key deferred again escalates its backoff along its kind's steps (<see cref="Policy"/>: an entry doubles from 10 minutes up to 80, a quest and a training visit stay at 10 minutes; an unreachable quest goes 60 s, 10 min, 1 h, 6 h, 24 h; a dead-end pin cell 2, 10, 30, 60 min). A deferral that
/// ends (by deadline or <see cref="Wake(DeferKey, long)"/>) keeps its strikes, so the next one is longer; <see cref="Succeeded"/> forgets
/// them; strikes of a kind that <see cref="Policy.Decays"/> also decay once <see cref="StrikeDecayMs"/> passed since the last
/// deferral ended (not since it began). An unreachable quest's strikes never decay and are saved while the deferral is over as well
/// (<see cref="Persistable"/>): the bound on abandoning and re-taking a quest must hold however long one cycle takes, and across a
/// restart. Only <see cref="Succeeded"/> (a turn-in) clears them.
/// </para>
/// <para>
/// Time: every call takes the time as 64-bit milliseconds on one monotonic clock that never wraps (the world's
/// <c>WorldRuntime.Uptime</c>; <see cref="PlayerbotSuspensions"/> and <see cref="PlayerbotStallWatch"/> convert the 32-bit
/// <c>NowMs</c> their callers hold against it). Deadlines are stored on that clock and compared in 64-bit arithmetic only, so an
/// ended deferral cannot read as live again however long the bot goes without a ledger call, or however long the server runs.
/// </para>
/// <para>
/// Bounded: per kind its policy's cap (<see cref="Policy.MaxEntries"/>; the caps together are far below <see cref="MaxTotal"/>, which
/// is the persisted-row limit of a bot). When a kind is full, a new key always costs one existing entry its strikes: the ended entry
/// with the fewest effective (decayed) strikes goes first, the oldest of those; a live deferral is cut short only when no entry has
/// ended, and then the one soonest to end goes. Recording never refuses.
/// </para>
/// <para>
/// Ownership: one bot's brain owns its ledger (<see cref="PlayerbotSuspensions.Ledger"/>). Thread affinity: the world thread, except
/// <see cref="Version"/>, which the persistence thread reads. No locks. Memory: nothing is allocated until the first deferral; each
/// entry is one small object and a dictionary slot.
/// </para>
/// </summary>
internal sealed class PlayerbotDeferLedger
{
    /// <summary>The most rows of every kind together that a bot's saved set-asides may have (the store's row limit); the per-kind caps sum to less.</summary>
    internal const int MaxTotal = 512;

    /// <summary>A key whose last deferral ended this long ago starts again at one strike.</summary>
    internal const uint StrikeDecayMs = 3_600_000;

    /// <summary>The widest delay of a fleet-wide wake (<see cref="Jitter"/>).</summary>
    internal const uint JitterSpanMs = 60_000;

    /// <summary>Causes every bot of the world sees at once: waking them together would stampede the planners.</summary>
    internal const DeferWake FleetWide = DeferWake.NavDataset | DeferWake.Definitions | DeferWake.QuestStore;

    private const int MaxStrikes = 255;
    private const int FlagCount = 4;

    /// <summary>
    /// The deferral's rules for one kind. <paramref name="Steps"/>, when given, are the lengths by strike (the last holds); otherwise the
    /// length doubles from <paramref name="MinMs"/> up to <paramref name="MaxMs"/>. <paramref name="PersistAs"/> is the persisted row
    /// kind, or -1 when not persisted. <paramref name="Decays"/> false keeps the strikes until <see cref="Succeeded"/>.
    /// </summary>
    internal readonly record struct Policy(uint MinMs, uint MaxMs, int MaxEntries, int PersistAs, DeferWake DefaultWake,
        uint[]? Steps = null, bool Decays = true);

    private static readonly Policy[] Policies =
    [
        new(600_000, 4_800_000, 64, 0, DeferWake.None),                                                              // Entry
        new(600_000, 600_000, 64, 1, DeferWake.None),                                                                // Quest
        new(600_000, 600_000, 1, 2, DeferWake.None),                                                                 // Training
        new(60_000, 86_400_000, 64, 5, DeferWake.Level | DeferWake.NavDataset | DeferWake.QuestStore | DeferWake.Definitions,
            [60_000, 600_000, 3_600_000, 21_600_000, 86_400_000], Decays: false),                                    // UnreachableQuest
        new(120_000, 3_600_000, 64, -1, DeferWake.None, [120_000, 600_000, 1_800_000, 3_600_000]),                   // PinCell (the first stall is no pin yet: the dead-end windows are 10, 30, 60 min)
    ];

    /// <summary>The most entries all kinds together can hold (the sum of the caps).</summary>
    internal static int TotalCapacity => Policies.Sum(policy => policy.MaxEntries);

    private sealed class Slot
    {
        /// <summary>The end of the deferral on the ledger's unwrapped clock.</summary>
        public long UntilMs;
        public int Strikes;
        public DeferWake Wake;
        public bool Live;
    }

    private Dictionary<DeferKey, Slot>? _slots;
    private readonly int[] _kindCounts = new int[Policies.Length];
    private readonly int[] _waiters = new int[FlagCount];
    private int _version;
    private int _waitingSlots;
    private long _earliestWakeDeadline;

    /// <summary>Bumped only when a deferral of a persisted kind began, ended early or was forgotten (what a save would write changed).</summary>
    internal int Version => Volatile.Read(ref _version);

    /// <summary>What <see cref="Jitter"/> hashes: the bot's guid, set by the brain.</summary>
    internal ulong Seed { get; set; }

    /// <summary>
    /// The causes some live deferral waits for at <paramref name="now"/>; the brain's probe computes only these. Deferrals past
    /// their deadline stop waiting here (the only place besides <see cref="WakeAll(DeferWake, long)"/> that expires them), so a
    /// finished deferral costs the probe nothing. The sweep runs only when the earliest waiting deadline has passed.
    /// </summary>
    internal DeferWake WaitingAt(long now)
    {
        if (_waitingSlots == 0) return DeferWake.None;
        if (_earliestWakeDeadline <= now) Sweep(now);
        DeferWake waiting = DeferWake.None;
        for (int bit = 0; bit < FlagCount; bit++)
            if (_waiters[bit] != 0) waiting |= (DeferWake)(1 << bit);
        return waiting;
    }

    internal static Policy PolicyOf(DeferKind kind) => Policies[(int)kind];

    /// <summary>The persisted row kind of <paramref name="kind"/>, or -1.</summary>
    internal static int PersistAs(DeferKind kind) => Policies[(int)kind].PersistAs;

    /// <summary>The default length of the deferral at <paramref name="strikes"/> strikes.</summary>
    internal static uint Backoff(DeferKind kind, int strikes)
    {
        Policy policy = Policies[(int)kind];
        if (policy.Steps is { } steps) return steps[Math.Clamp(strikes - 1, 0, steps.Length - 1)];
        ulong ms = (ulong)policy.MinMs << Math.Clamp(strikes - 1, 0, 16);
        return (uint)Math.Min(ms, policy.MaxMs);
    }

    /// <summary>The stagger (0 to <see cref="JitterSpanMs"/>) of a fleet-wide wake for the bot with <paramref name="seed"/>.</summary>
    internal static uint Jitter(ulong seed) => (uint)(Mix(seed) % JitterSpanMs);

    /// <summary>A stable 64-bit mix (splitmix64 finaliser); string and Guid hash codes are not stable across runs.</summary>
    internal static ulong Mix(ulong value)
    {
        value += 0x9E3779B97F4A7C15UL;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }

    /// <summary>
    /// Defer <paramref name="key"/> from <paramref name="now"/>. The deadline is <paramref name="untilMs"/> when given (a time not
    /// after <paramref name="now"/> ends the deferral at once), else the kind's backoff at the new strike count.
    /// <paramref name="strike"/> false records the deferral without escalating (a key never has fewer than
    /// <paramref name="minStrikes"/> strikes while it exists). <paramref name="wake"/> null takes the kind's default causes.
    /// </summary>
    internal DeferResult Defer(DeferKey key, long now, DeferWake? wake = null, long? untilMs = null, bool strike = true, int minStrikes = 1)
    {
        Policy policy = Policies[(int)key.Kind];
        Dictionary<DeferKey, Slot> slots = _slots ??= [];
        if (!slots.TryGetValue(key, out Slot? slot))
        {
            MakeRoom(key.Kind, policy, now);
            slots[key] = slot = new Slot();
            _kindCounts[(int)key.Kind]++;
        }
        else
        {
            ClearWake(slot);
            if (Decayed(policy, slot, now)) slot.Strikes = 0;
        }

        bool episode = slot.Strikes == 0;
        slot.Strikes = Math.Clamp(strike ? slot.Strikes + 1 : slot.Strikes, Math.Max(1, minStrikes), MaxStrikes);
        uint length = untilMs is { } until ? (uint)Math.Clamp(until - now, 0, uint.MaxValue) : Backoff(key.Kind, slot.Strikes);
        slot.UntilMs = now + length;
        slot.Live = true;
        SetWake(slot, wake ?? policy.DefaultWake);
        if (policy.PersistAs >= 0) Interlocked.Increment(ref _version);
        return new DeferResult(length, slot.Strikes, episode);
    }

    /// <summary>Whether <paramref name="key"/> is deferred at <paramref name="now"/>. Changes no deferral, whatever time it is asked.</summary>
    internal bool IsDeferred(DeferKey key, long now)
    {
        return _slots is { } slots && slots.TryGetValue(key, out Slot? slot) && slot.Live && slot.UntilMs > now;
    }

    /// <summary>The strikes of <paramref name="key"/> as stored (0: none, or forgotten). Strikes that would decay on the next deferral are still counted; see <see cref="EffectiveStrikes"/>.</summary>
    internal int Strikes(DeferKey key) => _slots is { } slots && slots.TryGetValue(key, out Slot? slot) ? slot.Strikes : 0;

    /// <summary>The strikes <paramref name="key"/> would escalate from if deferred at <paramref name="now"/>: <see cref="Strikes"/> after the decay a deferral then applies.</summary>
    internal int EffectiveStrikes(DeferKey key, long now)
    {
        if (_slots is not { } slots || !slots.TryGetValue(key, out Slot? slot)) return 0;
        return Decayed(Policies[(int)key.Kind], slot, now) ? 0 : slot.Strikes;
    }

    /// <summary>End the deferral of <paramref name="key"/> now; its strikes stay. False when it was not deferred.</summary>
    internal bool Wake(DeferKey key, long now)
    {
        if (_slots is not { } slots || !slots.TryGetValue(key, out Slot? slot) || !slot.Live) return false;
        bool wasLive = slot.UntilMs > now;
        Release(key.Kind, slot, now);
        return wasLive;
    }

    /// <summary>The goal the deferral was for was done: forget the entry and its strikes.</summary>
    internal bool Succeeded(DeferKey key)
    {
        if (_slots is not { } slots || !slots.TryGetValue(key, out Slot? slot)) return false;
        Remove(key, slot);
        return true;
    }

    /// <summary>
    /// End the deferrals waiting for any of <paramref name="causes"/>. A fleet-wide cause (<see cref="FleetWide"/>) alone ends the
    /// deferral after this bot's <see cref="Jitter"/> instead, so the bots of a world do not all plan again in one tick; a cause of
    /// the bot's own ends it now. Returns the deferrals ended or scheduled to end.
    /// </summary>
    internal int WakeAll(DeferWake causes, long now)
    {
        if (_slots is not { } slots || slots.Count == 0 || causes == DeferWake.None) return 0;
        int woken = 0;
        foreach ((DeferKey key, Slot slot) in slots)
        {
            DeferWake hit = slot.Wake & causes;
            if (hit == DeferWake.None) continue;
            if (!slot.Live || slot.UntilMs <= now)
            {
                Expire(slot);
                continue;
            }

            uint jitter = (hit & ~FleetWide) == 0 ? Jitter(Seed) : 0;
            if (jitter == 0)
            {
                Release(key.Kind, slot, now);
            }
            else
            {
                bool shortened = jitter < slot.UntilMs - now;
                if (shortened) slot.UntilMs = now + jitter;
                ClearWake(slot);
                if (shortened && Policies[(int)key.Kind].PersistAs >= 0) Interlocked.Increment(ref _version);
            }

            woken++;
        }

        return woken;
    }

    /// <summary>
    /// What a save writes at <paramref name="now"/>: the live deferrals of persisted kinds with the time they have left, and the
    /// strikes of a persisted kind that never decays (the unreachable quest) whose deferral is over (remaining 0).
    /// </summary>
    internal List<DeferRecord> Persistable(long now)
    {
        List<DeferRecord> rows = [];
        if (_slots is not { } slots) return rows;
        foreach ((DeferKey key, Slot slot) in slots)
        {
            Policy policy = Policies[(int)key.Kind];
            if (policy.PersistAs < 0) continue;
            if (slot.Live && slot.UntilMs > now) rows.Add(new DeferRecord(key, (uint)(slot.UntilMs - now), slot.Strikes));
            else if (KeepsStrikes(policy, slot)) rows.Add(new DeferRecord(key, 0, slot.Strikes));
        }

        return rows;
    }

    /// <summary>
    /// Put back a deferral read from a save: <paramref name="remainingMs"/> left (0: the deferral is over and only the strikes come
    /// back), <paramref name="strikes"/> strikes, the kind's default wakes. Does not count as a change to save.
    /// </summary>
    internal void RestoreEntry(DeferKey key, long now, uint remainingMs, int strikes)
    {
        Policy policy = Policies[(int)key.Kind];
        Dictionary<DeferKey, Slot> slots = _slots ??= [];
        if (!slots.TryGetValue(key, out Slot? slot))
        {
            MakeRoom(key.Kind, policy, now);
            slots[key] = slot = new Slot();
            _kindCounts[(int)key.Kind]++;
        }
        else
        {
            ClearWake(slot);
        }

        slot.Strikes = Math.Clamp(strikes, 1, MaxStrikes);
        slot.UntilMs = now + remainingMs;
        slot.Live = remainingMs > 0;
        if (slot.Live) SetWake(slot, policy.DefaultWake);
    }

    /// <summary>Count a change a save must write that no deferral call made (a legacy row rewritten with a finite deadline).</summary>
    internal void MarkChanged() => Interlocked.Increment(ref _version);

    // Quiet is counted from the end of the last deferral, so a long one never decays while it lasts.
    private static bool Decayed(Policy policy, Slot slot, long now)
        => policy.Decays && slot.Strikes > 0 && !(slot.Live && slot.UntilMs > now) && now - slot.UntilMs > StrikeDecayMs;

    private static bool KeepsStrikes(Policy policy, Slot slot) => !policy.Decays && slot.Strikes > 0;

    private bool IsPersisted(DeferKind kind, Slot slot)
    {
        Policy policy = Policies[(int)kind];
        return policy.PersistAs >= 0 && (slot.Live || KeepsStrikes(policy, slot));
    }

    private void Sweep(long now)
    {
        bool any = false;
        long earliest = 0;
        foreach (Slot slot in _slots!.Values)
        {
            if (slot.Wake == DeferWake.None) continue;
            if (!slot.Live || slot.UntilMs <= now)
            {
                Expire(slot);
                continue;
            }

            if (!any || slot.UntilMs < earliest) earliest = slot.UntilMs;
            any = true;
        }

        _earliestWakeDeadline = earliest;
    }

    private void Release(DeferKind kind, Slot slot, long now)
    {
        bool wasLive = slot.Live && slot.UntilMs > now;
        slot.UntilMs = now;
        Expire(slot);
        if (wasLive && Policies[(int)kind].PersistAs >= 0) Interlocked.Increment(ref _version);
    }

    private void Expire(Slot slot)
    {
        slot.Live = false;
        ClearWake(slot);
    }

    private void Remove(DeferKey key, Slot slot)
    {
        bool persisted = IsPersisted(key.Kind, slot);
        ClearWake(slot);
        _slots!.Remove(key);
        _kindCounts[(int)key.Kind]--;
        if (persisted) Interlocked.Increment(ref _version);
    }

    private void SetWake(Slot slot, DeferWake wake)
    {
        slot.Wake = wake;
        if (wake != DeferWake.None && (_waitingSlots++ == 0 || slot.UntilMs < _earliestWakeDeadline))
            _earliestWakeDeadline = slot.UntilMs;

        for (int bit = 0; bit < FlagCount; bit++)
            if (((int)wake & (1 << bit)) != 0) _waiters[bit]++;
    }

    private void ClearWake(Slot slot)
    {
        DeferWake wake = slot.Wake;
        if (wake == DeferWake.None) return;
        slot.Wake = DeferWake.None;
        _waitingSlots--;
        for (int bit = 0; bit < FlagCount; bit++)
            if (((int)wake & (1 << bit)) != 0) _waiters[bit]--;
    }

    /// <summary>
    /// Make room for a new entry of <paramref name="kind"/> when its cap is reached: evict its expired entry with the fewest strikes
    /// (the oldest of those), else its entry soonest to end.
    /// </summary>
    private void MakeRoom(DeferKind kind, Policy policy, long now)
    {
        if (_kindCounts[(int)kind] < policy.MaxEntries) return;
        DeferKey? expired = null, soonest = null;
        Slot? expiredSlot = null;
        int expiredStrikes = 0;
        long soonestUntil = long.MaxValue;
        foreach ((DeferKey key, Slot slot) in _slots!)
        {
            if (key.Kind != kind) continue;
            if (!slot.Live || slot.UntilMs <= now)
            {
                int strikes = Decayed(policy, slot, now) ? 0 : slot.Strikes;
                if (expiredSlot is null || strikes < expiredStrikes
                    || (strikes == expiredStrikes && slot.UntilMs < expiredSlot.UntilMs))
                {
                    expired = key;
                    expiredSlot = slot;
                    expiredStrikes = strikes;
                }

                continue;
            }

            if (slot.UntilMs < soonestUntil) { soonestUntil = slot.UntilMs; soonest = key; }
        }

        DeferKey? victim = expired ?? soonest;
        if (victim is { } evict) Remove(evict, _slots[evict]);
    }
}

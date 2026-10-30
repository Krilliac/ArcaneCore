using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Ops.Watchdog.Counters;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// The spell tick's active set. <see cref="Update"/> visits only the <see cref="UnitSpellState"/>s that have something to do; a state with
/// nothing but permanent passives (a bot) costs nothing per tick. A state leaves the set only when its own update proves it dormant
/// (<see cref="IsDormant"/>), and re-enters through <see cref="UnitSpellState.Wake"/>, which the state's collections and fields call
/// structurally. A missed wake would mean an aura that never expires, so a rotating audit re-checks dormant states and reports and heals
/// any that are not (<c>spell/active-set-stale</c>). World thread only.
/// </summary>
public sealed partial class SpellSystem
{
    private static readonly Counter StatesVisitedGauge = CounterRegistry.Default.GetOrAdd("spell.states_visited", CounterKind.Gauge);
    private static readonly Counter StatesActiveGauge = CounterRegistry.Default.GetOrAdd("spell.states_active", CounterKind.Gauge);

    // Compaction predicate: cached so the per-tick RemoveAll allocates nothing. Clears InActiveList on each state it drops.
    private static readonly Predicate<UnitSpellState> LeavesActiveList = static s =>
    {
        bool leaves = !s.IsActive || s.Detached;
        if (leaves)
        {
            s.InActiveList = false;
        }

        return leaves;
    };

    private static readonly Predicate<UnitSpellState> IsDetachedState = static s => s.Detached;

    /// <summary>States that want a visit (or are waiting for compaction), sorted by <see cref="UnitSpellState.Seq"/>.</summary>
    private readonly List<UnitSpellState> _active = [];

    /// <summary>Every attached state in creation order (detached ones are dropped lazily), for the audit's rotating scan.</summary>
    private readonly List<UnitSpellState> _all = [];

    /// <summary>States woken while no update loop was running: unsorted and already marked <see cref="UnitSpellState.InActiveList"/>; merged into <c>_active</c> once at the start of the next active-set update.</summary>
    private readonly List<UnitSpellState> _pendingWakes = [];

    private static readonly Comparison<UnitSpellState> BySeq = static (a, b) => a.Seq.CompareTo(b.Seq);

    private long _nextStateSeq;
    private long _updateTick;
    private bool _activeIterating;
    private int _activeCursor = -1;
    private int _auditPosition;
    private int _allDetached;

    /// <summary>Test seam: false runs the original visit-every-state loop (the active set is still maintained).</summary>
    internal bool ActiveSetEnabled { get; set; } = true;

    /// <summary>Test seam: wake reasons that are ignored, so a test can prove the audit notices the missing wake.</summary>
    internal SpellWakeReason DisabledWakeReasons { get; set; }

    /// <summary>How many dormant states the audit re-checks per <see cref="Update"/> (entries examined in the rotating scan); 0 disables the audit.</summary>
    internal int ActiveSetAuditBudget { get; set; } = 64;

    /// <summary>Test seam: the audit throws <see cref="InvalidOperationException"/> after healing a stale state.</summary>
    internal bool ActiveSetAuditThrows { get; set; }

    /// <summary>Stale dormant states the audit found and woke (each one is a missed wake site).</summary>
    internal long ActiveSetStaleHeals { get; private set; }

    /// <summary>Test seam: where the stale-state audit resumes in the all-states list (kept across a compaction of that list).</summary>
    internal int AuditPositionForTests => _auditPosition;

    /// <summary>States <see cref="Update"/> visited on the last tick.</summary>
    internal int LastStatesVisited { get; private set; }

    /// <summary>States tracked (<c>_states.Count</c>) on the last tick.</summary>
    internal int LastStatesTracked { get; private set; }

    /// <summary>States still active after the last tick.</summary>
    internal int LastStatesActive { get; private set; }

    /// <summary>Put <paramref name="state"/> in the active set; called by <see cref="UnitSpellState.Wake"/> when the state is not active.
    /// World thread only: nothing here is synchronised. The Map-setter wake (<c>MapChanged</c>) runs on whichever thread writes <c>Map</c>, so an ungated Grid stage (World:Parallel:Ungated=Grid) would make it race with the spell tick.
    /// No cheap thread check exists here: <c>Map.EnsureWorldThread</c> needs the map's world, and a state may have no map.
    /// </summary>
    internal void WakeState(UnitSpellState state, SpellWakeReason reason)
    {
        if (state.Detached || (reason & DisabledWakeReasons) != 0)
        {
            return;
        }

        state.IsActive = true;
        if (state.InActiveList)
        {
            return;
        }

        if (!_activeIterating)
        {
            // Outside the loop the order does not matter yet: append, and merge the batch once at the next update (a mass wake stays O(k log k)).
            _pendingWakes.Add(state);
            state.InActiveList = true;
            return;
        }

        int low = 0;
        int high = _active.Count;
        while (low < high)
        {
            int mid = (low + high) >>> 1;
            if (_active[mid].Seq < state.Seq)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        _active.Insert(low, state);
        state.InActiveList = true;
        if (_activeIterating && low <= _activeCursor)
        {
            _activeCursor++; // keep the loop on the element it is updating
        }
    }

    /// <summary>Merge <c>_pendingWakes</c> into the Seq-sorted <c>_active</c>: sort the batch, then merge from the back (touches only the elements that move).</summary>
    private void FlushPendingWakes()
    {
        int k = _pendingWakes.Count;
        if (k == 0)
        {
            return;
        }

        _pendingWakes.Sort(BySeq);
        int oldCount = _active.Count;
        System.Runtime.InteropServices.CollectionsMarshal.SetCount(_active, oldCount + k);
        Span<UnitSpellState> active = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_active);
        int i = oldCount - 1;
        int j = k - 1;
        int w = oldCount + k - 1;
        while (j >= 0)
        {
            active[w--] = i >= 0 && active[i].Seq > _pendingWakes[j].Seq ? active[i--] : _pendingWakes[j--];
        }

        _pendingWakes.Clear();
    }

    /// <summary>The state is no longer the one in <c>_states</c> for its guid: stop waking it, and let compaction drop it from the active list.</summary>
    private void Detach(UnitSpellState state)
    {
        if (state.Detached)
        {
            return;
        }

        state.Detached = true;
        state.IsActive = false;
        state.DetachedTick = _updateTick;
        if (ReferenceEquals(state.Unit.SpellState, state))
        {
            state.Unit.SpellState = null;
        }

        _allDetached++;
        if (_allDetached > 64 && _allDetached * 8 > _all.Count)
        {
            // Keep the audit's place: resetting it to 0 would starve the tail of the list when states churn faster than one rotation.
            int position = Math.Min(_auditPosition, _all.Count);
            int keptBefore = 0;
            for (int i = 0; i < position; i++)
            {
                if (!_all[i].Detached)
                {
                    keptBefore++;
                }
            }

            _all.RemoveAll(IsDetachedState);
            _allDetached = 0;
            _auditPosition = keptBefore;
        }
    }

    /// <summary>Remove <paramref name="state"/> from <c>_states</c> (when it is still the state for its guid) and detach it.</summary>
    private void DropState(UnitSpellState state)
    {
        if (_states.TryGetValue(state.Unit.Guid, out UnitSpellState? current) && ReferenceEquals(current, state))
        {
            _states.Remove(state.Unit.Guid);
        }

        Detach(state);
    }

    private enum DormancyBlocker
    {
        None,
        NotInWorld,
        QuestSettlementPending,
        CurrentCast,
        AutoRepeatCast,
        MeleeCast,
        PendingChannelReset,
        Cooldowns,
        SchoolLockouts,
        TickingHolder,
        AreaSource,
        ChannelFields,
        IdleRemovalPending,
    }

    /// <summary>
    /// Whether <paramref name="state"/> has nothing to do at the next tick, so its visit can be skipped (read-only, allocation-free). True only
    /// when the unit is in the world and not settling a quest, has no cast of any kind, no pending channel reset, no cooldown or lockout, no
    /// holder that needs a tick (<see cref="AnyTickingHolder"/>), no live area-aura source (<c>UpdateAreaAuras</c> must keep running for the
    /// party auras a non-ticking permanent holder emits), no channel field the invariant check would heal or report, and the state is not
    /// <c>IsIdle</c> (an empty state must be visited once so it is dropped). Diminishing returns are not a reason: they are computed lazily
    /// at query time (DiminishingTracker), nothing ticks them.
    /// </summary>
    internal bool IsDormant(UnitSpellState state) => FirstDormancyBlocker(state) == DormancyBlocker.None;

    private DormancyBlocker FirstDormancyBlocker(UnitSpellState state)
    {
        Unit unit = state.Unit;
        if (!unit.IsInWorld)
        {
            return DormancyBlocker.NotInWorld;
        }

        if (IsQuestSettlementPending(unit))
        {
            return DormancyBlocker.QuestSettlementPending;
        }

        if (state.CurrentCast is not null)
        {
            return DormancyBlocker.CurrentCast;
        }

        if (state.AutoRepeatCast is not null)
        {
            return DormancyBlocker.AutoRepeatCast;
        }

        if (state.MeleeCast is not null)
        {
            return DormancyBlocker.MeleeCast;
        }

        if (state.PendingChannelResetMs != 0)
        {
            return DormancyBlocker.PendingChannelReset;
        }

        if (state.SpellCooldowns.Count != 0 || state.CategoryCooldowns.Count != 0 || state.GlobalCooldowns.Count != 0)
        {
            return DormancyBlocker.Cooldowns;
        }

        if (state.SchoolLockouts.Count != 0)
        {
            return DormancyBlocker.SchoolLockouts;
        }

        if (AnyTickingHolder(state))
        {
            return DormancyBlocker.TickingHolder;
        }

        WakingHolderList holders = state.Auras;
        for (int i = 0; i < holders.Count; i++)
        {
            if (holders[i] is { IsRemoved: false, IsAreaSource: true })
            {
                return DormancyBlocker.AreaSource;
            }
        }

        if (unit.GetUInt32(UpdateFields.UnitChannelSpell) != 0 || unit.GetUInt64(UpdateFields.UnitFieldChannelObject) != 0)
        {
            return DormancyBlocker.ChannelFields;
        }

        return state.IsIdle ? DormancyBlocker.IdleRemovalPending : DormancyBlocker.None;
    }

    /// <summary>The visit-every-state loop <see cref="Update"/> ran before the active set (kept as the <see cref="ActiveSetEnabled"/> = false path); it visits in Dictionary enumeration order, which is creation order only until the first removal.</summary>
    private int UpdateAllStates(uint diffMs, uint now)
    {
        int visited = 0;
        int stateCount = _states.Count;
        UnitSpellState[] states = System.Buffers.ArrayPool<UnitSpellState>.Shared.Rent(stateCount);
        _states.Values.CopyTo(states, 0);
        try
        {
            for (int i = 0; i < stateCount; i++)
            {
                try
                {
                    visited++;
                    UpdateState(states[i], diffMs, now);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("spell.update", ex);
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<UnitSpellState>.Shared.Return(states, clearArray: true);
        }

        return visited;
    }

    /// <summary>Visit the active states in creation-sequence (<see cref="UnitSpellState.Seq"/>) order, then retire the ones that proved dormant.</summary>
    private int UpdateActiveStates(uint diffMs, uint now)
    {
        int visited = 0;
        FlushPendingWakes();
        long seqLimit = _nextStateSeq; // states created during this loop are not visited this tick (the old snapshot did not see them either)
        long tick = ++_updateTick;
        _activeIterating = true;
        try
        {
            for (_activeCursor = 0; _activeCursor < _active.Count; _activeCursor++)
            {
                UnitSpellState s = _active[_activeCursor];
                if (s.Seq >= seqLimit)
                {
                    continue;
                }

                // A state detached by an earlier visit of this same loop is still visited, as the old snapshot visited it.
                if (!s.IsActive && !(s.Detached && s.DetachedTick == tick))
                {
                    continue;
                }

                try
                {
                    visited++;
                    UpdateState(s, diffMs, now);
                    if (!s.Detached && IsDormant(s))
                    {
                        s.IsActive = false;
                        s.HasTickingHolders = false; // dormant implies no ticking holder; the last visit may have computed it before a holder left
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("spell.update", ex); // a state whose update threw stays active
                }
            }
        }
        finally
        {
            _activeIterating = false;
            _activeCursor = -1;
            _active.RemoveAll(LeavesActiveList);
        }

        return visited;
    }

    private void PublishStateCounters(int visited, int active)
    {
        LastStatesVisited = visited;
        LastStatesTracked = _states.Count;
        LastStatesActive = active;
        StatesVisitedGauge.Set(visited);
        StatesActiveGauge.Set(active);
    }

    /// <summary>
    /// The safety net for a missed wake: re-check up to <see cref="ActiveSetAuditBudget"/> entries of the creation-ordered list, continuing
    /// where the last tick stopped. A dormant state that is not dormant is reported (<c>spell/active-set-stale</c>), counted, and woken.
    /// </summary>
    private void AuditDormantStates()
    {
        int budget = ActiveSetAuditBudget;
        if (budget <= 0 || _all.Count == 0)
        {
            return;
        }

        int examine = Math.Min(budget, _all.Count);
        InvalidOperationException? failure = null;
        for (int n = 0; n < examine; n++)
        {
            if (_auditPosition >= _all.Count)
            {
                _auditPosition = 0;
            }

            UnitSpellState s = _all[_auditPosition++];
            if (s.IsActive || s.Detached)
            {
                continue;
            }

            DormancyBlocker blocker = FirstDormancyBlocker(s);

            // A quest settlement can begin on a dormant player; UpdateState would only return early for it, so it is not a missed wake.
            if (blocker is DormancyBlocker.None or DormancyBlocker.QuestSettlementPending)
            {
                continue;
            }

            ActiveSetStaleHeals++;
            string detail = $"dormant state failed its dormancy check: clause={blocker}";
            AnomalyLedger.Shared.Report("spell", "active-set-stale", FormatUnitSubject(s.Unit), detail, AnomalySeverity.Warning);
            Invariant.Check(false, $"Unit {s.Unit.Guid} was dormant in the spell active set but is not ({detail}); a wake site is missing.");
            _logger.LogWarning("spell active set healed for unit {Guid}: dormant state failed clause {Clause}", s.Unit.Guid, blocker);
            s.Wake(SpellWakeReason.AuditHeal);
            failure ??= new InvalidOperationException($"spell active set stale: unit {s.Unit.Guid} {detail}");
        }

        if (failure is not null && ActiveSetAuditThrows)
        {
            throw failure;
        }
    }
}

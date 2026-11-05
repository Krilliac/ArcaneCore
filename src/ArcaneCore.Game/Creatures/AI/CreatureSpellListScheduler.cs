using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures.AI;

/// <summary>
/// What a <see cref="CreatureSpellListScheduler{TTarget}"/> asks of the creature it runs for. Every legality question (mana, range, line of
/// sight, silence, an active cast) stays with the spell system behind this seam; the scheduler never decides one itself.
/// Called on the creature's map thread only.
/// </summary>
/// <typeparam name="TTarget">The target type (a <c>Unit</c> in the world, a plain marker in tests).</typeparam>
public interface ICreatureSpellListHost<TTarget>
    where TTarget : class
{
    /// <summary>The creature is alive and in combat.</summary>
    bool IsInCombat { get; }

    /// <summary>The creature may start a spell this tick (cmangos UnitAI::CanCastSpell: not silenced for it, no non-melee cast running, not retreating).</summary>
    bool CanCastSpell { get; }

    /// <summary>The creature currently fights at range (the ranged chance is not reduced).</summary>
    bool InRangedMode { get; }

    /// <summary>The spell is off cooldown for the creature.</summary>
    bool IsSpellReady(uint spellId);

    /// <summary>
    /// Whether the combat condition an entry names holds for the creature and the spell's range (cmangos ObjectMgr::IsCombatConditionSatisfied).
    /// Asked only for entries whose <see cref="CreatureSpellListEntry.HasCombatCondition"/> is true; false skips the entry.
    /// </summary>
    bool IsCombatConditionSatisfied(int combatCondition, uint spellId);

    /// <summary>Resolve a target id of the list (<see cref="CreatureSpellListTargets"/> or a targeting row). False: no valid target, skip the entry. A true result with a null target means a self-cast or an untargeted spell.</summary>
    bool TryChooseTarget(uint targetId, uint spellId, out TTarget? target);

    /// <summary>Start the cast. False when the spell system refused it; the scheduler then treats the entry as not cast.</summary>
    bool TryCast(uint spellId, TTarget? target);

    /// <summary>Called after a successful cast (the relay script an entry names runs here).</summary>
    void OnCast(CreatureSpellListEntry entry, TTarget? target);
}

/// <summary>
/// The combat spell scheduler for one creature (cmangos UnitAI::UpdateSpellLists, UnitAI.cpp:1181-1288, re-expressed). Every
/// <see cref="CreatureSpellList.TickMs"/> in combat it rolls the support and ranged gates once, collects the entries that pass their
/// gates (disabled-for-AI, support chance, combat condition, ranged chance), are off cooldown and have a target, casts the non-blocking
/// ones, then casts one blocking entry chosen by probability weight (lowest position first when every weight is zero) and falls back to
/// the next one when a cast is refused.
/// <para>
/// Deliberate deviation: this scheduler stops after the first successful blocking cast. The reference keeps iterating its inner loop
/// after a success (it ends the outer loop only afterwards), so an instant blocking spell could fire a second entry there unless
/// something else, such as a global cooldown, stops it (not traced). Here a tick casts one blocking entry, following the spec ("choose
/// one eligible blocking spell") and cmangos doc/SpellLists.txt ("always one spell is selected").
/// </para>
/// <para>
/// Not modelled: the cmangos cooldown map keyed per unit and spell. Cooldowns here are per entry index, so two entries naming the same
/// spell do not share one, and <see cref="Bind"/> forgets them. The <see cref="CreatureSpellListFlags.CategoryCooldown"/> flag is not read;
/// the host reads it from the entry in <see cref="ICreatureSpellListHost{TTarget}.OnCast"/> when it applies the spell system cooldown.
/// </para>
/// <para>
/// Ownership: the instance owns only its cooldown clock and the entries an availability roll kept; the list it was bound to is immutable
/// content. Not thread safe: the creature's map thread drives it, and the <see cref="Random"/> it is given must not be shared across map
/// threads (a per-map instance or <see cref="Random.Shared"/>). A tick that finds nothing eligible allocates nothing.
/// </para>
/// </summary>
public sealed class CreatureSpellListScheduler<TTarget>
    where TTarget : class
{
    private readonly ICreatureSpellListHost<TTarget> _host;
    private readonly Random _random;
    private CreatureSpellList? _list;
    private CreatureSpellListEntry[] _active = [];
    private ulong[] _readyAtMs = [];
    private ulong _nowMs;
    private uint _tickRemainingMs;

    public CreatureSpellListScheduler(ICreatureSpellListHost<TTarget> host, Random random)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(random);
        _host = host;
        _random = random;
    }

    /// <summary>The list this scheduler runs, or null.</summary>
    public CreatureSpellList? List => _list;

    /// <summary>The entries this assignment kept after the availability roll, in position order.</summary>
    public IReadOnlyList<CreatureSpellListEntry> ActiveEntries => _active;

    /// <summary>Whether there is anything to schedule.</summary>
    public bool HasSpells => _active.Length > 0;

    /// <summary>
    /// Assign a list (on spawn, respawn or an explicit set-list). Each entry below availability 100 is kept only when a roll of 0..100 does
    /// not exceed its availability (cmangos Creature.cpp:2447-2450). Clears every cooldown. A null list clears the assignment, and so does a
    /// <see cref="CreatureSpellList.Disabled"/> list (<see cref="List"/> still reports it, but nothing is scheduled).
    /// <para>
    /// Binding does not arm initial cooldowns. A set-list in the middle of a fight must be followed by <see cref="EnterCombat"/> by the
    /// caller (cmangos UnitAI::SpellListChanged calls AddInitialCooldowns while in combat), or the new entries cast at once.
    /// </para>
    /// </summary>
    public void Bind(CreatureSpellList? list)
    {
        _list = list;
        if (list is null || list.Disabled)
        {
            _active = [];
        }
        else
        {
            List<CreatureSpellListEntry> kept = new(list.Entries.Count);
            foreach (CreatureSpellListEntry entry in list.Entries)
            {
                if (entry.Availability < 100 && _random.Next(0, 101) > (int)entry.Availability) continue;
                kept.Add(entry);
            }

            _active = [.. kept];
        }

        _readyAtMs = new ulong[_active.Length];
        _tickRemainingMs = 0;
    }

    /// <summary>Combat starts: every kept entry gets a random initial cooldown in [InitialMin, InitialMax] and the tick loop restarts (cmangos UnitAI::AddInitialCooldowns).</summary>
    public void EnterCombat()
    {
        _tickRemainingMs = 0;
        for (int i = 0; i < _active.Length; i++)
            _readyAtMs[i] = _nowMs + RollRange(_active[i].InitialMin, _active[i].InitialMax);
    }

    /// <summary>The creature evaded, died or respawned: forget every cooldown.</summary>
    public void Reset()
    {
        Array.Clear(_readyAtMs);
        _tickRemainingMs = 0;
    }

    /// <summary>Advance the clock by <paramref name="diffMs"/> and run a tick when the 1200 ms loop is due. True when a tick ran.</summary>
    public bool Update(uint diffMs)
    {
        _nowMs += diffMs;
        if (_active.Length == 0) return false;
        if (_tickRemainingMs > diffMs)
        {
            _tickRemainingMs -= diffMs;
            return false;
        }

        _tickRemainingMs = CreatureSpellList.TickMs;
        Tick();
        return true;
    }

    private void Tick()
    {
        if (_list is null || !_host.IsInCombat || !_host.CanCastSpell) return;

        List<Candidate>? blocking = null;
        List<Candidate>? nonBlocking = null;
        long sum = 0;

        // One roll per gate for the whole tick, so every entry of that kind passes or fails together.
        int supportRoll = _random.Next(0, 101);
        int rangedRoll = _random.Next(0, 101);
        int rangedChance = _host.InRangedMode
            ? (int)_list.ChanceRangedAttack
            : Math.Max((int)_list.ChanceRangedAttack - (int)CreatureSpellList.MeleeModeRangedPenalty, 0);

        for (int i = 0; i < _active.Length; i++)
        {
            CreatureSpellListEntry entry = _active[i];
            if (entry.DisabledForAI) continue;
            if ((entry.Flags & CreatureSpellListFlags.SupportAction) != 0 && supportRoll > (int)_list.ChanceSupportAction) continue;
            if (entry.HasCombatCondition && !_host.IsCombatConditionSatisfied(entry.CombatCondition, entry.SpellId)) continue;
            if ((entry.Flags & CreatureSpellListFlags.RangedAction) != 0 && rangedRoll > rangedChance) continue;
            if (_readyAtMs[i] > _nowMs || !_host.IsSpellReady(entry.SpellId)) continue;
            if (!_host.TryChooseTarget(entry.TargetId, entry.SpellId, out TTarget? target)) continue;

            Candidate candidate = new(i, target);
            if ((entry.Flags & CreatureSpellListFlags.NonBlocking) != 0)
            {
                (nonBlocking ??= []).Add(candidate);
            }
            else
            {
                (blocking ??= []).Add(candidate);
                sum += entry.Probability;
            }
        }

        if (nonBlocking is not null)
        {
            foreach (Candidate candidate in nonBlocking)
                Execute(candidate);
        }

        if (blocking is null) return;

        // A zero total means priority by position: the entries are already in position order and the roll of -1 selects the first one.
        if (blocking.Count > 1 && sum != 0) Shuffle(blocking);
        long roll = sum == 0 ? -1 : _random.NextInt64(0, sum);
        while (blocking.Count > 0)
        {
            for (int k = 0; k < blocking.Count;)
            {
                Candidate candidate = blocking[k];
                uint probability = _active[candidate.Index].Probability;
                if (roll < probability)
                {
                    bool cast = Execute(candidate);
                    blocking.RemoveAt(k);
                    if (cast) return;
                }
                else
                {
                    k++;
                }

                roll -= probability;
            }
        }
    }

    private bool Execute(Candidate candidate)
    {
        CreatureSpellListEntry entry = _active[candidate.Index];
        if (!_host.TryCast(entry.SpellId, candidate.Target)) return false;
        _readyAtMs[candidate.Index] = _nowMs + RollRange(entry.RepeatMin, entry.RepeatMax);
        _host.OnCast(entry, candidate.Target);
        return true;
    }

    private uint RollRange(uint min, uint max) => min >= max ? min : (uint)_random.NextInt64(min, (long)max + 1);

    private void Shuffle(List<Candidate> items)
    {
        for (int i = items.Count - 1; i > 0; i--)
        {
            int j = _random.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    private readonly record struct Candidate(int Index, TTarget? Target);
}

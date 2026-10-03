using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Stealth;

/// <summary>
/// vmangos VisibilityNotify group of a unit (Unit::m_visibility): <see cref="On"/> is visible to everyone,
/// <see cref="Stealth"/> is hidden unless detected, <see cref="NoDetect"/> is the transient state in which a unit that just
/// entered stealth ignores every previously detected state (Unit.cpp:6410-6412).
/// </summary>
public enum StealthVisibility
{
    On,
    Stealth,
    NoDetect,
}

/// <summary>
/// Which units are currently stealthed and in which visibility group. Stealth is volatile, exactly as in vmangos: it lives in
/// the aura (which survives logout through the existing character_aura persistence, a stealth spell carries no LEAVE_WORLD
/// interrupt flag) and this table is rebuilt by the aura handler when the aura is restored.
/// </summary>
public sealed class StealthRegistry
{
    private readonly ConditionalWeakTable<Unit, StrongBox> _states = new();
    private readonly HashSet<Unit> _stealthed = new(ReferenceEqualityComparer.Instance);
    private int _hidden;

    private sealed class StrongBox
    {
        public StealthVisibility Visibility;
    }

    /// <summary>Whether any unit is in a non-<see cref="StealthVisibility.On"/> group (the visibility rule skips its lookups when none is).</summary>
    public bool AnyHidden => _hidden > 0;

    /// <summary>Number of units in the <see cref="StealthVisibility.Stealth"/> group (the detection pass is skipped when 0).</summary>
    public int Count => _stealthed.Count;

    public StealthVisibility VisibilityOf(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return _states.TryGetValue(unit, out StrongBox? box) ? box.Visibility : StealthVisibility.On;
    }

    /// <summary>Whether <paramref name="unit"/> is hidden from viewers that have not detected it (any non-<see cref="StealthVisibility.On"/> group).</summary>
    public bool IsStealthed(Unit unit) => VisibilityOf(unit) != StealthVisibility.On;

    public void SetVisibility(Unit unit, StealthVisibility visibility)
    {
        ArgumentNullException.ThrowIfNull(unit);
        StrongBox box = _states.GetValue(unit, static _ => new StrongBox());
        if (box.Visibility == StealthVisibility.On && visibility != StealthVisibility.On)
        {
            _hidden++;
        }
        else if (box.Visibility != StealthVisibility.On && visibility == StealthVisibility.On)
        {
            _hidden--;
        }

        box.Visibility = visibility;
        if (visibility == StealthVisibility.Stealth)
        {
            _stealthed.Add(unit);
        }
        else
        {
            _stealthed.Remove(unit);
        }
    }

    /// <summary>A snapshot of the units in the Stealth group (vmangos AnyStealthedCheck: VISIBILITY_GROUP_STEALTH).</summary>
    public IReadOnlyList<Unit> Stealthed() => [.. _stealthed];

    /// <summary>Forget units that left the world for good (their auras were dropped without handlers, e.g. at logout): they go back to the On group.</summary>
    public void Prune(Func<Unit, bool> isGone)
    {
        ArgumentNullException.ThrowIfNull(isGone);
        if (_stealthed.Count == 0)
        {
            return;
        }

        foreach (Unit gone in _stealthed.Where(u => isGone(u)).ToArray())
        {
            SetVisibility(gone, StealthVisibility.On);
        }
    }
}

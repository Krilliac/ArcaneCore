using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Instances.Scripts;

/// <summary>World-tick implementation of ScriptDevAI CombatAI's success-reset combat actions.
/// Reference: mangos-classic AI/ScriptDevAI/base/CombatAI.cpp, UpdateAI/ExecuteActions.</summary>
public abstract class RaidCreatureAI(Creature creature, ScriptedInstance instance, uint? encounter) : CreatureAI(creature)
{
    protected sealed class ActionTimer(uint delay, Func<bool> action, Func<uint> repeat)
    {
        public uint Remaining = delay;
        public Func<bool> Action = action;
        public Func<uint> Repeat = repeat;
    }

    private readonly List<ActionTimer> _actions = [];
    protected ScriptedInstance Raid { get; } = instance;
    public override bool AggroesOnSight => true;
    protected uint Random(uint min, uint max) => (uint)(System?.RandomInt((int)min, (int)max) ?? (int)min);
    protected bool Below(uint percent) => (ulong)Me.Health * 100 <= (ulong)Me.MaxHealth * percent;
    protected void Say(int text) => System?.SayText(Me, text);
    protected bool Cast(uint spell, Unit? target = null, bool triggered = false)
        => DoCast(target ?? Me, spell, triggered) == CreatureCastResult.Ok;
    protected Unit? RandomTarget(Func<Unit, bool>? predicate = null)
    {
        Unit[] targets = Me.Combat.Threat.Entries.Select(e => e.Target)
            .Where(u => u.IsAlive && ReferenceEquals(u.Map, Me.Map) && (predicate?.Invoke(u) ?? true)).ToArray();
        return targets.Length == 0 ? null : targets[Random(0, (uint)targets.Length - 1)];
    }
    protected Unit? Friendly(float range, Func<Creature, bool> predicate, bool random = false)
    {
        Creature[] candidates = System?.Creatures.Where(c => c.IsAlive && c.FactionTemplate == Me.FactionTemplate
            && DistanceSquared(c, Me) <= range * range && predicate(c)).ToArray() ?? [];
        if (candidates.Length == 0) return null;
        // UnitAI::DoSelectLowestHpFriendly defaults to absolute missing HP, not percentage.
        return random ? candidates[Random(0, (uint)candidates.Length - 1)]
            : candidates.OrderByDescending(c => c.MaxHealth - Math.Min(c.Health, c.MaxHealth)).First();
    }
    protected static float DistanceSquared(Unit a, Unit b)
        => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z);
    protected void Schedule(uint initialMin, uint initialMax, uint repeatMin, uint repeatMax, Func<bool> action)
        => _actions.Add(new ActionTimer(Random(initialMin, initialMax), action, () => Random(repeatMin, repeatMax)));
    protected void Spell(uint id, uint initialMin, uint initialMax, uint repeatMin, uint repeatMax, Func<Unit?>? target = null)
        => Schedule(initialMin, initialMax, repeatMin, repeatMax, () =>
        {
            Unit? selected = target is null ? Me : target();
            return selected is not null && Cast(id, selected);
        });
    protected void ClearActions() => _actions.Clear();
    // cmangos UnitAI::SetMeleeEnabled: update the host's active swing as well as future AttackStart calls.
    protected void SetMeleeEnabled(bool enabled)
    {
        MeleeEnabled = enabled;
        if (Victim is { } victim) System?.SetMelee(Me, victim, enabled);
    }
    protected virtual void Reset()
    {
        ClearActions();
        CombatMovement = true;
        SetMeleeEnabled(Me.MeleeAllowedByTemplate);
    }
    public override void OnRespawn() => Reset();
    public override void OnEvade()
    {
        Reset();
        if (encounter is { } type && Raid.GetData(type) is not (EncounterState.Done or EncounterState.Special))
            Raid.SetData(type, EncounterState.Fail);
    }
    public override void OnAggro(Unit target)
    {
        if (encounter is { } type) Raid.SetData(type, EncounterState.InProgress);
        if (encounter is not null) System?.SetInCombatWithZone(Me);
    }
    public override void OnDeath(Unit? killer)
    {
        if (encounter is { } type) Raid.SetData(type, EncounterState.Done);
    }
    public override void OnUpdate(uint diffMs)
    {
        if (!Me.Combat.IsInCombat || !UpdateVictim()) return;
        TickActions(diffMs);
    }
    protected void TickActions(uint diffMs)
    {
        foreach (ActionTimer timer in _actions)
        {
            if (timer.Remaining > diffMs) timer.Remaining -= diffMs;
            else
            {
                timer.Remaining = 0;
                if (timer.Action()) timer.Remaining = timer.Repeat();
            }
        }
    }
    protected static bool Due(ref uint timer, uint diffMs)
    {
        if (timer > diffMs) { timer -= diffMs; return false; }
        timer = 0;
        return true;
    }
}

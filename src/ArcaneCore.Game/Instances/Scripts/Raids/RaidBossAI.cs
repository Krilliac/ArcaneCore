using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.BlackwingLair;
using ArcaneCore.Game.Instances.Scripts.RuinsOfAhnQiraj;
using ArcaneCore.Game.Instances.Scripts.Classic;
using ArcaneCore.Game.Instances.Scripts.ZulGurub;
using ArcaneCore.Game.Pets;

namespace ArcaneCore.Game.Instances.Scripts.Raids;

/// <summary>
/// The combat-action subset of mangos-classic AI/ScriptDevAI/base/CombatAI.cpp:
/// combat-only countdowns, reset on evade/respawn, and retry a ready action until its cast succeeds.
/// Melee and victim selection remain with CreatureMapSystem.
/// </summary>
public abstract class RaidBossAI(Creature creature, uint? encounter) : AggressorAI(creature)
{
    private sealed class ActionTimer(Func<uint> initial, Func<bool> execute, Func<uint> repeat)
    {
        public uint Remaining = initial();
        public Func<uint> Initial { get; } = initial;
        public Func<bool> Execute { get; } = execute;
        public Func<uint> Repeat { get; } = repeat;
    }

    private readonly List<ActionTimer> _actions = [];
    protected InstanceData? Instance => Me.Map?.FindUpdater<InstanceData>();

    // vmangos CreatureAISelector.cpp selectAI: script before template AIName, never a controlled unit.
    public static CreatureAI? Create(Creature creature)
    {
        if (!creature.CharmerGuid.IsEmpty || creature.Summon is { Kind: Pets.SummonKind.Pet })
        {
            return null;
        }

        return (creature.Map?.FindUpdater<InstanceData>(), creature.Template.Entry) switch
        {
            (BlackwingLairInstance, 12017) => new BroodlordAI(creature),
            (BlackwingLairInstance, 11983) => new FiremawAI(creature),
            (BlackwingLairInstance, 11981) => new FlamegorAI(creature),
            (RuinsOfAhnQirajInstance, 15348) => new KurinnaxxAI(creature),
            (RuinsOfAhnQirajInstance, 15341) => new RajaxxAI(creature),
            (RuinsOfAhnQirajInstance, 15340) => new MoamAI(creature),
            (RuinsOfAhnQirajInstance, 15370) => new BuruAI(creature),
            (RuinsOfAhnQirajInstance, 15369) => new AyamissAI(creature),
            (RuinsOfAhnQirajInstance, 15339) => new OssirianAI(creature),
            (RuinsOfAhnQirajInstance, 15514) => new BuruEggAI(creature),
            (RuinsOfAhnQirajInstance, 15471) => new AndorovAI(creature),
            (RuinsOfAhnQirajInstance, 15473) => new KaldoreiEliteAI(creature),
            (ZulGurubInstance, 14517) => new JeklikAI(creature),
            (ZulGurubInstance, 14507) => new VenoxisAI(creature),
            (ZulGurubInstance, 14510) => new MarliAI(creature),
            (ZulGurubInstance, 14509) => new ThekalAI(creature),
            (ZulGurubInstance, 11347) => new LorKhanAI(creature),
            (ZulGurubInstance, 11348) => new ZathAI(creature),
            (ZulGurubInstance, 14515) => new ArlokkAI(creature),
            (ZulGurubInstance, 11380) => new JindoAI(creature),
            (ZulGurubInstance, 11382) => new MandokirAI(creature),
            (ZulGurubInstance, 15114) => new GahzrankaAI(creature),
            (ZulGurubInstance, 15082) => new GrilekAI(creature),
            (ZulGurubInstance, 15083) => new HazzarahAI(creature),
            (ZulGurubInstance, 15084) => new RenatakiAI(creature),
            (ZulGurubInstance, 15085) => new WushoolayAI(creature),
            (ZulGurubInstance, 14834) => new HakkarAI(creature),
            _ => null,
        };
    }

    protected uint RandomDelay(int min, int max) => (uint)(System?.RandomInt(min, max) ?? min);

    protected void AddAction(uint initial, Func<bool> execute, Func<uint> repeat)
        => _actions.Add(new ActionTimer(() => initial, execute, repeat));

    /// <summary>SD2 CombatAI AddCombatAction(action, min, max): the first delay is rolled again on every reset.</summary>
    protected void AddAction(int initialMin, int initialMax, Func<bool> execute, Func<uint> repeat)
        => _actions.Add(new ActionTimer(() => RandomDelay(initialMin, initialMax), execute, repeat));

    protected bool Cast(uint spell, Unit? target = null, bool triggered = false)
        => DoCast(target, spell, triggered) == CreatureCastResult.Ok;

    protected bool Below(uint percent) => (ulong)Me.Health * 100 <= (ulong)Me.MaxHealth * percent;

    protected Unit? RandomTarget()
    {
        Unit[] targets = [.. Me.Combat.Threat.Entries.Select(e => e.Target)
            .Where(t => t.IsAlive && t.IsInWorld && ReferenceEquals(t.Map, Me.Map))];
        return targets.Length == 0 ? null : targets[System!.RandomInt(0, targets.Length - 1)];
    }

    protected void ResetThreat()
    {
        foreach (var entry in Me.Combat.Threat.Entries.ToArray())
            Me.Combat.Threat.ModifyThreatPercent(entry.Target, -100);
    }

    public override void OnAggro(Unit target)
    {
        if (encounter is { } slot) Instance?.SetData(slot, EncounterState.InProgress);
    }

    public override void OnDeath(Unit? killer)
    {
        if (encounter is { } slot) Instance?.SetData(slot, EncounterState.Done);
    }

    public override void OnReachedHome()
    {
        if (encounter is { } slot) Instance?.SetData(slot, EncounterState.Fail);
    }

    public override void OnEvade() => ResetActions();

    public override void OnRespawn() => ResetActions();

    protected virtual void ResetActions()
    {
        foreach (ActionTimer action in _actions)
        {
            action.Remaining = action.Initial();
        }
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!UpdateVictim())
        {
            return;
        }

        UpdateCombat(diffMs);
    }

    protected virtual void UpdateCombat(uint diffMs)
    {
        foreach (ActionTimer action in _actions)
        {
            action.Remaining = action.Remaining > diffMs ? action.Remaining - diffMs : 0;
            if (action.Remaining == 0 && action.Execute())
            {
                action.Remaining = action.Repeat();
            }
        }
    }
}

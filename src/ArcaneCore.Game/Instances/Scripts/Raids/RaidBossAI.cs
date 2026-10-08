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
public abstract class RaidBossAI(Creature creature, uint encounter) : AggressorAI(creature)
{
    private sealed class ActionTimer(uint initial, Func<bool> execute, Func<uint> repeat)
    {
        public uint Remaining = initial;
        public uint Initial { get; } = initial;
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
            (ZulGurubInstance, 14834) => new HakkarAI(creature),
            _ => null,
        };
    }

    protected uint RandomDelay(int min, int max) => (uint)(System?.RandomInt(min, max) ?? min);

    protected void AddAction(uint initial, Func<bool> execute, Func<uint> repeat)
        => _actions.Add(new ActionTimer(initial, execute, repeat));

    protected bool Cast(uint spell, Unit? target = null, bool triggered = false)
        => DoCast(target, spell, triggered) == CreatureCastResult.Ok;

    public override void OnAggro(Unit target) => Instance?.SetData(encounter, EncounterState.InProgress);

    public override void OnDeath(Unit? killer) => Instance?.SetData(encounter, EncounterState.Done);

    public override void OnReachedHome() => Instance?.SetData(encounter, EncounterState.Fail);

    public override void OnEvade() => ResetActions();

    public override void OnRespawn() => ResetActions();

    protected virtual void ResetActions()
    {
        foreach (ActionTimer action in _actions)
        {
            action.Remaining = action.Initial;
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

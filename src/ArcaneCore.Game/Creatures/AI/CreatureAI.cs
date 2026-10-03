using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// The scriptable creature AI base (vmangos CreatureAI / cmangos ScriptedAI). One instance
/// drives one creature; the creature's <see cref="CreatureMapSystem"/> calls the hooks on the
/// world thread during its map update:
/// <list type="bullet">
/// <item><see cref="OnAggro"/> once when the creature enters combat (vmangos EnterCombat/Aggro).</item>
/// <item><see cref="OnUpdate"/> every tick while alive (vmangos UpdateAI).</item>
/// <item><see cref="OnDeath"/> when it dies (JustDied), <see cref="OnKilledUnit"/> when it kills (KilledUnit).</item>
/// <item><see cref="OnSpellHit"/> when a spell lands on it (SpellHit).</item>
/// <item><see cref="OnEvade"/> when it leaves combat to go home (EnterEvadeMode), <see cref="OnReachedHome"/> on arrival.</item>
/// </list>
/// The protected helpers start attacks, choose victims (threat list), evade, move and cast
/// through the map system so derived scripts never touch combat or spell internals directly.
/// Register a script under an <c>AIName</c> with <see cref="CreatureAiFactory.Register"/>.
/// </summary>
public abstract class CreatureAI
{
    protected CreatureAI(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        Me = creature;
    }

    /// <summary>The creature this AI drives.</summary>
    public Creature Me { get; }

    /// <summary>The map system that owns the creature (null while it is outside a map).</summary>
    protected CreatureMapSystem? System => Me.System;

    /// <summary>The current melee victim.</summary>
    public Unit? Victim => Me.Combat.Victim;

    /// <summary>Whether this AI attacks hostile units that come into aggro range (vmangos AggressorAI).</summary>
    public virtual bool AggroesOnSight => false;

    /// <summary>Whether the creature runs after its victim (cmangos EventAI combat movement).</summary>
    public bool CombatMovement { get; set; } = true;

    /// <summary>Whether the creature swings at its victim (cmangos EventAI auto attack); false keeps it at range.</summary>
    public bool MeleeEnabled { get; protected set; } = true;

    // --- hooks ----------------------------------------------------------------------------

    /// <summary>The creature entered combat with <paramref name="target"/>.</summary>
    public virtual void OnAggro(Unit target)
    {
    }

    /// <summary>One tick while alive. The default keeps the victim chosen from the threat list and evades when none is left.</summary>
    public virtual void OnUpdate(uint diffMs) => UpdateVictim();

    public virtual void OnDeath(Unit? killer)
    {
    }

    public virtual void OnKilledUnit(Unit victim)
    {
    }

    public virtual void OnSpellHit(Unit caster, SpellInfo spell)
    {
    }

    public virtual void OnEvade()
    {
    }

    public virtual void OnReachedHome()
    {
    }

    /// <summary>Spawned or respawned (vmangos JustRespawned / Reset).</summary>
    public virtual void OnRespawn()
    {
    }

    /// <summary>A point or home movement ended (vmangos MovementInform).</summary>
    public virtual void OnMovementInform(MovementGeneratorType type, uint pointId)
    {
    }

    /// <summary>Attacked (vmangos AttackedBy): an idle creature fights back.</summary>
    public virtual void OnAttackedBy(Unit attacker)
    {
        if (Victim is null)
        {
            AttackStart(attacker);
        }
    }

    /// <summary>A unit is near (vmangos MoveInLineOfSight). Aggressive AIs attack valid hostile targets in aggro range.</summary>
    public virtual void MoveInLineOfSight(Unit who)
    {
        if (AggroesOnSight && System is { } system && system.CanAggroOnSight(Me, who))
        {
            AttackStart(who);
        }
    }

    // --- helpers --------------------------------------------------------------------------

    /// <summary>Attack <paramref name="target"/>: melee, threat, combat state, chase and the assistance call.</summary>
    public bool AttackStart(Unit target) => System?.AttackStart(Me, target) ?? false;

    /// <summary>Choose the victim from the threat list; evades and returns false when there is none (vmangos SelectHostileTarget).</summary>
    protected bool UpdateVictim() => System?.SelectHostileTarget(Me) ?? false;

    /// <summary>Leave combat: reset health and threat, run home (vmangos EnterEvadeMode).</summary>
    public void EnterEvadeMode() => System?.EnterEvadeMode(Me);

    /// <summary>Cast a spell through the world spell system (no-op result without one).</summary>
    protected CreatureCastResult DoCast(Unit? target, uint spellId, bool triggered = false)
        => System?.CastSpell(Me, spellId, target, triggered) ?? CreatureCastResult.NoSpellSystem;

    /// <summary>Make nearby same-faction creatures join the fight at once (EventAI CALL_FOR_HELP).</summary>
    protected int DoCallForHelp(float radius) => System?.CallForHelp(Me, radius) ?? 0;
}

/// <summary>vmangos NullCreatureAI: does nothing, not even fight back.</summary>
public sealed class NullCreatureAI(Creature creature) : CreatureAI(creature)
{
    public override void OnUpdate(uint diffMs)
    {
    }

    public override void OnAttackedBy(Unit attacker)
    {
    }
}

/// <summary>vmangos ReactorAI: fights back when attacked, never aggroes on sight (civilians and passive creatures).</summary>
public class ReactorAI(Creature creature) : CreatureAI(creature)
{
}

/// <summary>vmangos AggressorAI: attacks hostile units that enter its aggro radius and fights back.</summary>
public class AggressorAI(Creature creature) : CreatureAI(creature)
{
    public override bool AggroesOnSight => true;
}

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
        MeleeEnabled = creature.MeleeAllowedByTemplate; // vmangos CreatureAI::CreatureAI (AI/CreatureAI.cpp:40)
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

    /// <summary>
    /// The distance a scripted caster keeps from its victim while it chases it (vmangos Creature::SetCasterChaseDistance); 0 chases into melee
    /// reach as any creature.
    /// </summary>
    public float CasterChaseDistance { get; protected set; }

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

    /// <summary>
    /// A script's own evade (an SD2 / vmangos <c>EnterEvadeMode() override</c> that does not call the base): asked first by
    /// <see cref="CreatureMapSystem.EnterEvadeMode"/>. Return true when the script handled the evade itself; the engine's evade (interrupt,
    /// aura reset, combat stop, <see cref="OnEvade"/>, the run home) is then skipped entirely. Return false (the default) for the engine's
    /// evade - the source's <c>else Base::EnterEvadeMode()</c>. <see cref="CreatureMapSystem.StopCombatInPlace"/> is the source's
    /// <c>SetLootRecipient(nullptr); CombatStop(false); MovementExpired(true)</c> without the cast interrupt and aura reset. A nested
    /// <see cref="EnterEvadeMode"/> from inside this hook takes the engine's evade.
    /// </summary>
    public virtual bool OnEnterEvadeMode() => false;

    public virtual void OnReachedHome()
    {
    }

    /// <summary>A player aimed a text emote at the creature (vmangos CreatureAI::ReceiveEmote).</summary>
    public virtual void OnReceiveEmote(Player player, uint textEmote)
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

    /// <summary>A creature this one summoned entered the world (cmangos JustSummoned: EventAI summons and spell summons).</summary>
    public virtual void OnJustSummoned(Creature summoned)
    {
    }

    /// <summary>A creature this one summoned died (cmangos SummonedCreatureJustDied).</summary>
    public virtual void OnSummonedCreatureJustDied(Creature summoned)
    {
    }

    /// <summary>A creature this one summoned left the world (cmangos SummonedCreatureDespawn).</summary>
    public virtual void OnSummonedCreatureDespawn(Creature summoned)
    {
    }

    /// <summary>
    /// An AI event reached the creature (cmangos UnitAI::ReceiveAIEvent, AI/BaseAI/UnitAI.h:372): <paramref name="eventType"/> is a cmangos
    /// <c>AIEventType</c>, <paramref name="sender"/> the unit that sent it (a creature, or the player of a relay's SEND_AI_EVENT to a player
    /// target) and <paramref name="invoker"/> the unit it is about. Nothing by default.
    /// </summary>
    public virtual void OnReceiveAiEvent(uint eventType, Unit sender, Unit? invoker, uint miscValue)
    {
    }

    /// <summary>A spell this creature cast landed on <paramref name="target"/> (cmangos SpellHitTarget).</summary>
    public virtual void OnSpellHitTarget(Unit target, SpellInfo spell)
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

    /// <summary>
    /// A unit is near (vmangos BasicAI::MoveInLineOfSight, AI/BasicAI.cpp:49-77). Aggressive AIs attack valid hostile targets in aggro
    /// range; a CALLS_GUARDS creature that does not attack calls the guards on a hostile player within its detection range
    /// (<see cref="CreatureMapSystem.CanCallGuardsOnSight"/>, <see cref="CreatureMapSystem.SummonGuard"/>).
    /// </summary>
    public virtual void MoveInLineOfSight(Unit who)
    {
        if (System is not { } system)
        {
            return;
        }

        if (AggroesOnSight && system.CanAggroOnSight(Me, who))
        {
            system.EnterCombatWithTarget(Me, who);
        }
        else if (CallsGuardsOnSight && system.CanCallGuardsOnSight(Me, who))
        {
            Me.CanCallGuardsOnSight = !system.SummonGuard(Me, who); // vmangos BasicAI::SummonGuard (BasicAI.cpp:102-105)
        }
    }

    /// <summary>Whether this AI calls the guards on sight (vmangos BasicAI does; NullCreatureAI, CritterAI and EventAI do not).</summary>
    protected virtual bool CallsGuardsOnSight => true;

    /// <summary>
    /// A stealthed player the creature cannot see stands just outside its detection range (vmangos CreatureAI::OnMoveInStealth,
    /// AI/CreatureAI.cpp:349-353): a hostile creature that qualifies reacts with the alert (<see cref="CreatureMapSystem.TriggerAlert"/>).
    /// </summary>
    public virtual void OnMoveInStealth(Unit who)
    {
        if (System is { } system && system.CanTriggerAlert(Me, who))
        {
            system.TriggerAlert(Me, who);
        }
    }

    // --- helpers --------------------------------------------------------------------------

    /// <summary>Attack <paramref name="target"/>: melee, threat, combat state, chase and the assistance call.</summary>
    public virtual bool AttackStart(Unit target) => System?.AttackStart(Me, target) ?? false;

    /// <summary>Choose the victim from the threat list; evades and returns false when there is none (vmangos SelectHostileTarget).</summary>
    protected bool UpdateVictim() => System?.SelectHostileTarget(Me) ?? false;

    /// <summary>Leave combat: reset health and threat, run home (vmangos EnterEvadeMode).</summary>
    public void EnterEvadeMode() => System?.EnterEvadeMode(Me);

    /// <summary>Cast a spell through the world spell system (no-op result without one).</summary>
    protected CreatureCastResult DoCast(Unit? target, uint spellId, bool triggered = false)
        => System?.CastSpell(Me, spellId, target, triggered) ?? CreatureCastResult.NoSpellSystem;

    /// <summary>
    /// vmangos <c>m_creature-&gt;AddAura(spellId, permanent ? ADD_AURA_PERMANENT : 0)</c>: the spell's auras on this creature without a cast; a
    /// permanent holder never runs out, whatever the spell's duration.
    /// </summary>
    protected CreatureCastResult DoAddAura(uint spellId, bool permanent = false)
        => System?.AddAura(Me, spellId, permanent) ?? CreatureCastResult.NoSpellSystem;

    /// <summary>Make nearby same-faction creatures join the fight at once (EventAI CALL_FOR_HELP).</summary>
    protected int DoCallForHelp(float radius) => System?.CallForHelp(Me, radius) ?? 0;

    /// <summary>
    /// cmangos <c>Unit::SelectAttackingTarget(ATTACKING_TARGET_RANDOM, position[, spell, SELECT_FLAG_PLAYER])</c> (Entities/Unit.cpp): a random
    /// living unit of the threat list (highest threat first) from index <paramref name="position"/> on; position 1 skips the top-threat unit.
    /// <paramref name="playerOnly"/> is SELECT_FLAG_PLAYER. Null when the list has no more than <paramref name="position"/> entries or none fits.
    /// </summary>
    protected Unit? SelectRandomAttackingTarget(int position = 0, bool playerOnly = false)
    {
        if (!Me.Combat.HasThreatList)
        {
            return null;
        }

        IReadOnlyList<Combat.ThreatEntry> threat = Me.Combat.Threat.Entries;
        if (position >= threat.Count)
        {
            return null;
        }

        Unit[] candidates = [.. threat.Skip(position).Select(e => e.Target).Where(u => u.IsAlive && (!playerOnly || u is Player))];
        return candidates.Length == 0 ? null : candidates[System?.RandomInt(0, candidates.Length - 1) ?? 0];
    }

    /// <summary>
    /// cmangos <c>UnitAI::DoSelectLowestHpFriendly(range, minMissing, percent = false, targetSelf)</c> (AI/BaseAI/UnitAI.cpp:667-687) with
    /// MostHPMissingInRangeCheck (Grids/GridNotifiers.h:825-854): of the living, in-combat creatures within <paramref name="range"/> yards
    /// this creature can assist (itself included when <paramref name="targetSelf"/>), the one missing the most health, more than
    /// <paramref name="minMissing"/> points. Null when none does.
    /// </summary>
    protected Creature? SelectLowestHpFriendly(float range, uint minMissing = 1, bool targetSelf = true)
    {
        if (System is not { } system)
        {
            return null;
        }

        ICreatureHostility hostility = system.AiServices.Hostility;
        float rangeSquared = range * range;
        Creature? best = null;
        uint bestMissing = minMissing;
        foreach (Creature other in system.Creatures)
        {
            if (!other.IsAlive || !other.Combat.IsInCombat || (!targetSelf && ReferenceEquals(other, Me)))
            {
                continue;
            }

            float dx = other.X - Me.X, dy = other.Y - Me.Y, dz = other.Z - Me.Z;
            if (dx * dx + dy * dy + dz * dz > rangeSquared
                || (!ReferenceEquals(other, Me) && !hostility.CanAssist(other, Me) && !hostility.IsFriendly(Me, other)))
            {
                continue;
            }

            uint missing = other.MaxHealth - Math.Min(other.Health, other.MaxHealth);
            if (missing > bestMissing)
            {
                best = other;
                bestMissing = missing;
            }
        }

        return best;
    }
}

/// <summary>vmangos NullCreatureAI: does nothing, not even fight back.</summary>
public sealed class NullCreatureAI(Creature creature) : CreatureAI(creature)
{
    protected override bool CallsGuardsOnSight => false;

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

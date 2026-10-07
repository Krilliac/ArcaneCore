using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Creatures;

/// <summary>The AI-facing state of a creature: evade, the AI instance, combat start and the per-fight flags.</summary>
public sealed partial class Creature : Unit, ICombatCreature
{
    /// <summary>
    /// vmangos Creature::IsInEvadeMode: true from EnterEvadeMode until the creature is home.
    /// Combat refuses new attacks on an evading creature (docs/integration/combat.md).
    /// </summary>
    public bool IsInEvadeMode => IsEvading;

    /// <summary>Set by the map system while the creature runs home after leaving combat.</summary>
    internal bool IsEvading { get; set; }

    /// <summary>The script driving this creature (null outside a creature map system).</summary>
    public CreatureAI? AI { get; internal set; }

    /// <summary>Where combat began (vmangos m_combatStartX/Y/Z): waypoint movers evade back here.</summary>
    internal CreatureHome? CombatStart { get; set; }

    /// <summary>The aggro hook ran for the current fight (reset by evade, death and respawn).</summary>
    internal bool HasAggroed { get; set; }

    /// <summary>
    /// How the creature reacts to units around it (vmangos <c>m_reactState</c>): set from the template at creation and
    /// again at every respawn (Creature::InitializeReactState); an AI may change it afterwards.
    /// </summary>
    public CreatureReactState ReactState { get; set; } = CreatureReactState.Aggressive;

    /// <summary>
    /// Milliseconds left of the temporary pacify (vmangos <c>m_pacifiedTimer</c>): the creature cannot initiate attacks while it is
    /// above 0. Set at respawn, counted down while alive, cleared when it enters combat.
    /// </summary>
    internal uint PacifiedMs { get; set; }

    /// <summary>The map clock (ms) of the creature's last stealth alert (vmangos m_uLastAlertTime), or null before the first one.</summary>
    internal long? LastAlertAtMs { get; set; }

    /// <summary>Whether the creature is temporarily pacified (vmangos IsTempPacified).</summary>
    public bool IsTempPacified => PacifiedMs > 0;

    /// <summary>
    /// The leash extension clock (vmangos <c>m_lastLeashExtensionTime</c>): null until the first leash check of a fight, shared with the
    /// creatures that joined this one through its assistance call, cleared when combat stops.
    /// </summary>
    internal LeashExtensionClock? LeashClock { get; set; }

    /// <summary>The assistance call went out for the current fight (vmangos m_AlreadyCallAssistance).</summary>
    internal bool CalledAssistance { get; set; }

    /// <summary>
    /// A live fear aura holds <c>UnitFlags.Fleeing</c> (set with the flag by <c>CcState.RefreshFear</c>). A timed flight (critter, flee for
    /// assistance) sets the same flag for itself and must not clear it while this is true (vmangos keeps UNIT_FLAG_FLEEING aura-owned,
    /// Unit::ModConfuseSpell, Unit.cpp:9141-9147; TimedFleeingMovementGenerator::Finalize clears only unit states).
    /// </summary>
    internal bool FearHeldByAura { get; set; }

    /// <summary>vmangos CreatureAI::AttackedBy: an idle creature retaliates against its attacker.</summary>
    public void OnAttackedBy(Unit attacker)
    {
        if (AI is { } ai)
        {
            if (IsAlive && !IsEvading && Map is not null)
            {
                ai.OnAttackedBy(attacker);
            }

            return;
        }

        if (Map is not { } map || Combat.Victim is not null || !IsAlive || !map.Combat.Hooks.CanAttack(this, attacker))
        {
            return;
        }

        System?.StopMoving(this);
        map.Combat.Attack(this, attacker, MeleeAllowedByTemplate);
    }

    /// <summary>vmangos CreatureAI::JustDied: tell the AI, then begin the map system's corpse and respawn timers.</summary>
    public void OnJustDied(Unit? killer) => System?.OnCreatureDied(this, killer);
}

/// <summary>A shared whole-second timestamp (vmangos <c>shared_ptr&lt;time_t&gt;</c> leash extension time).</summary>
internal sealed class LeashExtensionClock
{
    public long Seconds { get; set; }
}

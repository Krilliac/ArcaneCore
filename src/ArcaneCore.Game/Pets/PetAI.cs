using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Pets;

/// <summary>
/// vmangos <c>PetAI</c> (AI/PetAI.cpp) for the pets, guardians and mini pets of this build: the
/// state machine that obeys <see cref="CharmInfo"/> (stay, follow, attack commands and the react
/// state), picks the next target after a kill, returns to the owner or the stay position, defends
/// itself and its owner, and autocasts its harmful spells on its victim.
/// <para>
/// Ported: <c>UpdateAI</c> (victim validity, return movement, autocast), <c>_needToStop</c>,
/// <c>_stopAttack</c>, <c>HandleReturnMovement</c>, <c>DoAttack</c>, <c>AttackStart</c> (as
/// <see cref="AttackTarget"/>: the base <c>AttackStart</c> is not virtual), <c>CanAttack</c>,
/// <c>SelectNextTarget</c>, <c>KilledUnit</c>, <c>AttackedBy</c>, <c>OwnerAttackedBy</c>,
/// <c>OwnerAttacked</c>, <c>MovementInform</c> and the imp's lack of a melee attack. Not ported (each
/// needs data or primitives another area owns, docs/integration/pets.md): positive and ally autocast
/// (<c>Spell::CanAutoCast</c>, <c>UpdateAllies</c>), taunt, the threat-list retarget of creature-owned
/// pets, crowd-control checks (<c>HasAuraPetShouldAvoidBreaking</c>), possession, and aggro on sight
/// (the creature area does not model creature-versus-creature aggro, so an aggressive pet reacts
/// to attacks on itself and its owner only).
/// </para>
/// <para>Thread affinity: world thread.</para>
/// </summary>
public sealed class PetAI : CreatureAI
{
    /// <summary>vmangos <c>m_bMeleeAttack = (entry != 416)</c>: the warlock imp has no melee attack (PetAI.cpp:41).</summary>
    public const uint ImpEntry = 416;

    private readonly Func<SpellSystem?> _spells;
    private readonly Random _random;

    public PetAI(Creature creature, Func<SpellSystem?>? spells = null, Random? random = null)
        : base(creature)
    {
        _spells = spells ?? (static () => null);
        _random = random ?? new Random();
        MeleeEnabled = creature.Entry != ImpEntry;
    }

    private CharmInfo? Charm => Me.Summon?.Charm;

    private Unit? Owner => Me.GetOwner();

    private MapCombat? Fight => Me.Map?.Combat;

    // --- UpdateAI -------------------------------------------------------------------------------------

    /// <summary>vmangos PetAI::UpdateAI (PetAI.cpp:145-340).</summary>
    public override void OnUpdate(uint diffMs)
    {
        if (!Me.IsAlive || Charm is not { } charm || (Me.UnitFlags & (UnitFlags.Stunned | UnitFlags.Fleeing | UnitFlags.Confused)) != 0)
        {
            return;
        }

        if (Me.Combat.Victim is { IsAlive: true })
        {
            if (NeedToStop(charm))
            {
                StopAttack(charm);
                return;
            }

            // DoMeleeAttackIfReady: the swing loop is the combat system's
        }
        else
        {
            HandleReturnMovement(charm);
        }

        // FOLLOW_MOTION_TYPE inform: the pet reached its follow point (PetAI::MovementInform)
        if (charm.IsReturning && Me.Motion.CurrentType == MovementGeneratorType.Follow && !Me.IsMoving)
        {
            charm.ClearFlags();
            charm.IsFollowing = true;
        }

        if (Me.IsAlive && Charm is not null)
        {
            Autocast(charm);
        }
    }

    /// <summary>vmangos PetAI::_needToStop (PetAI.cpp:56-90).</summary>
    private bool NeedToStop(CharmInfo charm)
    {
        // Stop attacking when player is mounted
        if (Me.IsPet && !charm.Enabled)
        {
            return true;
        }

        Unit? victim = Me.Combat.Victim;
        if (victim is null)
        {
            return true;
        }

        // Prevent creature pets from chasing forever
        if (Owner is Creature ownerCreature && !ownerCreature.Combat.IsInCombat)
        {
            if (ownerCreature.IsAlive)
            {
                if (ownerCreature.IsInEvadeMode)
                {
                    return true;
                }
            }
            else if (System?.IsOutOfThreatArea(Me, victim) == true)
            {
                return true;
            }
        }

        return Fight?.Hooks.CanAttack(Me, victim) != true;
    }

    /// <summary>vmangos PetAI::_stopAttack (PetAI.cpp:92-105).</summary>
    private void StopAttack(CharmInfo charm)
    {
        if (!Me.IsAlive)
        {
            Me.Motion.Clear();
            Fight?.CombatStop(Me);
            return;
        }

        Fight?.AttackStop(Me);
        System?.AiServices.Spells?.Interrupt(Me);
        charm.IsCommandAttack = false;
        charm.ClearFlags();
        HandleReturnMovement(charm);
    }

    /// <summary>vmangos PetAI::HandleReturnMovement (PetAI.cpp:585-620): back to the stay point, or to the owner.</summary>
    private void HandleReturnMovement(CharmInfo charm)
    {
        if (charm.CommandState == CommandState.Stay)
        {
            if (!charm.IsAtStay && !charm.IsReturning)
            {
                // Return to previous position where stay was clicked
                (float x, float y, float z) = charm.StayPosition;
                charm.ClearFlags();
                charm.IsReturning = true;
                Me.Motion.Clear();
                Me.Motion.MovePoint(StayPointId, x, y, z, run: true);
            }
        }
        else if (!charm.IsFollowing && !charm.IsReturning && Owner is { } owner)
        {
            // COMMAND_FOLLOW
            charm.ClearFlags();
            charm.IsReturning = true;
            Me.Motion.Clear();
            Me.Motion.MoveFollow(owner, PetConstants.FollowDistance, Me.Summon?.FollowAngle ?? PetConstants.FollowAngle);
        }
    }

    /// <summary>The point id of the move to the stay position (vmangos uses the pet's low GUID).</summary>
    private uint StayPointId => Me.Guid.Counter;

    /// <summary>vmangos PetAI::MovementInform, POINT_MOTION_TYPE (PetAI.cpp:672-690).</summary>
    public override void OnMovementInform(MovementGeneratorType type, uint pointId)
    {
        if (Charm is { } charm && type == MovementGeneratorType.Point && pointId == StayPointId && charm.IsReturning)
        {
            charm.ClearFlags();
            charm.IsAtStay = true;
            Me.Motion.Clear();
        }
    }

    // --- attacking ------------------------------------------------------------------------------------------

    /// <summary>
    /// vmangos PetAI::AttackStart (PetAI.cpp:366-381): check every pet state to decide whether the
    /// target may be attacked, then attack it, chasing unless the pet is told to stay (and is not
    /// commanded to attack).
    /// </summary>
    public bool AttackTarget(Unit target)
    {
        if (Charm is not { } charm || !CanAttack(target, charm))
        {
            return false;
        }

        return DoAttack(target, chase: charm.CommandState != CommandState.Stay || charm.IsCommandAttack);
    }

    /// <summary>vmangos PetAI::DoAttack (PetAI.cpp:690-748): attack with or without chase and reset the flags.</summary>
    private bool DoAttack(Unit target, bool chase)
    {
        if (Charm is not { } charm || Me.Map is not { } map || !map.Combat.Attack(Me, target, MeleeEnabled))
        {
            return false;
        }

        // Play sound to let the player know the pet is attacking something it picked on its own
        if (charm.ReactState == ReactState.Aggressive && !charm.IsCommandAttack && Owner is Player owner)
        {
            owner.Session.Send(WorldOpcode.SmsgAiReaction, PetPackets.BuildAiReaction(Me.Guid));
        }

        Me.Combat.Threat.AddThreat(target, 0f);
        map.Combat.SetInCombatState(Me, 0);
        map.Combat.SetInCombatState(target, 0);
        if (chase)
        {
            bool oldCommandAttack = charm.IsCommandAttack; // This needs to be reset after other flags are cleared
            charm.ClearFlags();
            charm.IsCommandAttack = oldCommandAttack;      // for passive pets commanded to attack so they will use spells
            Me.Motion.Clear();
            Me.Motion.MoveChase(target);
        }
        else
        {
            // (Stay && ((Aggressive || Defensive) && In Melee Range))
            charm.ClearFlags();
            charm.IsAtStay = true;
            Me.Motion.Clear();
        }

        return true;
    }

    /// <summary>
    /// vmangos PetAI::CanAttack (PetAI.cpp:750-812): whether the pet may attack <paramref name="target"/>
    /// given its command state, react state and flags. The order of the checks is vmangos'.
    /// </summary>
    public bool CanAttack(Unit target, CharmInfo charm)
    {
        if (Fight?.Hooks.CanAttack(Me, target) != true || !target.IsAlive)
        {
            if (!target.IsAlive)
            {
                // Clear target to prevent getting stuck on dead targets
                Fight?.AttackStop(Me);
                System?.AiServices.Spells?.Interrupt(Me);
            }

            return false;
        }

        // Pet desactive (monture)
        if (Me.IsPet && !charm.Enabled)
        {
            return false;
        }

        // Passive - passive pets can attack if told to
        if (charm.ReactState == ReactState.Passive)
        {
            return charm.IsCommandAttack;
        }

        // Player's pet should not attack PvP flagged target unless told to
        if ((Me.UnitFlags & UnitFlags.Pvp) == 0 && (target.UnitFlags & UnitFlags.Pvp) != 0 && Me.CharmerOrOwnerGuid.IsPlayer)
        {
            return charm.IsCommandAttack;
        }

        // Returning - pets ignore attacks only if owner clicked follow
        if (charm.IsReturning)
        {
            return !charm.IsCommandFollow;
        }

        // Stay - can attack if target is within range or commanded to
        if (charm.CommandState == CommandState.Stay)
        {
            return MapCombat.CanReachWithMeleeAutoAttack(Me, target) || charm.IsCommandAttack;
        }

        // Pets attacking something (or chasing) should only switch targets if owner tells them to
        if (Me.Combat.Victim is { } victim && !ReferenceEquals(victim, target))
        {
            // Check if our owner selected this target and clicked "attack"
            Unit? owner = Owner;
            Unit? ownerTarget = owner switch
            {
                Player player => player.Target.IsEmpty ? null : player.Map?.FindObject(player.Target) as Unit,
                not null => owner.Combat.Victim,
                _ => null,
            };
            if (ownerTarget is not null && charm.IsCommandAttack)
            {
                return target.Guid == ownerTarget.Guid;
            }
        }

        // Follow
        return charm.CommandState == CommandState.Follow && !charm.IsReturning;
    }

    // --- target selection -------------------------------------------------------------------------------------

    private enum SelectResult
    {
        Success,
        FailDefault,
        FailNotEnabled,
        FailPassive,
        FailNoOwner,
    }

    /// <summary>vmangos PetAI::SelectNextTarget (PetAI.cpp:480-560): after a kill, the pet's own attackers, then what the owner is fighting.</summary>
    private (Unit? Target, SelectResult Reason) SelectNextTarget(CharmInfo charm)
    {
        // Pet desactive (monture)
        if (Me.IsPet && !charm.Enabled)
        {
            return (null, SelectResult.FailNotEnabled);
        }

        // Passive pets don't do next target selection
        if (charm.ReactState == ReactState.Passive)
        {
            return (null, SelectResult.FailPassive);
        }

        if (Owner is not { } owner)
        {
            return (null, SelectResult.FailNoOwner);
        }

        if (owner is Creature ownerCreature)
        {
            // Owner is creature and is evading. We must not re-aggro.
            if (ownerCreature.IsInEvadeMode)
            {
                return (null, SelectResult.FailNotEnabled);
            }
        }
        else
        {
            foreach (Unit attacker in Me.Combat.Attackers)
            {
                if (charm.IsAtStay && !MapCombat.CanReachWithMeleeAutoAttack(Me, attacker))
                {
                    continue;
                }

                if (attacker.Combat.IsInCombat && Fight?.Hooks.CanAttack(Me, attacker) == true)
                {
                    return (attacker, SelectResult.Success);
                }
            }
        }

        if (owner.Combat.IsInCombat)
        {
            if (owner.Combat.Victim is { } ownerVictim
                && (!charm.IsAtStay || MapCombat.CanReachWithMeleeAutoAttack(Me, ownerVictim)))
            {
                return (ownerVictim, SelectResult.Success);
            }

            // Check owner attackers
            foreach (Unit attacker in owner.Combat.Attackers)
            {
                if (charm.IsAtStay && !MapCombat.CanReachWithMeleeAutoAttack(Me, attacker))
                {
                    continue;
                }

                if (attacker.Combat.IsInCombat && Fight?.Hooks.CanAttack(Me, attacker) == true)
                {
                    return (attacker, SelectResult.Success);
                }
            }
        }

        // Default - no valid targets
        return (null, SelectResult.FailDefault);
    }

    // --- reactions ------------------------------------------------------------------------------------------------

    /// <summary>vmangos PetAI::KilledUnit (PetAI.cpp:342-373): the pet killed (or its owner killed) something.</summary>
    public override void OnKilledUnit(Unit victim)
    {
        if (Charm is not { } charm)
        {
            return;
        }

        // if owner killed this victim, pet may still be attacking something else
        if (Me.Combat.Victim is { } current && !ReferenceEquals(current, victim))
        {
            return;
        }

        // Clear target just in case. Can't use StopAttack() because that activates movement handlers and ignores next target selection
        Fight?.AttackStop(Me);
        System?.AiServices.Spells?.Interrupt(Me);

        // Before returning to owner, see if there are more things to attack
        (Unit? next, SelectResult reason) = SelectNextTarget(charm);
        if (next is not null)
        {
            AttackTarget(next);
        }
        else if (reason is SelectResult.FailDefault or SelectResult.FailNotEnabled or SelectResult.FailNoOwner)
        {
            if (Me.Combat.IsInCombat)
            {
                Fight?.CombatStop(Me);
            }

            HandleReturnMovement(charm);
        }
    }

    /// <summary>vmangos PetAI::AttackedBy (PetAI.cpp:814-835): the pet was hit.</summary>
    public override void OnAttackedBy(Unit attacker)
    {
        if (Charm is not { } charm || Fight?.Hooks.CanAttack(Me, attacker) != true)
        {
            return;
        }

        // Pet desactive (monture), passive pets don't do anything
        if ((Me.IsPet && !charm.Enabled) || charm.ReactState == ReactState.Passive)
        {
            return;
        }

        // Prevent pet from disengaging from current target
        if (Me.Combat.Victim is { IsAlive: true })
        {
            return;
        }

        AttackTarget(attacker);
    }

    /// <summary>vmangos PetAI::OwnerAttackedBy (PetAI.cpp:383-414): the owner was hit.</summary>
    public void OwnerAttackedBy(Unit attacker)
    {
        if (Charm is not { } charm || Fight?.Hooks.CanAttack(Me, attacker) != true || (Me.IsPet && !charm.Enabled)
            || charm.ReactState == ReactState.Passive || (Me.UnitFlags & (UnitFlags.Stunned | UnitFlags.Fleeing | UnitFlags.Confused)) != 0)
        {
            return;
        }

        // Prevent pet from disengaging from current target
        if (Me.Combat.Victim is { IsAlive: true })
        {
            return;
        }

        AttackTarget(attacker);
    }

    /// <summary>vmangos PetAI::OwnerAttacked (PetAI.cpp:416-453): the owner started a fight.</summary>
    public void OwnerAttacked(Unit target)
    {
        // The owner attacking a mob while the pet is currently not in combat will not make the pet
        // attack that target too (tested on classic): a defensive pet engages only when it or its owner
        // is damaged, an aggressive one by proximity.
        if (Charm is not { } charm || (!Me.Combat.IsInCombat && Me.CharmerOrOwnerGuid.IsPlayer))
        {
            return;
        }

        if (Fight?.Hooks.CanAttack(Me, target) != true || (Me.IsPet && !charm.Enabled) || charm.ReactState == ReactState.Passive
            || (Me.UnitFlags & (UnitFlags.Stunned | UnitFlags.Fleeing | UnitFlags.Confused)) != 0)
        {
            return;
        }

        if (Me.Combat.Victim is { IsAlive: true })
        {
            return;
        }

        AttackTarget(target);
    }

    // --- autocast -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The harmful half of vmangos' autocast loop (PetAI.cpp:226-330): while the pet fights, a spell
    /// with autocast on that is harmful, ready and not a non-combat spell goes at the victim; one is
    /// picked at random when several qualify, the pet turns to its target and the owner hears the aggro
    /// growl (or, 10% of the time for a summoned pet, its special-spell talk).
    /// </summary>
    private void Autocast(CharmInfo charm)
    {
        if (_spells() is not { } spells || spells.GetState(Me.Guid)?.CurrentCast is not null
            || Me.Combat.Victim is not { IsAlive: true } victim || Fight?.Hooks.CanAttack(Me, victim) != true)
        {
            return;
        }

        List<SpellInfo>? candidates = null;
        foreach ((uint spellId, bool autocast) in charm.PetSpells)
        {
            if (!autocast || spells.Store.Get(spellId) is not { IsPassive: false, IsPositive: false } spell || !spells.IsSpellReady(Me, spell))
            {
                continue;
            }

            (candidates ??= []).Add(spell);
        }

        if (candidates is null)
        {
            return;
        }

        SpellInfo chosen = candidates[_random.Next(candidates.Count)];
        if (!MapCombat.HasInArc(Me, victim, MathF.PI))
        {
            Me.Orientation = Creature.NormalizeOrientation(MathF.Atan2(victim.Y - Me.Y, victim.X - Me.X));
        }

        if (spells.CastSpell(Me, chosen.Id, SpellCastTargets.ForUnit(victim.Guid), triggered: false) == SpellCastResult.CastOk
            && Owner is Player owner)
        {
            if (Me.Summon?.Kind == SummonKind.Pet && _random.Next(0, 101) < 10)
            {
                owner.Session.Send(WorldOpcode.SmsgPetActionSound, PetPackets.BuildActionSound(Me.Guid, PetTalk.SpecialSpell));
            }
            else
            {
                owner.Session.Send(WorldOpcode.SmsgAiReaction, PetPackets.BuildAiReaction(Me.Guid));
            }
        }
    }
}

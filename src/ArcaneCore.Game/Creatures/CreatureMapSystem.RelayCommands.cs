using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// The relay commands TEMP_SPAWN_CREATURE (10), ACTIVATE_OBJECT (13), MOVEMENT (20) and SEND_AI_EVENT (35) (cmangos
/// ScriptAction::ExecuteDbscriptCommand, DBScripts/ScriptMgr.cpp:2048-2073, 2115-2128, 2277-2385, 2759-2775).
/// </summary>
public sealed partial class CreatureMapSystem
{
    /// <summary>cmangos CONTACT_DISTANCE (ObjectDefines.h): the gap a summon without coordinates keeps to its summoner.</summary>
    public const float ContactDistance = 0.5f;

    /// <summary>
    /// SCRIPT_COMMAND_TEMP_SPAWN_CREATURE: creature <c>datalong</c> at (x, y, z, o), from any source object. Without coordinates it stands
    /// in front of the source at contact distance (CreatureCreatePos(spawner, spawner orientation, CONTACT_DISTANCE) then GetClosePoint,
    /// which adds the spawner's orientation a second time and both bounding radii: Entities/Object.cpp:2050-2054, Creature.cpp:105-118,
    /// Object.h:905-909). <c>dataint</c> 1 makes it run (SetWalk(!setRun)). With <c>datalong2</c> it goes after that many ms alive, out of
    /// combat and uncharmed (TEMPSPAWN_TIMED_OOC_OR_DEAD_DESPAWN), otherwise only once its corpse is gone (TEMPSPAWN_DEAD_DESPAWN).
    /// <c>datalong3</c> is the default path id (MotionMaster::SetDefaultPathId) a summon with waypoint movement walks from its entry's
    /// <c>creature_movement_template</c>. COMMAND_ADDITIONAL (active object) needs no grid state here; a spawn data entry (<c>dataint4</c>,
    /// creature_spawn_data_template) is not supported and is reported.
    /// </summary>
    private void RelayTempSpawn(RelayScriptStep step, WorldObject? source)
    {
        if (source is null)
        {
            return; // "found no worldobject as source, skipping"
        }

        if (_content.FindTemplate(step.DataLong) is not { } template)
        {
            ReportRelay(step, $"TEMP_SPAWN_CREATURE of missing creature_template {step.DataLong}");
            return;
        }

        if (step.DataInt4 != 0)
        {
            ReportRelay(step, "TEMP_SPAWN_CREATURE with a creature_spawn_data_template entry (dataint4)");
        }

        bool atSource = step.X == 0f && step.Y == 0f && step.Z == 0f;
        Creature summoned = SpawnTemporary(template, atSource ? source.X : step.X, atSource ? source.Y : step.Y, atSource ? source.Z : step.Z, step.Orientation);
        if (atSource)
        {
            float distance = ContactDistance + source.BoundingRadius + summoned.BoundingRadius;
            float angle = source.Orientation + source.Orientation;
            PlaceTemporary(summoned, source.X + (distance * MathF.Cos(angle)), source.Y + (distance * MathF.Sin(angle)), source.Z, step.Orientation);
        }

        summoned.ScriptRun = step.DataInt == 1;
        SyncWalkMode(summoned, summoned.ScriptRun);
        if (step.DataLong3 != 0 && summoned.MovementType == CreatureMovementType.Waypoint)
        {
            IReadOnlyList<CreatureWaypoint> path = _content.GetEntryWaypoints(template.Entry, step.DataLong3);
            if (path.Count > 0)
            {
                summoned.Motion.Initialize(new WaypointMovementGenerator(path), this, start: true);
            }
            else
            {
                ReportRelay(step, $"TEMP_SPAWN_CREATURE path {step.DataLong3} missing for entry {template.Entry}");
            }
        }

        if (step.DataLong2 > 0)
        {
            AddTimedSummon(summoned, step.DataLong2, SummonTimer.OutOfCombatUncharmed);
        }
    }

    /// <summary>Put a just-made temporary creature at its final spawn point (before any client has seen it).</summary>
    private void PlaceTemporary(Creature creature, float x, float y, float z, float orientation)
    {
        creature.SetHome(new CreatureHome(x, y, z, orientation));
        creature.ResetToHome(_serverTime());
        Map.OnObjectMoved(creature);
    }

    /// <summary>
    /// SCRIPT_COMMAND_ACTIVATE_OBJECT: the source unit uses the target game object (GameObject::Use(Unit*)), or with COMMAND_ADDITIONAL the
    /// object plays custom animation <c>datalong</c> instead.
    /// </summary>
    private void RelayActivateObject(RelayScriptStep step, WorldObject? source, WorldObject? target)
    {
        if (source is not Unit user)
        {
            ReportRelay(step, "ACTIVATE_OBJECT by a non-unit");
            return;
        }

        if (target is not GameObject go || Map.FindUpdater<GameObjectMapSystem>() is not { } objects)
        {
            ReportRelay(step, "ACTIVATE_OBJECT without a game object target");
            return;
        }

        if ((step.DataFlags & FlagCommandAdditional) != 0)
        {
            objects.SendCustomAnim(go, step.DataLong);
            return;
        }

        if (objects.UseByUnit(user, go) == GameObjectUseResult.Unsupported)
        {
            ReportRelay(step, $"ACTIVATE_OBJECT of a game object of type {go.Type}");
        }
    }

    /// <summary>
    /// SCRIPT_COMMAND_MOVEMENT for a creature source out of combat (in combat the step is skipped): <c>datalong</c> 0 idles; 1 wanders within
    /// <c>datalong2</c> yards (the spawn's own wander when 0) around the spawn point, or around where it stands with COMMAND_ADDITIONAL, walking
    /// unless <c>dataint</c> is non-zero (MoveRandomAroundPoint's walk argument is textId[0] == 0); 2 walks waypoint path <c>datalong2</c> (0: the
    /// creature's default path, its spawn's <c>creature_movement</c> else the entry's path 0; otherwise path <c>datalong2</c> of the entry's
    /// <c>creature_movement_template</c>). Idle and waypoint replace the creature's default movement (Clear(false, true) then the new one); random
    /// does so only with <c>dataint2</c> bit 0x1 ("make it main movegen"), otherwise it is pushed over the default, which resumes when the stack
    /// is cleared (cmangos ScriptMgr.cpp:2334-2350, MoveRandomAroundPoint mutates). A waypoint with <c>datalong3</c> bit 0x1 (pass the target) is
    /// skipped only when there is no target (ScriptMgr.cpp:2318-2330); the target itself is not handed to the path (no waypoint scripts here).
    /// Paths from <c>waypoint_path</c> (<c>datalong3</c> bit 0x2), the random expiry timer, forced movement, the formation update and the path,
    /// linear and fall movement types are not supported and are reported.
    /// </summary>
    private void RelayMovement(RelayScriptStep step, WorldObject? source, WorldObject? target)
    {
        if (source is not Creature mover)
        {
            ReportRelay(step, "MOVEMENT by a non-creature");
            return;
        }

        if (mover.Combat.IsInCombat)
        {
            return; // "source is in combat and may lead to wrong behaviour: skipping"
        }

        switch (step.DataLong)
        {
            case 0:
                StopMoving(mover);
                mover.Motion.Initialize(IdleMovementGenerator.Instance, this, start: true);
                break;
            case 1:
            {
                if (step.DataLong3 != 0)
                {
                    ReportRelay(step, "MOVEMENT random with an expiry timer");
                }

                bool around = (step.DataFlags & FlagCommandAdditional) != 0;
                float? wander = step.DataLong2 != 0 ? step.DataLong2 : around ? 0f : null;
                CreatureHome? center = around ? new CreatureHome(mover.X, mover.Y, mover.Z, mover.Orientation) : null;
                var random = new RandomMovementGenerator(wander, center, run: step.DataInt != 0);
                if ((step.DataInt2 & 0x1) != 0)
                {
                    // "make it main movegen": StopMoving, Clear(false, true), then the wander is the only generator.
                    StopMoving(mover);
                    mover.Motion.Initialize(random, this, start: true);
                }
                else
                {
                    mover.Motion.MoveRandom(random);
                }

                break;
            }

            case 2:
            {
                if ((step.DataLong3 & 0x2) != 0)
                {
                    ReportRelay(step, $"MOVEMENT waypoint with datalong3 {step.DataLong3} (a waypoint_path path)");
                    return;
                }

                if ((step.DataLong3 & 0x1) != 0 && target is null)
                {
                    return; // "pass target true and target nullptr: skipping"
                }

                IReadOnlyList<CreatureWaypoint> path = step.DataLong2 == 0
                    ? _content.ResolveWaypointPath(mover.Spawn?.Guid ?? 0, mover.Template.Entry).Points
                    : _content.GetEntryWaypoints(mover.Template.Entry, step.DataLong2);
                if (path.Count == 0)
                {
                    ReportRelay(step, $"MOVEMENT waypoint path {step.DataLong2} missing for entry {mover.Template.Entry}");
                    return;
                }

                StopMoving(mover);
                mover.Motion.Initialize(new WaypointMovementGenerator(path), this, start: true);
                break;
            }

            default:
                ReportRelay(step, $"MOVEMENT type {step.DataLong}");
                break;
        }
    }

    /// <summary>
    /// SCRIPT_COMMAND_SEND_AI_EVENT: a creature source sends AI event <c>datalong</c>. With a radius (<c>datalong2</c>) it goes to the creatures
    /// around (<see cref="SendAiEventAround"/>) with the target as the invoker; without one, to the target when that is a creature (sender the
    /// source, no invoker: UnitAI::SendAIEvent), and to the source itself when the target is a player (sender and invoker the player).
    /// </summary>
    private void RelaySendAiEvent(RelayScriptStep step, WorldObject? source, WorldObject? target)
    {
        if (source is not Creature sender || target is not Unit unit)
        {
            return; // LogIfNotCreature(pSource) / LogIfNotUnit(pTarget)
        }

        if (step.DataLong2 != 0)
        {
            SendAiEventAround(sender, step.DataLong, unit, step.DataLong2);
        }
        else if (unit is Creature receiver)
        {
            receiver.ReceiveAiEvent(step.DataLong, sender, invoker: null);
        }
        else if (unit is Player player)
        {
            sender.ReceiveAiEvent(step.DataLong, player, player);
        }
    }

    /// <summary>
    /// cmangos UnitAI::SendAIEventAround without delay (AI/BaseAI/UnitAI.cpp:615-654): the custom events A to F (5, 6, 8-11) and the events
    /// above 100 reach every living creature within <paramref name="radius"/> (IsWithinDistInMap: 3D, bounding radii added), the sender
    /// included; the other events reach the creatures that may assist the sender against the invoker (AnyAssistCreatureInRangeCheck, here
    /// <see cref="CanAssist"/>, which includes the line-of-sight check of AnyAssistCreatureInRangeCheck, GridNotifiers.cpp:265-282); for
    /// AI_EVENT_CALL_ASSISTANCE (13, AI/BaseAI/AIDefines.h:40; the "type 0" of the UnitAI.cpp:647 comment is stale) each receiver also answers
    /// the call (<see cref="HandleAssistanceCall"/>). The EventAI action THROW_AI_EVENT
    /// (45) and the relay command SEND_AI_EVENT (35) both send through here. Returns how many creatures received it.
    /// </summary>
    public int SendAiEventAround(Creature sender, uint eventType, Unit? invoker, float radius, uint miscValue = 0)
    {
        ArgumentNullException.ThrowIfNull(sender);
        if (radius <= 0)
        {
            return 0;
        }

        bool custom = (eventType >= 5 && eventType <= 11 && eventType != 7) || eventType > 100;
        Creature[] receivers = [.. _creatures.Values.Where(c => c.IsAlive && WithinDistInMap(sender, c, radius)
            && (custom || (invoker is not null && !ReferenceEquals(c, sender) && CanAssist(c, sender, invoker, radius))))];
        foreach (Creature receiver in receivers)
        {
            receiver.ReceiveAiEvent(eventType, sender, invoker, miscValue);
            if (eventType == AiEventCallAssistance)
            {
                HandleAssistanceCall(receiver, sender, invoker);
            }
        }

        return receivers.Length;
    }

    /// <summary>
    /// cmangos AI_EVENT_CALL_ASSISTANCE (AI/BaseAI/AIDefines.h:40): the AI event type that also runs <see cref="HandleAssistanceCall"/> on each
    /// receiver. 0 is AI_EVENT_JUST_DIED, which is only received.
    /// </summary>
    public const uint AiEventCallAssistance = 13;

    /// <summary>
    /// cmangos CreatureAI::HandleAssistanceCall (AI/BaseAI/CreatureAI.cpp:224-233): a receiver that is not a critter and may assist the sender
    /// against the invoker stops calling assistance itself (SetNoCallAssistance) and answers the call (OnCallForHelp: AttackStart).
    /// </summary>
    private void HandleAssistanceCall(Creature receiver, Creature sender, Unit? invoker)
    {
        if (invoker is null || receiver.Template.CreatureType == CreatureAiFactory.CritterType || !receiver.IsAlive
            || !_ai.Hostility.CanAssist(receiver, sender) || !Map.Combat.Hooks.CanAttack(receiver, invoker))
        {
            return;
        }

        receiver.CalledAssistance = true;
        AttackStart(receiver, invoker);
    }

    private static bool WithinDistInMap(WorldObject a, WorldObject b, float range)
    {
        float reach = range + a.BoundingRadius + b.BoundingRadius;
        return a.MapId == b.MapId && DistanceSquared(a, b) <= reach * reach;
    }
}

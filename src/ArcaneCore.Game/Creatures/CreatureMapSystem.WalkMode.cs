using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Creatures;

public sealed partial class CreatureMapSystem
{
    /// <summary>
    /// The walk/run toggle that every spline launch carries (vmangos MoveSplineInit::Launch, MoveSplineInit.cpp:109-112 and
    /// :173-176): a run spline clears MOVEFLAG_WALK_MODE, a walk spline sets it, and a change is announced to the observers
    /// with SMSG_SPLINE_MOVE_SET_RUN_MODE / SET_WALK_MODE (body: the packed guid, wow_messages
    /// smsg_spline_move_set_walk_mode.wowm) BEFORE the SMSG_MONSTER_MOVE itself. A repeat of the same mode sends nothing.
    /// </summary>
    private void SyncWalkMode(Creature creature, bool run)
    {
        bool walking = creature.Movement.HasFlag(MovementFlags.WalkMode);
        if (run == !walking)
        {
            return;
        }

        if (run)
        {
            creature.RemoveMovementFlags(MovementFlags.WalkMode);
        }
        else
        {
            creature.AddMovementFlags(MovementFlags.WalkMode);
        }

        var w = new PacketWriter(10);
        w.WritePackedGuid(creature.Guid.Value);
        Map.BroadcastToObservers(creature, run ? WorldOpcode.SmsgSplineMoveSetRunMode : WorldOpcode.SmsgSplineMoveSetWalkMode, w.ToArray());
    }
}

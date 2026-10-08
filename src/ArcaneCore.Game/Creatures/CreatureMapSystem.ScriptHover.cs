using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Creatures;

public sealed partial class CreatureMapSystem
{
    /// <summary>vmangos Unit::SetHover for a server-controlled creature (Unit.cpp:7272-7293):
    /// set MOVEFLAG_HOVER and send SMSG_SPLINE_MOVE_SET_HOVER/UNSET_HOVER with its packed GUID.</summary>
    public void SetScriptHover(Creature creature, bool hover)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (creature.Movement.HasFlag(MovementFlags.Hover) == hover)
        {
            return;
        }

        if (hover) creature.AddMovementFlags(MovementFlags.Hover);
        else creature.RemoveMovementFlags(MovementFlags.Hover);

        var writer = new PacketWriter(10);
        writer.WritePackedGuid(creature.Guid.Value);
        Map.BroadcastToObservers(creature, hover ? WorldOpcode.SmsgSplineMoveSetHover : WorldOpcode.SmsgSplineMoveUnsetHover,
            writer.ToArray());
    }
}

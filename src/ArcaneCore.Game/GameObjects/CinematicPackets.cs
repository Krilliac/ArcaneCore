using ArcaneCore.Protocol;

namespace ArcaneCore.Game.GameObjects;

/// <summary>Cinematic packets for camera objects.</summary>
public static class CinematicPackets
{
    /// <summary>
    /// SMSG_TRIGGER_CINEMATIC: u32 cinematic sequence id (vmangos Server/Packets/Misc.cpp:789-797 TriggerCinematic, sent by
    /// Player::SendCinematicStart, Player.cpp:6049-6056). Limit: vmangos also starts its server-side cinematic state
    /// (Player::CinematicStart, camera path and the explore check at the end); only the packet is sent here.
    /// </summary>
    public static byte[] TriggerCinematic(uint cinematicId)
    {
        var writer = new PacketWriter(4);
        writer.WriteUInt32(cinematicId);
        return writer.ToArray();
    }
}

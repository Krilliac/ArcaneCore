using ArcaneCore.Game.Items;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Skills;

/// <summary>Skill packet bodies for build 5875.</summary>
public static class SkillPackets
{
    /// <summary>
    /// SMSG_SET_PROFICIENCY (0x0127): u8 item class, u32 item sub-class mask (vmangos Player::SendProficiency,
    /// Player.cpp:17788-17794; gtker wow_messages world/item/smsg_set_proficiency.wowm).
    /// </summary>
    public static byte[] SetProficiency(ItemClass itemClass, uint itemSubClassMask)
    {
        var w = new PacketWriter(5);
        w.WriteByte((byte)itemClass);
        w.WriteUInt32(itemSubClassMask);
        return w.ToArray();
    }
}

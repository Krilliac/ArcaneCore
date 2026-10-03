using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Talents;

/// <summary>Talent packet builders.</summary>
public static class TalentPackets
{
    /// <summary>
    /// MSG_TALENT_WIPE_CONFIRM, server to client: u64 trainer guid, u32 cost in copper (wow_messages
    /// msg_talent_wipe_confirm_server.wowm; vmangos Skill.h TalentWipeConfirmResponse, Player::SendTalentWipeConfirm
    /// Player.cpp:8259-8265). An empty guid means "you have not spent any talent points".
    /// </summary>
    public static PacketWriter WipeConfirm(ObjectGuid trainer, uint cost)
    {
        var writer = new PacketWriter(12);
        writer.WriteUInt64(trainer.Value);
        writer.WriteUInt32(cost);
        return writer;
    }
}

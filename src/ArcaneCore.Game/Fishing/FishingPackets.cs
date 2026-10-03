using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Fishing;

/// <summary>
/// Fishing packets: SMSG_FISH_NOT_HOOKED (0x1C8) and SMSG_FISH_ESCAPED (0x1C9) have empty bodies, SMSG_PLAY_OBJECT_SOUND (0x278) is
/// u32 sound id then the object GUID (gtker/wow_messages wowm/world/spell/smsg_fish_*.wowm, world/gameobject/smsg_player_object_sound.wowm).
/// </summary>
public static class FishingPackets
{
    public static byte[] NotHooked() => [];

    public static byte[] Escaped() => [];

    public static byte[] PlayObjectSound(uint soundId, ObjectGuid source)
    {
        var writer = new PacketWriter(12);
        writer.WriteUInt32(soundId);
        writer.WriteUInt64(source.Value);
        return writer.ToArray();
    }
}
using ArcaneCore.Game;
using ArcaneCore.Game.WorldState.Time;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Gm.Events;

/// <summary>
/// The payloads the <c>.fx</c> commands send (build 5875). Each layout is checked against gtker wow_messages
/// (<c>D:\refs\wow_messages\wow_message_parser\wowm\world\...</c>) and vmangos
/// (<c>D:\refs\vmangos\src\game\Server\Packets\Misc.cpp</c> / <c>Spell.cpp</c>); the opcode numbers are the
/// 1.12.1 ones in <c>Opcodes_1_12_1.h</c>. Where ArcaneCore already had a builder for the same packet it is reused.
/// </summary>
public static class LiveFxPackets
{
    /// <summary>
    /// SMSG_PLAY_MUSIC (0x277 = 631): u32 sound id (wow_messages world/world/smsg_play_music.wowm; vmangos Misc.cpp:579-582
    /// PlayMusic, sent by WorldObject::PlayDirectMusic, Object.cpp:2909-2916).
    /// </summary>
    public static byte[] PlayMusic(uint soundId) => U32(soundId);

    /// <summary>
    /// SMSG_PLAY_SOUND (0x2D2): u32 sound id (wow_messages world/world/smsg_play_sound.wowm; vmangos Misc.cpp:589-592).
    /// Same bytes as <see cref="ArcaneCore.Game.Battlegrounds.BattlegroundPackets.BuildPlaySound"/>.
    /// </summary>
    public static byte[] PlaySound(uint soundId) => ArcaneCore.Game.Battlegrounds.BattlegroundPackets.BuildPlaySound(soundId);

    /// <summary>
    /// SMSG_PLAY_SPELL_VISUAL (0x1F3 = 499): u64 guid, u32 SpellVisualKit id (wow_messages world/spell/smsg_play_spell_visual.wowm;
    /// vmangos Spell.cpp:73-77, sent to the unit's visible set by Unit::SendPlaySpellVisualKit, Unit.cpp:10739-10745).
    /// </summary>
    public static byte[] PlaySpellVisual(ObjectGuid unit, uint visualKit) => ArcaneCore.Game.Spells.FoodDrinkVisualPackets.PlaySpellVisual(unit, visualKit);

    /// <summary>
    /// SMSG_TRIGGER_CINEMATIC (0xFA): u32 CinematicSequences id (wow_messages world/cinematic/smsg_trigger_cinematic.wowm;
    /// vmangos Misc.cpp:794-797).
    /// </summary>
    public static byte[] TriggerCinematic(uint cinematicId) => ArcaneCore.Game.GameObjects.CinematicPackets.TriggerCinematic(cinematicId);

    /// <summary>
    /// SMSG_ZONE_UNDER_ATTACK (0x254 = 596): u32 area id (wow_messages world/combat/smsg_zone_under_attack.wowm "Area zone_id",
    /// versions 1.12; vmangos Misc.cpp:632-635, sent by Creature::SendZoneUnderAttackMessage, Creature.cpp:2909-2923).
    /// </summary>
    public static byte[] ZoneUnderAttack(uint areaId) => U32(areaId);

    /// <summary>
    /// SMSG_UPDATE_WORLD_STATE (0x2C3): u32 field, u32 value for builds after 1.8.4 (wow_messages world/world/smsg_update_world_state.wowm;
    /// vmangos Misc.cpp:1018-1026).
    /// </summary>
    public static byte[] UpdateWorldState(uint field, uint value) => ArcaneCore.Game.Battlegrounds.BattlegroundPackets.BuildUpdateWorldState(field, value);

    /// <summary>
    /// SMSG_LOGIN_SETTIMESPEED (0x42 = 66): packed game time (u32 DateTime bit fields), f32 game minutes per real second
    /// (wow_messages login_logout/smsg_login_settimespeed.wowm, its 1.12 test vector; vmangos Misc.cpp:941-945). The login
    /// sends <see cref="GameTimePacker.GameSpeedMinutesPerSecond"/> (1/60); this one carries a GM-chosen speed.
    /// </summary>
    public static byte[] TimeSpeed(DateTimeOffset localNow, float minutesPerSecond)
    {
        var writer = new PacketWriter(8);
        writer.WriteUInt32(GameTimePacker.Pack(localNow));
        writer.WriteSingle(minutesPerSecond);
        return writer.ToArray();
    }

    /// <summary>
    /// SMSG_AREA_TRIGGER_MESSAGE (0x2B8): u32 length including the terminator, then the NUL-terminated text
    /// (wow_messages world/gameobject/smsg_area_trigger_message.wowm "SizedCString"; vmangos WorldSession::SendAreaTriggerMessage, WorldSession.cpp:882-897, which formats into a 1024-byte buffer).
    /// </summary>
    public static byte[] ScreenMessage(string text) => ArcaneCore.Game.Teleport.TeleportPackets.BuildAreaTriggerMessage(text);

    private static byte[] U32(uint value)
    {
        var writer = new PacketWriter(4);
        writer.WriteUInt32(value);
        return writer.ToArray();
    }
}

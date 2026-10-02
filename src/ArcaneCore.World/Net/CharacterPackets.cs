using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Net;

/// <summary>
/// Builders for the character-screen and login packets. Formats verified against vmangos
/// Player::BuildEnumData and the login sequence in CharacterHandler.cpp.
/// </summary>
public static class CharacterPackets
{
    private const int EquipmentSlots = 20; // 19 equipment slots + first bag (INVENTORY_SLOT_BAG_START + 1)

    /// <summary>Experience needed to leave level 1 (player_xp_for_level, level 1 = 400) until M8 imports the table.</summary>
    private const uint Level1NextLevelXp = 400;

    /// <summary>SMSG_CHAR_ENUM: count followed by one block per character.</summary>
    public static byte[] BuildCharEnum(IReadOnlyList<CharacterRecord> characters)
    {
        var writer = new PacketWriter(64 + (characters.Count * 200));
        writer.WriteByte((byte)characters.Count);

        foreach (CharacterRecord c in characters)
        {
            writer.WriteUInt64((ulong)c.Id); // HIGHGUID_PLAYER is 0, so guid == low id
            writer.WriteCString(c.Name);
            writer.WriteByte(c.Race);
            writer.WriteByte(c.Class);
            writer.WriteByte(c.Gender);
            writer.WriteByte(c.Skin);
            writer.WriteByte(c.Face);
            writer.WriteByte(c.HairStyle);
            writer.WriteByte(c.HairColor);
            writer.WriteByte(c.FacialHair);
            writer.WriteByte(c.Level);
            writer.WriteUInt32(c.ZoneId);
            writer.WriteUInt32(c.MapId);
            writer.WriteSingle(c.X);
            writer.WriteSingle(c.Y);
            writer.WriteSingle(c.Z);
            writer.WriteUInt32(0); // guild id
            writer.WriteUInt32(0); // character flags
            writer.WriteByte((byte)(c.PlayedTime == 0 ? 1 : 0)); // first login
            writer.WriteUInt32(0); // pet display id
            writer.WriteUInt32(0); // pet level
            writer.WriteUInt32(0); // pet family

            for (int slot = 0; slot < EquipmentSlots; slot++)
            {
                writer.WriteUInt32(0); // item display info id
                writer.WriteByte(0);   // inventory type
            }
        }

        return writer.ToArray();
    }

    /// <summary>SMSG_LOGIN_VERIFY_WORLD: the map and position the client should load.</summary>
    public static byte[] BuildLoginVerifyWorld(CharacterRecord c)
    {
        var writer = new PacketWriter(20);
        writer.WriteUInt32(c.MapId);
        writer.WriteSingle(c.X);
        writer.WriteSingle(c.Y);
        writer.WriteSingle(c.Z);
        writer.WriteSingle(c.Orientation);
        return writer.ToArray();
    }

    /// <summary>SMSG_TUTORIAL_FLAGS: eight masks; all-ones marks every tutorial as seen.</summary>
    public static byte[] BuildTutorialFlags()
    {
        var writer = new PacketWriter(32);
        for (int i = 0; i < 8; i++)
        {
            writer.WriteUInt32(0xFFFFFFFF);
        }

        return writer.ToArray();
    }

    /// <summary>
    /// SMSG_LOGIN_SETTIMESPEED: packed game time + game minutes per real second
    /// (vmangos Player::SendInitialPacketsBeforeAddToMap: 1.0f / 60.0f).
    /// </summary>
    public static byte[] BuildTimeSpeed(DateTime utcNow)
    {
        var writer = new PacketWriter(8);
        writer.WriteUInt32(PackGameTime(utcNow));
        writer.WriteSingle(1.0f / 60.0f);
        return writer.ToArray();
    }

    /// <summary>SMSG_INITIAL_SPELLS: empty spell + cooldown lists.</summary>
    public static byte[] BuildInitialSpells()
    {
        var writer = new PacketWriter(8);
        writer.WriteByte(0);   // unknown
        writer.WriteUInt16(0); // spell count
        writer.WriteUInt16(0); // cooldown count
        return writer.ToArray();
    }

    /// <summary>SMSG_CHARACTER_LOGIN_FAILED: one result byte (gtker smsg_character_login_failed).</summary>
    public static byte[] BuildLoginFailed(CharResult result) => [(byte)result];

    /// <summary>Race/class-derived creation values for a player object.</summary>
    public static PlayerAppearance BuildAppearance(RaceInfo raceInfo, ClassInfo classInfo)
    {
        var powerType = (PowerType)classInfo.PowerType;
        (uint maxPower, uint startPower) = powerType switch
        {
            PowerType.Rage => (1000u, 0u),     // rage is stored ×10; 100 rage, starts empty
            PowerType.Energy => (100u, 100u),
            PowerType.Focus => (100u, 100u),
            _ => (classInfo.BaseMana, classInfo.BaseMana),
        };

        return new PlayerAppearance(
            raceInfo.DisplayId,
            raceInfo.FactionTemplate,
            powerType,
            classInfo.BaseHealth,
            classInfo.BaseMana,
            MaxHealth: classInfo.BaseHealth,
            MaxPower: maxPower,
            StartPower: startPower,
            NextLevelXp: Level1NextLevelXp);
    }

    /// <summary>
    /// Pack a UTC time into the client's calendar bit-field (vmangos WorldSession game-time
    /// packing): minute | hour&lt;&lt;6 | weekday&lt;&lt;11 | (day-1)&lt;&lt;14 | (month-1)&lt;&lt;20 | (year-100)&lt;&lt;24.
    /// </summary>
    private static uint PackGameTime(DateTime utcNow)
    {
        int weekday = (int)utcNow.DayOfWeek; // Sunday = 0, matches the client
        return (uint)(utcNow.Minute
            | (utcNow.Hour << 6)
            | (weekday << 11)
            | ((utcNow.Day - 1) << 14)
            | ((utcNow.Month - 1) << 20)
            | ((utcNow.Year - 2000) << 24));
    }
}

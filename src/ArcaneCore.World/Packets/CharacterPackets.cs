using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;

namespace ArcaneCore.World.Packets;

/// <summary>
/// Builders for the character-screen and login packets. Formats verified against vmangos
/// Player::BuildEnumData and the login sequence in CharacterHandler.cpp.
/// </summary>
public static class CharacterPackets
{
    private const int EquipmentSlots = 20; // 19 equipment slots + first bag (INVENTORY_SLOT_BAG_START + 1)

    /// <summary>Experience needed to leave level 1 (player_xp_for_level, level 1 = 400) until M8 imports the table.</summary>
    private const uint Level1NextLevelXp = 400;

    /// <summary>
    /// SMSG_CHAR_ENUM: count followed by one block per character. <paramref name="characterFlags"/> holds the CHARACTER_FLAG_* word
    /// of a character by id (vmangos Player::BuildEnumData: the u32 after the guild id; today only
    /// <see cref="ArcaneCore.Game.Characters.CharacterRenamePackets.CharacterFlagRename"/>); a character without an entry sends 0.
    /// The position of the word is the existing builder's and the MockClient parser's (ScenarioWire.CharacterList); the repo's
    /// generated wow_messages tables cover opcodes and update fields only, so it is UNVERIFIED against smsg_char_enum.wowm here.
    /// </summary>
    public static byte[] BuildCharEnum(
        IReadOnlyList<CharacterRecord> characters,
        IReadOnlyDictionary<int, CharEnumItem[]>? equipment = null,
        IReadOnlyDictionary<int, uint>? characterFlags = null)
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
            writer.WriteUInt32(characterFlags?.GetValueOrDefault(c.Id) ?? 0u); // character flags (CHARACTER_FLAG_*)
            writer.WriteByte((byte)(c.PlayedTime == 0 ? 1 : 0)); // first login
            writer.WriteUInt32(0); // pet display id
            writer.WriteUInt32(0); // pet level
            writer.WriteUInt32(0); // pet family

            CharEnumItem[]? items = equipment?.GetValueOrDefault(c.Id);
            for (int slot = 0; slot < EquipmentSlots; slot++)
            {
                CharEnumItem item = items is not null && slot < items.Length ? items[slot] : default;
                writer.WriteUInt32(item.DisplayId); // item display info id
                writer.WriteByte(item.InventoryType);
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

    /// <summary>
    /// SMSG_LOGIN_SETTIMESPEED: packed game time + game minutes per real second
    /// (vmangos Player::SendInitialPacketsBeforeAddToMap: 1.0f / 60.0f).
    /// </summary>
    public static byte[] BuildTimeSpeed(DateTime utcNow) => BuildTimeSpeed(new DateTimeOffset(utcNow));

    /// <summary>
    /// The same packet from the game's LOCAL time (vmangos packs <c>localtime</c>; see
    /// <see cref="ArcaneCore.Game.WorldState.Time.GameTimePacker"/>).
    /// </summary>
    public static byte[] BuildTimeSpeed(DateTimeOffset local)
    {
        var writer = new PacketWriter(8);
        writer.WriteUInt32(ArcaneCore.Game.WorldState.Time.GameTimePacker.Pack(local));
        writer.WriteSingle(ArcaneCore.Game.WorldState.Time.GameTimePacker.GameSpeedMinutesPerSecond);
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
}

using ArcaneCore.Protocol;

namespace ArcaneCore.Game;

/// <summary>
/// Builds the SMSG_UPDATE_OBJECT payload that creates a player in the client's world.
/// Format verified against vmangos Object::BuildCreateUpdateBlockForPlayer,
/// BuildMovementUpdate, BuildValuesUpdate and UpdateData::BuildPacket (build &gt; 1.8.4).
/// </summary>
public static class ObjectUpdateBuilder
{
    private const byte UpdateTypeCreateObject2 = 3; // UPDATETYPE_CREATE_OBJECT2
    private const byte UpdateFlagSelf = 0x01;
    private const byte UpdateFlagAll = 0x10;
    private const byte UpdateFlagLiving = 0x20;
    private const byte UpdateFlagHasPosition = 0x40;

    // Vanilla default movement speeds.
    private const float WalkSpeed = 2.5f;
    private const float RunSpeed = 7.0f;
    private const float RunBackSpeed = 4.5f;
    private const float SwimSpeed = 4.722222f;
    private const float SwimBackSpeed = 2.5f;
    private const float TurnRate = 3.141594f;

    /// <summary>Build the SMSG_UPDATE_OBJECT payload for the player viewing itself.</summary>
    public static byte[] BuildSelfCreate(PlayerObject player, uint serverTimeMs)
        => BuildCreate(player, serverTimeMs, self: true);

    /// <summary>Build the SMSG_UPDATE_OBJECT payload that shows this player to a different viewer.</summary>
    public static byte[] BuildOtherCreate(PlayerObject player, uint serverTimeMs)
        => BuildCreate(player, serverTimeMs, self: false);

    private static byte[] BuildCreate(PlayerObject player, uint serverTimeMs, bool self)
    {
        (uint[] values, UpdateMask mask) = BuildValues(player);

        var writer = new PacketWriter(512);
        writer.WriteUInt32(1);    // block count
        writer.WriteByte(0);      // has transport

        // --- create block ---
        writer.WriteByte(UpdateTypeCreateObject2);
        writer.WriteBytes(player.ObjectGuid.ToPacked());
        writer.WriteByte(TypeId.Player);

        // --- movement block (living) ---
        byte updateFlags = UpdateFlagAll | UpdateFlagLiving | UpdateFlagHasPosition;
        if (self)
        {
            updateFlags |= UpdateFlagSelf;
        }

        writer.WriteByte(updateFlags);

        writer.WriteUInt32(0);            // move flags (standing)
        writer.WriteUInt32(serverTimeMs); // movement timestamp
        writer.WriteSingle(player.X);
        writer.WriteSingle(player.Y);
        writer.WriteSingle(player.Z);
        writer.WriteSingle(player.Orientation);
        writer.WriteUInt32(0);            // fall time

        writer.WriteSingle(WalkSpeed);
        writer.WriteSingle(RunSpeed);
        writer.WriteSingle(RunBackSpeed);
        writer.WriteSingle(SwimSpeed);
        writer.WriteSingle(SwimBackSpeed);
        writer.WriteSingle(TurnRate);

        writer.WriteUInt32(1);            // UPDATEFLAG_ALL trailing uint32

        // --- values block ---
        mask.WriteTo(writer);
        for (int i = 0; i < values.Length; i++)
        {
            if (mask.GetBit(i))
            {
                writer.WriteUInt32(values[i]);
            }
        }

        return writer.AsMemory().ToArray();
    }

    private static (uint[] Values, UpdateMask Mask) BuildValues(PlayerObject p)
    {
        uint[] values = new uint[UpdateFields.PlayerEnd];
        var mask = new UpdateMask(UpdateFields.PlayerEnd);

        void SetUInt(int index, uint value)
        {
            values[index] = value;
            mask.SetBit(index);
        }

        void SetFloat(int index, float value) => SetUInt(index, BitConverter.SingleToUInt32Bits(value));

        void SetGuid(int index, ulong guid)
        {
            SetUInt(index, (uint)(guid & 0xFFFFFFFF));
            SetUInt(index + 1, (uint)(guid >> 32));
        }

        SetGuid(UpdateFields.ObjectFieldGuid, p.Guid);
        SetUInt(UpdateFields.ObjectFieldType, TypeMask.PlayerObject);
        SetFloat(UpdateFields.ObjectFieldScaleX, p.Scale);

        SetUInt(UpdateFields.UnitFieldHealth, p.Health);
        SetUInt(UpdateFields.UnitFieldMaxHealth, p.MaxHealth);
        SetUInt(UpdateFields.UnitFieldPower1, p.Power);
        SetUInt(UpdateFields.UnitFieldMaxPower1, p.MaxPower);
        SetUInt(UpdateFields.UnitFieldLevel, p.Level);
        SetUInt(UpdateFields.UnitFieldFactionTemplate, p.FactionTemplate);
        SetUInt(UpdateFields.UnitFieldBytes0, PackBytes(
            (byte)p.Race, (byte)p.Class, (byte)p.Gender, (byte)p.PowerType));
        SetUInt(UpdateFields.UnitFieldFlags, (uint)UnitFlags.PlayerControlled);
        SetUInt(UpdateFields.UnitFieldBaseAttackTime, 2000);
        SetUInt(UpdateFields.UnitFieldBaseAttackTime + 1, 2000);
        SetFloat(UpdateFields.UnitFieldBoundingRadius, p.BoundingRadius);
        SetFloat(UpdateFields.UnitFieldCombatReach, p.CombatReach);
        SetUInt(UpdateFields.UnitFieldDisplayId, p.DisplayId);
        SetUInt(UpdateFields.UnitFieldNativeDisplayId, p.DisplayId);
        SetUInt(UpdateFields.UnitFieldBytes1, 0);
        SetFloat(UpdateFields.UnitModCastSpeed, 1.0f);
        SetUInt(UpdateFields.UnitFieldBaseHealth, p.MaxHealth);
        SetUInt(UpdateFields.UnitFieldBaseMana, p.MaxPower);
        SetUInt(UpdateFields.UnitFieldBytes2, 0);

        SetUInt(UpdateFields.PlayerFlags, 0);
        SetUInt(UpdateFields.PlayerBytes, PackBytes(p.Skin, p.Face, p.HairStyle, p.HairColor));
        SetUInt(UpdateFields.PlayerBytes2, PackBytes(p.FacialHair, 0, 0, 0));
        SetUInt(UpdateFields.PlayerBytes3, (byte)p.Gender);
        SetUInt(UpdateFields.PlayerXp, 0);
        SetUInt(UpdateFields.PlayerNextLevelXp, 400);
        SetUInt(UpdateFields.PlayerFieldCoinage, p.Coinage);
        SetUInt(UpdateFields.PlayerFieldBytes, 0);

        return (values, mask);
    }

    private static uint PackBytes(byte b0, byte b1, byte b2, byte b3)
        => (uint)(b0 | (b1 << 8) | (b2 << 16) | (b3 << 24));
}

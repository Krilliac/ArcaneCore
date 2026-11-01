using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Groups;

/// <summary>
/// Values used by the 1.12 party stats packets. Visible aura slots come from UNIT_FIELD_AURA
/// (SpellSystem.Auras.cs); vmangos@0e3ff01 src/game/Handlers/GroupHandler.cpp:599-755 reads
/// those same fields, with 32 positive and 16 negative slots.
/// </summary>
internal sealed class GroupMemberStatsSnapshot
{
    private const int AuraSlots = 48; // vmangos MAX_AURAS; SpellSystem.MaxAuras

    public ObjectGuid Guid { get; private set; }
    public GroupMemberStatus Status { get; private set; }
    public uint Hp { get; private set; }
    public uint MaxHp { get; private set; }
    public PowerType Power { get; private set; }
    public uint CurrentPower { get; private set; }
    public uint MaxPower { get; private set; }
    public byte Level { get; private set; }
    public uint Zone { get; private set; }
    public short X { get; private set; }
    public short Y { get; private set; }
    public uint[] Auras { get; } = new uint[AuraSlots];
    public ObjectGuid PetGuid { get; private set; }
    public string PetName { get; private set; } = string.Empty;
    public uint PetDisplayId { get; private set; }
    public uint PetHp { get; private set; }
    public uint PetMaxHp { get; private set; }
    public PowerType PetPower { get; private set; }
    public uint PetCurrentPower { get; private set; }
    public uint PetMaxPower { get; private set; }
    public uint[] PetAuras { get; } = new uint[AuraSlots];

    /// <summary>A fresh snapshot of <paramref name="player"/>; the aura arrays are owned by the result.</summary>
    public static GroupMemberStatsSnapshot Capture(Player player)
    {
        var snapshot = new GroupMemberStatsSnapshot();
        snapshot.CaptureFrom(player);
        return snapshot;
    }

    /// <summary>
    /// Overwrites every field of this instance (including the aura arrays, in place) with the current values of
    /// <paramref name="player"/>. The group stats pass keeps two instances per member and refills the spare one,
    /// so a steady pass allocates nothing. Nothing outside the caller may hold this instance across the refill:
    /// packet builders copy values into the packet and do not retain the snapshot.
    /// World-thread only.
    /// </summary>
    public void CaptureFrom(Player player)
    {
        var pet = player.GetPet();
        Guid = player.Guid;
        Status = GroupPackets.StatusOf(player);
        Hp = player.Health;
        MaxHp = player.MaxHealth;
        Power = player.PowerType;
        CurrentPower = ReadPower(player, UpdateFields.UnitFieldPower1);
        MaxPower = ReadPower(player, UpdateFields.UnitFieldMaxpower1);
        Level = player.Level;
        Zone = player.ZoneId;
        X = (short)player.X;
        Y = (short)player.Y;
        ReadAuras(player, Auras);
        PetGuid = pet?.Guid ?? ObjectGuid.Empty;
        PetName = pet is null ? string.Empty : pet.Summon?.Charm?.Name ?? pet.Template.Name; // the name the pet name query answers
        PetDisplayId = pet?.DisplayId ?? 0;
        PetHp = pet?.Health ?? 0;
        PetMaxHp = pet?.MaxHealth ?? 0;
        PetPower = pet?.PowerType ?? 0;
        PetCurrentPower = pet is null ? 0 : ReadPower(pet, UpdateFields.UnitFieldPower1);
        PetMaxPower = pet is null ? 0 : ReadPower(pet, UpdateFields.UnitFieldMaxpower1);
        if (pet is null)
        {
            Array.Clear(PetAuras);
        }
        else
        {
            ReadAuras(pet, PetAuras);
        }
    }

    /// <summary>vmangos@0e3ff01 src/game/Handlers/GroupHandler.cpp:757-773: changed fields, including vacated aura slots.</summary>
    public GroupUpdateFlags Diff(GroupMemberStatsSnapshot before)
    {
        GroupUpdateFlags flags = GroupUpdateFlags.None;
        if (Status != before.Status) { flags |= GroupUpdateFlags.Status; }
        if (Hp != before.Hp) { flags |= GroupUpdateFlags.CurrentHp; }
        if (MaxHp != before.MaxHp) { flags |= GroupUpdateFlags.MaxHp; }
        if (Power != before.Power) { flags |= GroupUpdateFlags.PowerType | GroupUpdateFlags.CurrentPower | GroupUpdateFlags.MaxPower; }
        if (CurrentPower != before.CurrentPower) { flags |= GroupUpdateFlags.CurrentPower; }
        if (MaxPower != before.MaxPower) { flags |= GroupUpdateFlags.MaxPower; }
        if (Level != before.Level) { flags |= GroupUpdateFlags.Level; }
        if (Zone != before.Zone) { flags |= GroupUpdateFlags.Zone; }
        if (X != before.X || Y != before.Y) { flags |= GroupUpdateFlags.Position; }
        if (AuraChanged(Auras, before.Auras, 0, 32)) { flags |= GroupUpdateFlags.Auras; }
        if (AuraChanged(Auras, before.Auras, 32, 16)) { flags |= GroupUpdateFlags.AurasNegative; }

        if (PetGuid != before.PetGuid)
        {
            flags |= GroupUpdateFlags.PetGuid | GroupUpdateFlags.PetName | GroupUpdateFlags.PetModelId
                | GroupUpdateFlags.PetCurrentHp | GroupUpdateFlags.PetMaxHp | GroupUpdateFlags.PetPowerType
                | GroupUpdateFlags.PetCurrentPower | GroupUpdateFlags.PetMaxPower
                | GroupUpdateFlags.PetAuras | GroupUpdateFlags.PetAurasNegative;
        }
        else
        {
            if (PetName != before.PetName) { flags |= GroupUpdateFlags.PetName; }
            if (PetDisplayId != before.PetDisplayId) { flags |= GroupUpdateFlags.PetModelId; }
            if (PetHp != before.PetHp) { flags |= GroupUpdateFlags.PetCurrentHp; }
            if (PetMaxHp != before.PetMaxHp) { flags |= GroupUpdateFlags.PetMaxHp; }
            if (PetPower != before.PetPower) { flags |= GroupUpdateFlags.PetPowerType | GroupUpdateFlags.PetCurrentPower | GroupUpdateFlags.PetMaxPower; }
            if (PetCurrentPower != before.PetCurrentPower) { flags |= GroupUpdateFlags.PetCurrentPower; }
            if (PetMaxPower != before.PetMaxPower) { flags |= GroupUpdateFlags.PetMaxPower; }
            if (AuraChanged(PetAuras, before.PetAuras, 0, 32)) { flags |= GroupUpdateFlags.PetAuras; }
            if (AuraChanged(PetAuras, before.PetAuras, 32, 16)) { flags |= GroupUpdateFlags.PetAurasNegative; }
        }

        return flags;
    }

    private static uint ReadPower(Unit unit, int firstField)
    {
        int index = (int)unit.PowerType is >= 0 and <= 4 ? (int)unit.PowerType : 0;
        return unit.GetUInt32(firstField + index);
    }

    private static void ReadAuras(Unit unit, uint[] auras)
    {
        for (int i = 0; i < auras.Length; i++)
        {
            auras[i] = unit.GetUInt32(UpdateFields.UnitFieldAura + i);
        }
    }

    private static bool AuraChanged(uint[] current, uint[] previous, int start, int count)
    {
        for (int i = start; i < start + count; i++)
        {
            if (current[i] != previous[i])
            {
                return true;
            }
        }

        return false;
    }
}

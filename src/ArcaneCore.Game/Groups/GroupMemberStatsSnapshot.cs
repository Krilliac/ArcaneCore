using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Groups;

/// <summary>
/// Values used by the 1.12 party stats packets. Visible aura slots come from UNIT_FIELD_AURA
/// (SpellSystem.Auras.cs); D:\refs\vmangos\src\game\Handlers\GroupHandler.cpp:599-755 reads
/// those same fields, with 32 positive and 16 negative slots.
/// </summary>
internal sealed class GroupMemberStatsSnapshot
{
    private const int AuraSlots = 48; // vmangos MAX_AURAS; SpellSystem.MaxAuras

    public required ObjectGuid Guid { get; init; }
    public required GroupMemberStatus Status { get; init; }
    public required uint Hp { get; init; }
    public required uint MaxHp { get; init; }
    public required PowerType Power { get; init; }
    public required uint CurrentPower { get; init; }
    public required uint MaxPower { get; init; }
    public required byte Level { get; init; }
    public required uint Zone { get; init; }
    public required short X { get; init; }
    public required short Y { get; init; }
    public required uint[] Auras { get; init; }
    public required ObjectGuid PetGuid { get; init; }
    public required string PetName { get; init; }
    public required uint PetDisplayId { get; init; }
    public required uint PetHp { get; init; }
    public required uint PetMaxHp { get; init; }
    public required PowerType PetPower { get; init; }
    public required uint PetCurrentPower { get; init; }
    public required uint PetMaxPower { get; init; }
    public required uint[] PetAuras { get; init; }

    public static GroupMemberStatsSnapshot Capture(Player player)
    {
        var pet = player.GetPet();
        return new GroupMemberStatsSnapshot
        {
            Guid = player.Guid,
            Status = GroupPackets.StatusOf(player),
            Hp = player.Health,
            MaxHp = player.MaxHealth,
            Power = player.PowerType,
            CurrentPower = ReadPower(player, UpdateFields.UnitFieldPower1),
            MaxPower = ReadPower(player, UpdateFields.UnitFieldMaxpower1),
            Level = player.Level,
            Zone = player.ZoneId,
            X = (short)player.X,
            Y = (short)player.Y,
            Auras = ReadAuras(player),
            PetGuid = pet?.Guid ?? ObjectGuid.Empty,
            PetName = pet is null ? string.Empty : pet.Summon?.Charm?.Name ?? pet.Template.Name, // the name the pet name query answers
            PetDisplayId = pet?.DisplayId ?? 0,
            PetHp = pet?.Health ?? 0,
            PetMaxHp = pet?.MaxHealth ?? 0,
            PetPower = pet?.PowerType ?? 0,
            PetCurrentPower = pet is null ? 0 : ReadPower(pet, UpdateFields.UnitFieldPower1),
            PetMaxPower = pet is null ? 0 : ReadPower(pet, UpdateFields.UnitFieldMaxpower1),
            PetAuras = pet is null ? new uint[AuraSlots] : ReadAuras(pet),
        };
    }

    /// <summary>D:\refs\vmangos\src\game\Handlers\GroupHandler.cpp:757-773: changed fields, including vacated aura slots.</summary>
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

    private static uint[] ReadAuras(Unit unit)
    {
        var auras = new uint[AuraSlots];
        for (int i = 0; i < auras.Length; i++)
        {
            auras[i] = unit.GetUInt32(UpdateFields.UnitFieldAura + i);
        }

        return auras;
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

namespace ArcaneCore.Game.WorldState.Zones;

/// <summary>vmangos <c>AreaTeams</c> (Database/DBCEnums.h:46-51); 6 ("other") means never enforced.</summary>
public static class AreaTeams
{
    public const uint None = 0;
    public const uint Ally = 2;
    public const uint Horde = 4;
}

/// <summary>vmangos <c>AreaFlags</c> (Database/DBCEnums.h:53-67), the bits of <c>AreaTemplate.Flags</c>.</summary>
[Flags]
public enum AreaFlags : uint
{
    Snow = 0x1,
    Unk1 = 0x2,
    Unk2 = 0x4,
    SlaveCapital = 0x8,
    Unk3 = 0x10,
    SlaveCapital2 = 0x20,
    Duel = 0x40,
    Arena = 0x80,
    Capital = 0x100,
    City = 0x200,
}

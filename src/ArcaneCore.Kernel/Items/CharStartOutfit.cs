namespace ArcaneCore.Kernel.Items;

/// <summary>One build-5875 CharStartOutfit.dbc row: packed race/class/gender and twelve item slots.</summary>
public sealed record CharStartOutfit(uint RaceClassGender, IReadOnlyList<int> ItemIds)
{
    public const int ItemSlotCount = 12;

    public byte Race => (byte)(RaceClassGender & 0xFF);
    public byte Class => (byte)((RaceClassGender >> 8) & 0xFF);
    public byte Gender => (byte)((RaceClassGender >> 16) & 0xFF);
}

namespace ArcaneCore.Game.Reputation;

/// <summary>
/// vmangos reputation price rounding. Every discounted amount (vendor list and buy, trainer list and buy,
/// repair, each taxi leg) is <c>uint32(amount * GetReputationPriceDiscount() + 0.5f)</c> in single precision:
/// ItemHandler.cpp:763, NPCHandler.cpp:114 and 309, Player.cpp:4955 (repair), 17977 and 17997 (taxi legs),
/// 18445 (vendor buy). Rounds half up per amount; never floors and never ceils a sum.
/// </summary>
public static class ReputationPricing
{
    public static uint Round(ulong price, float discount)
    {
        float value = (float)price * discount + 0.5f;
        if (!(value > 0f))
        {
            return 0;
        }

        return value >= uint.MaxValue ? uint.MaxValue : (uint)value;
    }
}

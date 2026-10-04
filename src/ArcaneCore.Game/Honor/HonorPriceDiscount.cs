namespace ArcaneCore.Game.Honor;

/// <summary>
/// The honor part of vmangos' <c>Player::GetReputationPriceDiscount</c> (Player.cpp:19470-19510). Only vendors of the ten
/// capital and battleground factions react: at visual rank 3 and above a vendor price drops another 10 %, on top of the
/// Honored discount (additive: 0.8, not 0.81); a flight master takes 5 % off at visual rank 2 and 5 % more at rank 4.
/// The subtraction is applied step by step in single precision, like the original, because the price is floored afterwards.
/// </summary>
public static class HonorPriceDiscount
{
    // Alliance: Stormwind, Ironforge, Gnomeregan, Darnassus, Stormpike Guard. Horde: Undercity, Orgrimmar, Thunder Bluff, Darkspear, Frostwolf Clan.
    private static readonly HashSet<uint> Factions = [72, 47, 54, 69, 730, 68, 76, 81, 530, 729];

    /// <summary>Whether a vendor of <paramref name="factionId"/> gives the honor discount at all.</summary>
    public static bool Applies(uint factionId) => Factions.Contains(factionId);

    /// <summary>Take the honor discount off <paramref name="multiplier"/> (1.0 is full price).</summary>
    public static float Apply(float multiplier, sbyte visualRank, uint factionId, bool taxi)
    {
        if (!Applies(factionId))
        {
            return multiplier;
        }

        if (!taxi && visualRank >= 3)
        {
            multiplier -= 0.1f;
        }

        if (taxi && visualRank >= 2)
        {
            multiplier -= 0.05f;
            if (visualRank >= 4)
            {
                multiplier -= 0.05f;
            }
        }

        return multiplier;
    }
}

using ArcaneCore.Kernel.WorldData.GameObjects;

namespace ArcaneCore.Game.Loot;

/// <summary>
/// Creature/item/chest money generation, re-implemented from vmangos Loot::GenerateMoneyLoot
/// (D:\refs\vmangos\src\game\LootMgr.cpp:735-746): nothing when max is 0; max when max &lt;= min
/// (the 3 classic-db rows with max &lt; min pay max); a plain uniform roll when the range is under
/// 32700; otherwise the roll is made on the values shifted right 8 bits and shifted back, so large
/// boss purses are multiples of 256. The money rate is applied (truncating) before the shift back.
/// </summary>
public static class LootMoneyRules
{
    /// <summary>vmangos threshold (max - min) below which the plain roll is used.</summary>
    public const uint ShiftedRangeThreshold = 32700;

    public static uint Generate(uint minAmount, uint maxAmount, float rate, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (maxAmount == 0)
        {
            return 0;
        }

        if (maxAmount <= minAmount)
        {
            return Scale(maxAmount, rate);
        }

        if (maxAmount - minAmount < ShiftedRangeThreshold)
        {
            return Scale(UniformInclusive(random, minAmount, maxAmount), rate);
        }

        return Scale(UniformInclusive(random, minAmount >> 8, maxAmount >> 8), rate) << 8;
    }

    /// <summary>
    /// The money a game object's loot carries: the template's <c>mingold..maxgold</c> through <see cref="Generate"/>, exactly as
    /// the reference core rolls <c>generateMoneyLoot(goInfo->MinMoneyLoot, goInfo->MaxMoneyLoot)</c> right after filling the
    /// object's loot (mangos Object/PlayerLoot.cpp:222-229). That call sits behind <c>if (!lootid) break;</c>, so an object
    /// without a loot id pays nothing even when its template names a gold range. Fishing nodes take the fishing branch there
    /// and never reach it; the fishing area has its own use handler and does not call this.
    /// </summary>
    public static uint GenerateForGameObject(GameObjectTemplate template, uint lootId, float rate, Random random)
    {
        ArgumentNullException.ThrowIfNull(template);
        return lootId == 0 ? 0 : Generate(template.MinGold, template.MaxGold, rate, random);
    }

    private static uint UniformInclusive(Random random, uint min, uint max) => (uint)random.NextInt64(min, (long)max + 1);

    // vmangos: uint32(value * rate), a float multiply truncated; clamp instead of wrapping on absurd rates.
    private static uint Scale(uint value, float rate)
    {
        double scaled = value * (double)rate;
        return scaled <= 0 ? 0 : scaled >= uint.MaxValue ? uint.MaxValue : (uint)scaled;
    }
}

using ArcaneCore.Game.Economy;
using Xunit;

namespace ArcaneCore.Game.Tests.Economy;

/// <summary>
/// Deposit and cut arithmetic against vmangos single-precision math
/// (D:\refs\vmangos\src\game\AuctionHouse\AuctionHouseMgr.cpp:98-110 GetAuctionDeposit, :845-848 GetAuctionCut).
/// Expected values were computed with float32 operations in the vmangos order from item_template SellPrice rows of
/// D:\refs\classic-db\Full_DB\ClassicDB_1_12_1_z2815.sql.gz; integer math differs by 1 copper in these rows.
/// </summary>
public sealed class AuctionFormulaTests
{
    private static readonly AuctionHouseEntry Neutral = new(AuctionHouseRules.NeutralHouse, 75, 15);
    private static readonly AuctionHouseEntry Faction = new(AuctionHouseRules.AllianceHouse, 15, 5);

    [Theory]
    [InlineData(104729u, 1u, 1440u, 942560u)] // item 2801: integer math gives 942561
    [InlineData(261407u, 1u, 480u, 784220u)]  // integer math gives 784221
    [InlineData(95241u, 1u, 1440u, 857168u)]  // integer math gives 857169
    public void Deposit_UsesSinglePrecisionInVmangosOrder(uint sellPrice, uint count, uint minutes, uint expected)
        => Assert.Equal(expected, AuctionHouseRules.Deposit(Neutral, sellPrice, count, minutes));

    [Fact]
    public void Deposit_MinimumIsAppliedBeforeTheRate()
    {
        // vmangos: if (deposit < min) deposit = min; return uint32(deposit * rate)  (cpp:101-109)
        Assert.Equal(200u, AuctionHouseRules.Deposit(Faction, 0, 1, 120, minimum: 100, rate: 2f));
        Assert.Equal(30u, AuctionHouseRules.Deposit(Faction, 100, 1, 120, rate: 2f));
    }

    [Fact]
    public void Cut_UsesTheRate_AndKeepsA64BitProductOnPurpose()
    {
        Assert.Equal(10u, AuctionHouseRules.Cut(Faction, 100, rate: 2f));
        // vmangos wraps cutPercent*bid in uint32 for bids above ~286M at 15%; ArcaneCore deliberately does not.
        Assert.Equal(15_000_000u, AuctionHouseRules.Cut(Neutral, 100_000_000));
        // Above 2^32 the product is no longer exact in float32 (spacing 512): within float precision of 45,000,000.
        Assert.InRange(AuctionHouseRules.Cut(Neutral, 300_000_000), 44_999_900u, 45_000_100u);
    }
}

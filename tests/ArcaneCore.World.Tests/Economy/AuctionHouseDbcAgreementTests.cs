using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game.Economy;
using ArcaneCore.World.Tests.ClientData;
using Xunit;

namespace ArcaneCore.World.Tests.Economy;

/// <summary>
/// The built-in auction houses (<see cref="EconomyOptions.AuctionHouses"/>) against the client's own AuctionHouse.dbc (vmangos
/// AuctionHouseEntryfmt "niiixxxxxxxxx": id, faction, m_depositRate, m_consignmentRate), which vmangos reads for the deposit and the cut.
/// Skipped without ARCANECORE_TEST_DBC_DIR.
/// </summary>
public sealed class AuctionHouseDbcAgreementTests
{
    [RealClientDbcFact]
    public void TheBuiltInAuctionHouses_AreTheClientsAuctionHouse()
    {
        DbcFile dbc = DbcFile.Load(Path.Combine(RealClientDbcFactAttribute.DbcDirectory, "AuctionHouse.dbc"));
        var rows = Enumerable.Range(0, dbc.RecordCount).ToDictionary(r => dbc.GetUInt32(r, 0), r => (Deposit: dbc.GetUInt32(r, 2), Cut: dbc.GetUInt32(r, 3)));
        foreach (AuctionHouseEntry house in new EconomyOptions().AuctionHouses)
        {
            Assert.Equal((rows[house.Id].Deposit, rows[house.Id].Cut), (house.DepositPercent, house.CutPercent));
        }
    }

    [Fact]
    public void TheDefaults_AreTheBuild5875ClientValues()
        => Assert.Equal(
            [(2u, 5u, 5u), (6u, 5u, 5u), (7u, 25u, 15u)],
            new EconomyOptions().AuctionHouses.Select(h => (h.Id, h.DepositPercent, h.CutPercent)));
}

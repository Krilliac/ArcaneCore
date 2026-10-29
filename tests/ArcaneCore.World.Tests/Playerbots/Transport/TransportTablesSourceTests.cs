using ArcaneCore.Kernel.Tuning;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Transport;

/// <summary>
/// Transport rows are the source of the bot network: a replaced catalog reaches the readers (ship classification, lift regions, tram map),
/// the enum values the rows store hold, and every test restores the catalog that was active (it is process-wide).
/// </summary>
public sealed class TransportTablesSourceTests
{
    [Fact]
    public void Row_codes_are_the_playerbot_enum_values()
    {
        Assert.Equal(TuningCatalog.ShipMeansBoat, (byte)TravelMeans.Boat);
        Assert.Equal(TuningCatalog.ShipMeansZeppelin, (byte)TravelMeans.Zeppelin);
        Assert.Equal(TuningCatalog.AccessBoth, (byte)TravelAccess.Both);
        Assert.Equal(TuningCatalog.AccessAlliance, (byte)TravelAccess.Alliance);
        Assert.Equal(TuningCatalog.AccessHorde, (byte)TravelAccess.Horde);
    }

    [Fact]
    public void A_catalog_changes_what_the_readers_derive_from_it()
    {
        TuningCatalog catalog = TuningCatalog.Build(TuningDefaults.Content with
        {
            Ships = [new ShipRouteRecord(777, TuningCatalog.ShipMeansZeppelin, TuningCatalog.AccessAlliance, "Test route")],
            Elevators = [],
            Trams = [new TramNetworkRecord(400, 33f)],
        });

        Assert.Equal(new ShipClass(TravelMeans.Zeppelin, TravelAccess.Alliance, "Test route"), Assert.Single(PlayerbotShipNetwork.ClassificationOf(catalog)).Value);
        Assert.Empty(ElevatorRegions.From(catalog));
        Assert.Equal(7, ElevatorRegions.From(TuningCatalog.Shipped).Length);
    }

    [Fact]
    public void Without_a_tram_row_no_network_is_built()
    {
        TuningCatalog none = TuningCatalog.Build(TuningDefaults.Content with { Trams = [] });

        Assert.Null(TramNetwork.Build([], _ => null, none));
        Assert.Null(none.Tram);
    }
}

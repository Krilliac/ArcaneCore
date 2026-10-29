using System.Numerics;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Transport;

/// <summary>
/// Pins the retail transport geometry the bots use (ship routes, the regions each lift joins, the Deeprun Tram's map and pairing distance)
/// through the surfaces the bot code reads, so they hold whether the numbers are written in code or come from the tuning rows.
/// </summary>
public sealed class TransportTablesPinTests
{
    [Fact]
    public void Ship_classification_is_pinned()
    {
        (uint Entry, TravelMeans Means, TravelAccess Access, string Name)[] expected =
        [
            (20808, TravelMeans.Boat, TravelAccess.Both, "Booty Bay - Ratchet"),
            (176231, TravelMeans.Boat, TravelAccess.Alliance, "Menethil Harbor - Theramore Isle"),
            (176310, TravelMeans.Boat, TravelAccess.Alliance, "Menethil Harbor - Auberdine"),
            (176244, TravelMeans.Boat, TravelAccess.Alliance, "Auberdine - Rut'theran Village"),
            (177233, TravelMeans.Boat, TravelAccess.Alliance, "Forgotten Coast - Feathermoon Stronghold"),
            (164871, TravelMeans.Zeppelin, TravelAccess.Horde, "Orgrimmar - Undercity"),
            (175080, TravelMeans.Zeppelin, TravelAccess.Horde, "Grom'gol - Orgrimmar"),
            (176495, TravelMeans.Zeppelin, TravelAccess.Horde, "Grom'gol - Undercity"),
        ];
        Assert.Equal(expected.Length, PlayerbotShipNetwork.Classic.Count);
        foreach ((uint entry, TravelMeans means, TravelAccess access, string name) in expected)
        {
            ShipClass kind = PlayerbotShipNetwork.Classic[entry];
            Assert.Equal((means, access, name), (kind.Means, kind.Access, kind.Name));
        }
    }

    [Fact]
    public void Elevator_regions_are_pinned_in_order()
    {
        (uint Map, float X, float Y, uint LowZone, uint LowArea, uint HighZone, uint HighArea, bool Mesa, string Name, bool Vault)[] expected =
        [
            (1, -4668f, -1839f, 400, 0, 17, 0, false, "the Great Lift", false),
            (1, -5391f, -2497f, 400, 0, 400, 484, true, "the Freewind Post lift", false),
            (1, -1297f, 188f, 215, 0, 1638, 0, true, "the Thunder Bluff lift", false),
            (1, -1033f, -39f, 215, 0, 1638, 0, true, "the Thunder Bluff lift", false),
            (0, 1596f, 292f, 1497, 0, 85, 0, false, "the Undercity lift", true),
            (0, 1544f, 241f, 1497, 0, 85, 0, false, "the Undercity lift", true),
            (0, 1595f, 189f, 1497, 0, 85, 0, false, "the Undercity lift", true),
        ];
        Assert.Equal(expected.Length, ElevatorRegions.Known.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            ElevatorRegions row = ElevatorRegions.Known[i];
            var want = expected[i];
            Assert.Equal((want.Map, new Vector2(want.X, want.Y), new ElevatorRegion(want.LowZone, want.LowArea), new ElevatorRegion(want.HighZone, want.HighArea),
                want.Mesa, want.Name, want.Vault), (row.MapId, row.At, row.Low, row.High, row.MesaTop, row.Name, row.VaultLow));
        }
    }

    [Fact]
    public void Tram_map_and_pairing_distance_are_pinned()
    {
        Assert.Equal(369u, TramNetwork.TramMapId);
        Assert.Equal(60f, TramNetwork.PairYards);
    }
}

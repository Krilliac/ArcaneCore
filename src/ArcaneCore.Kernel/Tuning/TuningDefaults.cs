namespace ArcaneCore.Kernel.Tuning;

/// <summary>
/// The tuning rows the server ships (the same rows as <c>sql/content/world/&lt;provider&gt;/0002_tuning_tables.sql</c>, which a test keeps
/// equal). They are the seed of an empty tuning table at world start and the catalog a process uses before any database was read
/// (tools, unit tests); once the tables hold rows, the rows are the source.
/// <para>
/// Auction bot values and loot: cMaNGOS ahbot.conf.dist.in defaults (AuctionHouseBot.Value.*, AuctionHouseBot.Loot.*). Transport routes
/// and lifts: retail 1.12 geometry, classic-db entries and AreaTable.dbc (5875) zone and area ids.
/// </para>
/// </summary>
public static class TuningDefaults
{
    /// <summary>Value percents by quality (poor .. artifact) and item class 0..16.</summary>
    private static readonly int[][] ValueMatrix =
    [
        [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
        [100, 100, 0, 100, 0, 100, 100, 100, 0, 100, 0, 100, 100, 100, 0, 100, 100],
        [200, 200, 200, 200, 200, 200, 200, 200, 0, 200, 0, 200, 200, 200, 0, 200, 200],
        [400, 400, 400, 400, 400, 400, 400, 400, 0, 400, 0, 400, 400, 400, 0, 400, 400],
        [800, 800, 800, 800, 800, 800, 800, 800, 0, 800, 0, 800, 800, 800, 0, 800, 800],
        [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
        [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
    ];

    public static TuningContent Content { get; } = new()
    {
        Values = [.. ValueMatrix.SelectMany((row, quality) => row.Select((percent, itemClass) =>
            new AuctionBotValueRecord((byte)quality, (byte)itemClass, percent)))],
        Loot =
        [
            new("creature normal", 30, 35, 8, 12),
            new("creature elite", 30, 34, 1, 2),
            new("creature rare elite", -10, 2, 1, 1),
            new("creature world boss", -20, 1, 1, 1),
            new("creature rare", 0, 10, 1, 1),
            new("disenchant", 10, 12, 1, 1),
            new("fishing", 3, 5, 30, 40),
            new("gameobject", 13, 16, 7, 11),
            new("skinning", 3, 5, 50, 50),
        ],
        Ships =
        [
            new(20808, TuningCatalog.ShipMeansBoat, TuningCatalog.AccessBoth, "Booty Bay - Ratchet"),
            new(164871, TuningCatalog.ShipMeansZeppelin, TuningCatalog.AccessHorde, "Orgrimmar - Undercity"),
            new(175080, TuningCatalog.ShipMeansZeppelin, TuningCatalog.AccessHorde, "Grom'gol - Orgrimmar"),
            new(176231, TuningCatalog.ShipMeansBoat, TuningCatalog.AccessAlliance, "Menethil Harbor - Theramore Isle"),
            new(176244, TuningCatalog.ShipMeansBoat, TuningCatalog.AccessAlliance, "Auberdine - Rut'theran Village"),
            new(176310, TuningCatalog.ShipMeansBoat, TuningCatalog.AccessAlliance, "Menethil Harbor - Auberdine"),
            new(176495, TuningCatalog.ShipMeansZeppelin, TuningCatalog.AccessHorde, "Grom'gol - Undercity"),
            new(177233, TuningCatalog.ShipMeansBoat, TuningCatalog.AccessAlliance, "Forgotten Coast - Feathermoon Stronghold"),
        ],
        Elevators =
        [
            new(1, 1, -4668f, -1839f, 400, 0, 17, 0, false, false, "the Great Lift"),
            new(2, 1, -5391f, -2497f, 400, 0, 400, 484, true, false, "the Freewind Post lift"),
            new(3, 1, -1297f, 188f, 215, 0, 1638, 0, true, false, "the Thunder Bluff lift"),
            new(4, 1, -1033f, -39f, 215, 0, 1638, 0, true, false, "the Thunder Bluff lift"),
            new(5, 0, 1596f, 292f, 1497, 0, 85, 0, false, true, "the Undercity lift"),
            new(6, 0, 1544f, 241f, 1497, 0, 85, 0, false, true, "the Undercity lift"),
            new(7, 0, 1595f, 189f, 1497, 0, 85, 0, false, true, "the Undercity lift"),
        ],
        Trams = [new(369, 60f)],
    };
}

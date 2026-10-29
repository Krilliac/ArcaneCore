namespace ArcaneCore.Kernel.Tuning;

/// <summary>One row of <c>auction_bot_value</c>: the percent of the vendor price the auction bot asks for items of one quality and item class.</summary>
public sealed record AuctionBotValueRecord(byte Quality, byte ItemClass, int Percent);

/// <summary>
/// One row of <c>auction_bot_loot</c>: the "minTemplates, maxTemplates, minRolls, maxRolls" setting of one loot source of the seller
/// (<see cref="TuningCatalog.LootSources"/> names them).
/// </summary>
public sealed record AuctionBotLootRecord(string Source, int MinTemplates, int MaxTemplates, int MinRolls, int MaxRolls);

/// <summary>
/// One row of <c>playerbot_ship_route</c>: a <c>gameobject_template</c> entry bots may ride as a ship. <see cref="Means"/> is the
/// playerbot travel means (2 boat, 3 zeppelin; <see cref="TuningCatalog.ShipMeansBoat"/>), <see cref="Access"/> who may ride it
/// (0 both, 1 alliance, 2 horde).
/// </summary>
public sealed record ShipRouteRecord(uint Entry, byte Means, byte Access, string Name);

/// <summary>
/// One row of <c>playerbot_elevator_region</c>: an elevator the bots use, found by its centre (<see cref="X"/>, <see cref="Y"/>) on
/// <see cref="MapId"/>, and the AreaTable zone/area (area 0 = any area of the zone) of its lower and upper level. Rows are read in
/// <see cref="Id"/> order and the first whose centre is close enough to a lift is that lift's.
/// </summary>
public sealed record ElevatorRegionRecord(int Id, uint MapId, float X, float Y, uint LowZone, uint LowArea, uint HighZone, uint HighArea,
    bool MesaTop, bool VaultLow, string Name);

/// <summary>One row of <c>playerbot_tram_network</c>: the Deeprun Tram's map and how close an exit's landing must be to an entrance to be its way back.</summary>
public sealed record TramNetworkRecord(uint MapId, float PairYards);

/// <summary>Every row of every tuning table, in key order (what <see cref="ITuningContentStore"/> reads and a reload validates).</summary>
public sealed record TuningContent
{
    public IReadOnlyList<AuctionBotValueRecord> Values { get; init; } = [];

    public IReadOnlyList<AuctionBotLootRecord> Loot { get; init; } = [];

    public IReadOnlyList<ShipRouteRecord> Ships { get; init; } = [];

    public IReadOnlyList<ElevatorRegionRecord> Elevators { get; init; } = [];

    public IReadOnlyList<TramNetworkRecord> Trams { get; init; } = [];

    public static TuningContent Empty { get; } = new();

    public int RowCount => Values.Count + Loot.Count + Ships.Count + Elevators.Count + Trams.Count;
}

/// <summary>Reads (and, on an empty database, seeds) the tuning tables of the world database.</summary>
public interface ITuningContentStore
{
    /// <summary>Every row of every tuning table, in key order.</summary>
    Task<TuningContent> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Insert the rows of <paramref name="defaults"/> into every tuning table that holds no row at all (a table with any row is left as
    /// the operator has it). Returns the number of rows written.
    /// </summary>
    Task<int> SeedEmptyTablesAsync(TuningContent defaults, CancellationToken cancellationToken = default);
}

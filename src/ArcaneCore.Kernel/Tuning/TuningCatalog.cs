using System.Globalization;

namespace ArcaneCore.Kernel.Tuning;

/// <summary>A tuning row the loader refused, with the reason (the table, the row's key as text).</summary>
public sealed record TuningRejection(string Table, string Key, string Reason)
{
    public override string ToString() => $"{Table} {Key}: {Reason}";
}

/// <summary>
/// The validated, immutable tuning tables (docs/superpowers/specs/2026-10-28-rule-engine-design.md, phase P5: plain data, not rules):
/// the auction house bot's value and loot matrices and the playerbot transport networks (ship routes, lift regions, the Deeprun Tram).
/// <see cref="Build"/> checks every row and keeps the good ones, listing the others in <see cref="Rejected"/> (the startup load runs
/// with what is good; a reload with any rejection is refused whole, as for the rule tables). Thread affinity: immutable after
/// <see cref="Build"/>, so any thread may read it; <see cref="Active"/> is the one catalog the consumers read, swapped atomically.
/// </summary>
public sealed class TuningCatalog
{
    /// <summary><c>playerbot_ship_route.means</c> of a boat (the playerbot travel means value).</summary>
    public const byte ShipMeansBoat = 2;

    /// <summary><c>playerbot_ship_route.means</c> of a zeppelin.</summary>
    public const byte ShipMeansZeppelin = 3;

    public const byte AccessBoth = 0;
    public const byte AccessAlliance = 1;
    public const byte AccessHorde = 2;

    /// <summary>Item qualities an auction value row names (poor .. artifact).</summary>
    public const int QualityCount = 7;

    /// <summary>Item classes an auction value row names (consumable .. 16).</summary>
    public const int ItemClassCount = 17;

    /// <summary>The highest percent a value row may hold (a sanity bound; the bot caps prices at the client limit anyway).</summary>
    public const int MaxValuePercent = 100_000;

    private static long s_generations;

    private readonly int[][] _values;
    private readonly Dictionary<string, int[]> _loot;

    private TuningCatalog(int[][] values, Dictionary<string, int[]> loot, IReadOnlyList<ShipRouteRecord> ships,
        IReadOnlyList<ElevatorRegionRecord> elevators, TramNetworkRecord? tram, IReadOnlyList<TuningRejection> rejected, int rowCount)
    {
        _values = values;
        _loot = loot;
        Ships = ships;
        Elevators = elevators;
        Tram = tram;
        Rejected = rejected;
        RowCount = rowCount;
        Generation = Interlocked.Increment(ref s_generations);
    }

    /// <summary>The names an <c>auction_bot_loot.source</c> may take, in the order the seller rolls them.</summary>
    public static IReadOnlyList<string> LootSources { get; } =
    [
        "creature normal", "creature elite", "creature rare elite", "creature world boss", "creature rare",
        "disenchant", "fishing", "gameobject", "skinning",
    ];

    /// <summary>The catalog of <see cref="TuningDefaults.Content"/>: what a process uses before it read a database.</summary>
    public static TuningCatalog Shipped { get; } = Build(TuningDefaults.Content);

    private static TuningCatalog s_active = Shipped;

    /// <summary>The catalog consumers read now (<see cref="Shipped"/> until a world loads its tables).</summary>
    public static TuningCatalog Active => Volatile.Read(ref s_active);

    /// <summary>Make <paramref name="catalog"/> the active one; returns the one it replaced.</summary>
    public static TuningCatalog Replace(TuningCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return Interlocked.Exchange(ref s_active, catalog);
    }

    /// <summary>Differs for every catalog built in this process; consumers that cache what they derive compare it.</summary>
    public long Generation { get; }

    /// <summary>The good ship routes, by entry.</summary>
    public IReadOnlyList<ShipRouteRecord> Ships { get; }

    /// <summary>The good lift regions, in id order.</summary>
    public IReadOnlyList<ElevatorRegionRecord> Elevators { get; }

    /// <summary>The Deeprun Tram's row, or null when no row was good (the tram network is then not built).</summary>
    public TramNetworkRecord? Tram { get; }

    public IReadOnlyList<TuningRejection> Rejected { get; }

    /// <summary>The rows read (good and rejected).</summary>
    public int RowCount { get; }

    public string Summary => string.Create(CultureInfo.InvariantCulture,
        $"{_values.Sum(q => q.Count(v => v > 0))} auction value cells, {_loot.Count} loot sources, {Ships.Count} ship routes, {Elevators.Count} lift regions, "
        + $"{(Tram is null ? "no" : "the")} tram network, {Rejected.Count} rejected rows");

    /// <summary>The percent of the vendor price for a quality and item class (0 when the cell has no row or is out of range).</summary>
    public uint Value(int quality, int itemClass)
        => (uint)quality < QualityCount && (uint)itemClass < ItemClassCount ? (uint)_values[quality][itemClass] : 0;

    /// <summary>A copy of the value matrix, [quality][item class].</summary>
    public uint[][] ValueMatrix() => [.. _values.Select(row => row.Select(v => (uint)v).ToArray())];

    /// <summary>
    /// The "minTemplates, maxTemplates, minRolls, maxRolls" setting of a loot source (a fresh array), or "0,0,0,0" (the source is off)
    /// when it has no good row.
    /// </summary>
    public int[] LootConfig(string source)
        => _loot.TryGetValue(source, out int[]? config) ? (int[])config.Clone() : new int[4];

    /// <summary>Validate <paramref name="content"/> and keep the good rows.</summary>
    public static TuningCatalog Build(TuningContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var rejected = new List<TuningRejection>();

        int[][] values = [.. Enumerable.Range(0, QualityCount).Select(_ => new int[ItemClassCount])];
        var seenCells = new HashSet<(byte, byte)>();
        foreach (AuctionBotValueRecord row in content.Values)
        {
            string reason = row.Quality >= QualityCount ? $"quality must be 0..{QualityCount - 1}"
                : row.ItemClass >= ItemClassCount ? $"item_class must be 0..{ItemClassCount - 1}"
                : row.Percent is < 0 or > MaxValuePercent ? $"percent must be 0..{MaxValuePercent}"
                : !seenCells.Add((row.Quality, row.ItemClass)) ? "duplicate quality and item class"
                : string.Empty;
            if (reason.Length > 0)
            {
                rejected.Add(new TuningRejection("auction_bot_value", $"{row.Quality}/{row.ItemClass}", reason));
                continue;
            }

            values[row.Quality][row.ItemClass] = row.Percent;
        }

        var loot = new Dictionary<string, int[]>(StringComparer.Ordinal);
        foreach (AuctionBotLootRecord row in content.Loot)
        {
            string reason = !LootSources.Contains(row.Source, StringComparer.Ordinal) ? $"unknown source (one of: {string.Join(", ", LootSources)})"
                : row.MaxTemplates < 0 ? "max_templates must not be negative"
                : row.MinTemplates > row.MaxTemplates ? "min_templates must be at most max_templates"
                : row.MinRolls < 0 ? "min_rolls must not be negative"
                : row.MinRolls > row.MaxRolls ? "min_rolls must be at most max_rolls"
                : loot.ContainsKey(row.Source) ? "duplicate source"
                : string.Empty;
            if (reason.Length > 0)
            {
                rejected.Add(new TuningRejection("auction_bot_loot", row.Source, reason));
                continue;
            }

            loot[row.Source] = [row.MinTemplates, row.MaxTemplates, row.MinRolls, row.MaxRolls];
        }

        var ships = new List<ShipRouteRecord>();
        var seenShips = new HashSet<uint>();
        foreach (ShipRouteRecord row in content.Ships.OrderBy(r => r.Entry))
        {
            string reason = row.Entry == 0 ? "entry must not be 0"
                : row.Means is not (ShipMeansBoat or ShipMeansZeppelin) ? $"means must be {ShipMeansBoat} (boat) or {ShipMeansZeppelin} (zeppelin)"
                : row.Access > AccessHorde ? "access must be 0 (both), 1 (alliance) or 2 (horde)"
                : string.IsNullOrWhiteSpace(row.Name) ? "name must not be empty"
                : !seenShips.Add(row.Entry) ? "duplicate entry"
                : string.Empty;
            if (reason.Length > 0)
            {
                rejected.Add(new TuningRejection("playerbot_ship_route", row.Entry.ToString(CultureInfo.InvariantCulture), reason));
                continue;
            }

            ships.Add(row);
        }

        var elevators = new List<ElevatorRegionRecord>();
        var seenElevators = new HashSet<int>();
        foreach (ElevatorRegionRecord row in content.Elevators.OrderBy(r => r.Id))
        {
            string reason = !float.IsFinite(row.X) || !float.IsFinite(row.Y) ? "x and y must be finite"
                : row.LowZone == 0 || row.HighZone == 0 ? "low_zone and high_zone must not be 0 (a level needs a zone)"
                : string.IsNullOrWhiteSpace(row.Name) ? "name must not be empty"
                : !seenElevators.Add(row.Id) ? "duplicate id"
                : string.Empty;
            if (reason.Length > 0)
            {
                rejected.Add(new TuningRejection("playerbot_elevator_region", row.Id.ToString(CultureInfo.InvariantCulture), reason));
                continue;
            }

            elevators.Add(row);
        }

        TramNetworkRecord? tram = null;
        foreach (TramNetworkRecord row in content.Trams.OrderBy(r => r.MapId))
        {
            string reason = !float.IsFinite(row.PairYards) || row.PairYards <= 0f ? "pair_yards must be a positive number"
                : tram is not null ? "only one tram network is supported; the lowest map_id is used"
                : string.Empty;
            if (reason.Length > 0)
            {
                rejected.Add(new TuningRejection("playerbot_tram_network", row.MapId.ToString(CultureInfo.InvariantCulture), reason));
                continue;
            }

            tram = row;
        }

        return new TuningCatalog(values, loot, ships, elevators, tram, rejected, content.RowCount);
    }
}

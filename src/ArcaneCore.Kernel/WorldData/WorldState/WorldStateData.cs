namespace ArcaneCore.Kernel.WorldData.WorldState;

/// <summary>
/// One <c>game_weather</c> row (vmangos Weather.cpp:446-505): the zone and 12 chances in column
/// order spring, summer, fall, winter by rain, snow, storm (percent).
/// </summary>
public sealed record GameWeatherRecord(uint Zone, IReadOnlyList<uint> Chances);

/// <summary>One <c>exploration_basexp</c> row: base exploration XP of a level.</summary>
public sealed record ExplorationBaseXpRecord(uint Level, uint BaseXp);

/// <summary>The world-state tables the world daemon loads at startup.</summary>
public sealed record WorldStateContent(IReadOnlyList<GameWeatherRecord> Weather, IReadOnlyList<ExplorationBaseXpRecord> BaseXp)
{
    public static WorldStateContent Empty { get; } = new([], []);
}

/// <summary>Reads the world-state tables (scoped; implemented over the world database).</summary>
public interface IWorldStateDataStore
{
    Task<WorldStateContent> LoadAsync(CancellationToken cancellationToken = default);
}

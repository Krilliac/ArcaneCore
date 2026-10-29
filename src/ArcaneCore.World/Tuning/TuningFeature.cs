using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Tuning;
using ArcaneCore.World.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Tuning;

/// <summary>
/// The plain tuning tables in the world daemon (rule engine phase P5): at start it seeds an empty tuning table with the shipped rows
/// (<see cref="TuningDefaults"/>), reads the tables through the optional <see cref="ITuningContentStore"/>, validates them
/// (<see cref="TuningCatalog.Build"/>), logs the first 20 rejected rows and a summary, and makes the result the
/// <see cref="TuningCatalog.Active"/> catalog the auction bot and the bot transport code read. Without a store the shipped catalog
/// stays. <c>.reload tuning</c> replaces it (<see cref="TuningContentReloadable"/>).
/// </summary>
public sealed class TuningFeature(IServiceScopeFactory scopes, ILogger<TuningFeature> logger) : IWorldFeature
{
    /// <summary>How many rejected rows the startup log and a reload's notes name; the rest are counted.</summary>
    public const int LoggedRejections = 20;

    public TuningCatalog Catalog => TuningCatalog.Active;

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        using IServiceScope scope = scopes.CreateScope();
        if (scope.ServiceProvider.GetService<ITuningContentStore>() is not { } store)
        {
            logger.LogInformation("Tuning: no tuning store; the shipped tuning rows are used");
            return;
        }

        int seeded = store.SeedEmptyTablesAsync(TuningDefaults.Content).GetAwaiter().GetResult();
        if (seeded > 0)
        {
            logger.LogInformation("Tuning: seeded {Rows} shipped rows into empty tuning tables", seeded);
        }

        TuningContent content = store.LoadAsync().GetAwaiter().GetResult();
        foreach (string missing in MissingTables(content))
        {
            logger.LogWarning("Tuning: table {Table} holds no rows; its shipped behaviour is off (apply sql/content/world/<provider>/0003_tuning_tables.sql or restart to seed it)", missing);
        }

        TuningCatalog catalog = TuningCatalog.Build(content);
        TuningCatalog.Replace(catalog);
        foreach (TuningRejection rejection in catalog.Rejected.Take(LoggedRejections))
        {
            logger.LogWarning("Tuning row skipped: {Rejection}", rejection);
        }

        logger.LogInformation("Tuning: {Summary}{More}", catalog.Summary,
            catalog.Rejected.Count > LoggedRejections ? $" ({catalog.Rejected.Count - LoggedRejections} more rejections)" : "");
    }

    /// <summary>The tuning tables <paramref name="content"/> holds no row of (the shipped content has rows in every one).</summary>
    public static IReadOnlyList<string> MissingTables(TuningContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var missing = new List<string>();
        if (content.Values.Count == 0) missing.Add("auction_bot_value");
        if (content.Loot.Count == 0) missing.Add("auction_bot_loot");
        if (content.Ships.Count == 0) missing.Add("playerbot_ship_route");
        if (content.Elevators.Count == 0) missing.Add("playerbot_elevator_region");
        if (content.Trams.Count == 0) missing.Add("playerbot_tram_network");
        return missing;
    }

    /// <summary>Read the tuning tables (no store registered: none). Off the world thread.</summary>
    public async Task<TuningContent> LoadAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopes.CreateScope();
        return scope.ServiceProvider.GetService<ITuningContentStore>() is { } store
            ? await store.LoadAsync(cancellationToken).ConfigureAwait(false)
            : TuningDefaults.Content;
    }
}

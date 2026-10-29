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

        TuningCatalog catalog = TuningCatalog.Build(store.LoadAsync().GetAwaiter().GetResult());
        TuningCatalog.Replace(catalog);
        foreach (TuningRejection rejection in catalog.Rejected.Take(LoggedRejections))
        {
            logger.LogWarning("Tuning row skipped: {Rejection}", rejection);
        }

        logger.LogInformation("Tuning: {Summary}{More}", catalog.Summary,
            catalog.Rejected.Count > LoggedRejections ? $" ({catalog.Rejected.Count - LoggedRejections} more rejections)" : "");
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

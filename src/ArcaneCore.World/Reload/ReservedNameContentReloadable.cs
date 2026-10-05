using ArcaneCore.Data.Content.Names;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Reload;
using ArcaneCore.Kernel.WorldData.Names;
using ArcaneCore.World.Names;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Frozen;

namespace ArcaneCore.World.Reload;

/// <summary>
/// <c>.reload reserved_name</c>: rebuilds the SQL exact-name snapshot off the world thread and
/// publishes it as one complete immutable name policy on the world thread. Startup-loaded DBC
/// regex rules and their provenance remain part of every replacement.
/// </summary>
public sealed class ReservedNameContentReloadable(IServiceProvider services) : IContentReloadable
{
    public string Name => "reserved_name";

    public bool IncludedInAll => services.GetRequiredService<NameCatalogFeature>().HasSqlStore;

    public async Task<ContentCandidate> BuildAsync(CancellationToken cancellationToken)
    {
        NameCatalogFeature feature = services.GetRequiredService<NameCatalogFeature>();
        using IServiceScope scope = services.CreateScope();
        IReservedNameStore store = scope.ServiceProvider.GetService<IReservedNameStore>()
            ?? throw new InvalidOperationException("no reserved name store is registered");
        IReadOnlySet<string> loaded = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        // Snapshot the producer result before leaving the worker phase. A mutable test or custom
        // store must not be able to alter a candidate after it has been queued for world-thread publish.
        IReadOnlySet<string> reservedExact = loaded.ToFrozenSet(StringComparer.Ordinal);
        return new ReservedNameCandidate(feature, reservedExact);
    }

    private sealed class ReservedNameCandidate(NameCatalogFeature feature, IReadOnlySet<string> reservedExact) : ContentCandidate
    {
        public override string Summary => $"{reservedExact.Count} reserved names";

        public override void Commit(WorldRuntime world, ReloadTransaction transaction)
        {
            NameCatalog previous = feature.Catalog;
            transaction.Step("reserved name catalog", () => feature.ReplaceReservedExact(reservedExact), () => feature.RestoreCatalog(previous));
        }
    }
}

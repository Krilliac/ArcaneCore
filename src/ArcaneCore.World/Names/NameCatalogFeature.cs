using ArcaneCore.Data.Content.Names;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Features;
using ArcaneCore.World.Pets;
using ArcaneCore.Kernel.WorldData.Names;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Names;

public sealed class NameCatalogFeature(IServiceProvider services) : IWorldFeature
{
    private NameCatalog _catalog = NameCatalog.Empty;

    /// <summary>The complete immutable policy currently used by character and pet name validation.</summary>
    public NameCatalog Catalog => Volatile.Read(ref _catalog);

    public bool HasSqlStore { get; private set; }

    public void Attach(WorldRuntime world)
    {
        IConfiguration? configuration = services.GetService<IConfiguration>();
        NameCatalogOptions options = new();
        configuration?.GetSection(NameCatalogOptions.SectionName).Bind(options);
        IReadOnlySet<string> reservedExact;
        using (IServiceScope scope = services.CreateScope())
        {
            IReservedNameStore? store = scope.ServiceProvider.GetService<IReservedNameStore>();
            HasSqlStore = store is not null;
            reservedExact = store?.LoadAsync().GetAwaiter().GetResult()
                ?? new HashSet<string>(StringComparer.Ordinal);
        }
        if (string.IsNullOrWhiteSpace(options.NamesProfanityDbcPath) && string.IsNullOrWhiteSpace(options.NamesReservedDbcPath))
        {
            Publish(new NameCatalog([], [], [], reservedExact));
            InstallVeto(services);
            return;
        }
        if (string.IsNullOrWhiteSpace(options.NamesProfanityDbcPath) || string.IsNullOrWhiteSpace(options.NamesReservedDbcPath))
            throw new InvalidOperationException("Names:NamesProfanityDbcPath and Names:NamesReservedDbcPath must be set together");

        NameCatalogSource profanitySource = NamesDbcReader.Read(options.NamesProfanityDbcPath, "NamesProfanity.dbc", out var profanity);
        NameCatalogSource reservedSource = NamesDbcReader.Read(options.NamesReservedDbcPath, "NamesReserved.dbc", out var reserved);
        Publish(new NameCatalog(profanity, reserved, [profanitySource, reservedSource], reservedExact));
        InstallVeto(services);
    }

    /// <summary>Publishes a complete replacement while retaining the startup-loaded DBC rules and provenance.</summary>
    public void ReplaceReservedExact(IReadOnlySet<string> reservedExact)
    {
        ArgumentNullException.ThrowIfNull(reservedExact);
        NameCatalog current = Catalog;
        Publish(current.WithReservedExact(reservedExact));
    }

    internal void RestoreCatalog(NameCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Publish(catalog);
    }

    private void Publish(NameCatalog catalog) => Interlocked.Exchange(ref _catalog, catalog);

    private void InstallVeto(IServiceProvider services)
    {
        PetNameRules rules = services.GetRequiredService<PetNamingFeature>().Rules;
        Func<string, bool>? prior = rules.ExternalVeto;
        rules.ExternalVeto = name => (prior?.Invoke(name) ?? true) && Catalog.Check(name) == NameCatalogResult.Allowed;
    }
}

using ArcaneCore.Data.Content.Items;
using ArcaneCore.Data.Items;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Items;

/// <summary>The "ItemRandomProperties" configuration section (docs/areas/items.md, "Random properties").</summary>
public sealed class ItemRandomPropertyOptions
{
    public const string SectionName = "ItemRandomProperties";

    /// <summary>
    /// The build-5875 ItemRandomProperties.dbc (16 fields), supplied by the developer and never downloaded by the daemon. Empty: no item gets a
    /// random property. The file is read only when the item_enchantment_template table has rows; a file that is unreadable or has another layout then refuses startup.
    /// </summary>
    public string? DbcPath { get; set; }
}

/// <summary>
/// Random item properties in the world daemon: loads ItemRandomProperties.dbc and the world database's <c>item_enchantment_template</c>
/// (<see cref="IItemEnchantmentTemplateStore"/>; filled by <c>arcane-content-importer import</c> or <c>refresh</c>) once at startup into the
/// immutable <see cref="ItemRandomPropertyCatalog"/> and hands <see cref="ItemRandomProperties"/> to the items feature, which gives it to every
/// inventory at login; new items of a template with <c>random_property</c> then roll their "of the …" property (vmangos
/// Item::GenerateItemRandomPropertyId). Not hot-reloadable (the client's own DBC cannot change under a running world).
/// </summary>
public sealed class ItemRandomPropertyFeature : IWorldFeature
{
    private readonly IServiceProvider _services;
    private readonly ILogger<ItemRandomPropertyFeature> _logger;

    public ItemRandomPropertyFeature(IServiceProvider services, ILogger<ItemRandomPropertyFeature> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Options = new ItemRandomPropertyOptions();
        services.GetService<IConfiguration>()?.GetSection(ItemRandomPropertyOptions.SectionName).Bind(Options);
    }

    public ItemRandomPropertyOptions Options { get; }

    /// <summary>The generator in force, or null when random properties are off.</summary>
    public ItemRandomProperties? Properties { get; private set; }

    public void Attach(WorldRuntime world)
    {
        if (string.IsNullOrWhiteSpace(Options.DbcPath))
        {
            _logger.LogWarning("ItemRandomProperties:DbcPath is not set: new items get no random property");
            return;
        }

        // Fail closed: a configured file or a database that cannot be read refuses startup.
        IReadOnlyList<ItemEnchantmentChance> chances = [];
        using (IServiceScope scope = _services.GetRequiredService<IServiceScopeFactory>().CreateScope())
        {
            if (scope.ServiceProvider.GetService<IItemEnchantmentTemplateStore>() is { } store)
            {
#pragma warning disable ARC0006 // synchronous IWorldFeature.Attach runs once at startup before the world thread ticks and the realm accepts connections, so blocking the startup thread is intended (fail closed)
                chances = store.LoadAsync().GetAwaiter().GetResult();
#pragma warning restore ARC0006
            }
        }

        if (chances.Count == 0)
        {
            _logger.LogWarning("The world database has no item_enchantment_template rows (import the content dump): new items get no random property");
            return;
        }

        var catalog = new ItemRandomPropertyCatalog(ItemRandomPropertiesDbcReader.Load(Options.DbcPath), chances);
        Properties = new ItemRandomProperties(catalog, report: message => _logger.LogWarning("{Message}", message));
        _services.GetRequiredService<ItemsFeature>().RandomProperties = Properties;
        _logger.LogInformation("Loaded {Properties} random properties and {Groups} item_enchantment_template entries", catalog.PropertyCount, catalog.GroupCount);
    }
}

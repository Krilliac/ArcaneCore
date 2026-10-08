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
    /// random property. A configured file that is unreadable or has another layout refuses startup.
    /// </summary>
    public string? DbcPath { get; set; }

    /// <summary>
    /// A MySQL world dump (vmangos or cmangos classic-db, plain or .gz) holding <c>item_enchantment_template</c>; only that table is read. Both
    /// this and <see cref="DbcPath"/> are needed for random properties. A configured dump that cannot be read refuses startup.
    /// </summary>
    public string? EnchantmentTemplateDumpPath { get; set; }
}

/// <summary>
/// Random item properties in the world daemon: loads ItemRandomProperties.dbc and <c>item_enchantment_template</c> once at startup into the
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
        bool dbc = !string.IsNullOrWhiteSpace(Options.DbcPath);
        bool dump = !string.IsNullOrWhiteSpace(Options.EnchantmentTemplateDumpPath);
        if (!dbc || !dump)
        {
            _logger.LogWarning("ItemRandomProperties:DbcPath and ItemRandomProperties:EnchantmentTemplateDumpPath are not both set: new items get no random property");
            return;
        }

        // Fail closed: configured files that cannot be read refuse startup.
        var catalog = new ItemRandomPropertyCatalog(
            ItemRandomPropertiesDbcReader.Load(Options.DbcPath!), ItemEnchantmentTemplateDumpReader.Load(Options.EnchantmentTemplateDumpPath!));
        Properties = new ItemRandomProperties(catalog, report: message => _logger.LogWarning("{Message}", message));
        _services.GetRequiredService<ItemsFeature>().RandomProperties = Properties;
        _logger.LogInformation("Loaded {Properties} random properties and {Groups} item_enchantment_template entries", catalog.PropertyCount, catalog.GroupCount);
    }
}

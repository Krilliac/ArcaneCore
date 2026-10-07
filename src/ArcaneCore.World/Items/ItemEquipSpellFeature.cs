using ArcaneCore.Data.Items;
using ArcaneCore.Game.Items.ItemSets;
using ArcaneCore.Game.Items.ItemUse;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Features;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Items;

/// <summary>The "ItemSets" configuration section (docs/areas/items.md).</summary>
public sealed class ItemSetOptions
{
    public const string SectionName = "ItemSets";

    /// <summary>
    /// The build-5875 ItemSet.dbc (45 fields), supplied by the developer and never downloaded by the daemon. Empty: no item set bonuses
    /// (the ON_EQUIP item spells still work). A configured file that is unreadable or has another layout refuses startup.
    /// </summary>
    public string? DbcPath { get; set; }
}

/// <summary>
/// ON_EQUIP item spells and item set bonuses in the world daemon: loads ItemSet.dbc into the immutable <see cref="ItemSetCatalog"/> and
/// attaches <see cref="ItemEquipSpells"/> to each player at login (world thread, from <see cref="SpellFeature.PlayerSpellsRestored"/>, so
/// the saved auras are already back), where it follows the inventory's equip hook and replays the worn items so logout and login re-apply
/// every bonus. The catalog is read once at startup (not part of hot reload: the client's own DBC cannot change under a running world).
/// </summary>
public sealed class ItemEquipSpellFeature : IWorldFeature
{
    private readonly IServiceProvider _services;
    private readonly ILogger<ItemEquipSpellFeature> _logger;
    private readonly HashSet<uint> _reportedSets = [];

    public ItemEquipSpellFeature(IServiceProvider services, ILogger<ItemEquipSpellFeature> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Options = Bind(services.GetService<IConfiguration>());
    }

    public ItemSetOptions Options { get; }

    /// <summary>The item set content; <see cref="ItemSetCatalog.Empty"/> without a configured file.</summary>
    public ItemSetCatalog Catalog { get; private set; } = ItemSetCatalog.Empty;

    /// <summary>The equip binding handed to every player (valid once attached).</summary>
    public ItemEquipSpells? Spells { get; private set; }

    public static ItemSetOptions Bind(IConfiguration? configuration)
    {
        var options = new ItemSetOptions();
        configuration?.GetSection(ItemSetOptions.SectionName).Bind(options);
        return options;
    }

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        SpellFeature spellFeature = _services.GetRequiredService<SpellFeature>();
        SpellSystem spells = spellFeature.System;
        bool hasCatalog = !string.IsNullOrWhiteSpace(Options.DbcPath);
        if (hasCatalog)
        {
            // Fail closed: a configured file that cannot be read or has another layout refuses startup.
            Catalog = ItemSetDbcReader.Load(Options.DbcPath!);
            _logger.LogInformation("Loaded {Count} item sets from {Path}", Catalog.Count, Options.DbcPath);
        }
        else
        {
            _logger.LogWarning("ItemSets:DbcPath is not set: item set bonuses are off (ON_EQUIP item spells still apply)");
        }

        var bonuses = new ItemSetBonuses(Catalog, spells, hasCatalog ? ReportUnknownSet : null);
        Spells = new ItemEquipSpells(spells, bonuses);
        // The login replay runs after SpellFeature restored the saved auras, not from PlayerLoggedIn: features attach in full-name order
        // (ArcaneCore.World.Items before ArcaneCore.World.Spells), so a PlayerLoggedIn handler here would replay first and the restore of
        // a saved non-passive Equip: aura would then stack on (or replace) the item-bound holder instead of being removed by the replay.
        spellFeature.PlayerSpellsRestored += Spells.Attach;

        // A shapeshift re-checks the worn items' "Equip:" spells against the new form (Player::UpdateEquipSpellsAtFormChange).
        _services.GetService<StanceFeature>()?.AddFormChangeListener(Spells);
    }

    private void ReportUnknownSet(uint setId, uint itemEntry)
    {
        if (_reportedSets.Add(setId))
        {
            _logger.LogWarning("Item {Item} names item set {Set}, which ItemSet.dbc does not list: its set bonuses are not applied", itemEntry, setId);
        }
    }
}

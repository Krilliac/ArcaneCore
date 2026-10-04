using ArcaneCore.Data.Crafting;
using ArcaneCore.Game.Crafting.Enchanting;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Crafting;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Crafting;

/// <summary>The "Enchanting" configuration section (docs/areas/crafting.md). Every default is the retail behaviour.</summary>
public sealed class EnchantingOptions
{
    public const string SectionName = "Enchanting";

    /// <summary>Master switch (default true): false leaves the enchantment engine and the enchant spell effects unregistered.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Build-5875 SpellItemEnchantment.dbc (24 fields). Without it the engine is inactive and says so in the log: no enchantment can be applied or
    /// resolved (a configured file that is unreadable or has another layout refuses startup).
    /// </summary>
    public string? SpellItemEnchantmentDbcPath { get; set; }

    /// <summary>vmangos <c>GM.AllowTrades</c> (default true, World.cpp:680): false keeps a game master's enchant spells from landing (SpellEffects.cpp:3029).</summary>
    public bool GmAllowTrades { get; set; } = true;
}

/// <summary>
/// Enchanting in the world daemon (discovered <see cref="IWorldFeature"/>, docs/areas/crafting.md): loads the enchantment catalog, gives every loading
/// player its <see cref="PlayerEnchantments"/> and the item hook that keeps enchantments paired with the equip state, and ticks the temporary-enchantment
/// timers of every map.
/// </summary>
public sealed class EnchantingFeature(IServiceProvider services, ILogger<EnchantingFeature> logger) : IWorldFeature, ICharacterHooks
{
    private readonly HashSet<Map> _maps = [];
    private SpellFeature? _spells;

    public EnchantingOptions Options { get; } = new();

    /// <summary>The enchantment catalog; <see cref="EnchantCatalog.Empty"/> while inactive.</summary>
    public EnchantCatalog Catalog { get; private set; } = EnchantCatalog.Empty;

    /// <summary>Whether enchantments work (enabled and a catalog loaded).</summary>
    public bool IsActive { get; private set; }

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        services.GetService<IConfiguration>()?.GetSection(EnchantingOptions.SectionName).Bind(Options);
        if (!CraftingFeature.IsEnabled(services.GetService<IConfiguration>()))
        {
            logger.LogInformation("Enchanting: disabled");
            return;
        }

        if (!Options.Enabled)
        {
            logger.LogInformation("Enchanting: disabled");
            EnchantItemSpells.InstallUnavailable(services.GetRequiredService<SpellFeature>().System);
            return;
        }

        EnchantCatalog? catalog = services.GetService<EnchantCatalog>()
            ?? (string.IsNullOrWhiteSpace(Options.SpellItemEnchantmentDbcPath) ? null : EnchantDbcReader.Load(Options.SpellItemEnchantmentDbcPath));
        if (catalog is null)
        {
            logger.LogWarning(
                "Enchanting: no SpellItemEnchantment.dbc is configured (Enchanting:SpellItemEnchantmentDbcPath); enchanting is inactive: enchant casts are refused and consume no reagents");
            EnchantItemSpells.InstallUnavailable(services.GetRequiredService<SpellFeature>().System);
            return;
        }

        Catalog = catalog;
        _spells = services.GetRequiredService<SpellFeature>();
        new EnchantItemSpells(catalog, () => Options.GmAllowTrades, TradeItem).Install(_spells.System);
        IsActive = true;
        world.MapCreated += OnMapCreated;
        foreach (Map map in world.Maps)
        {
            OnMapCreated(map);
        }

        logger.LogInformation("Enchanting: {Count} enchantments", catalog.Count);
    }

    /// <summary>The item a trade-slot target names: the partner's offer in that slot (vmangos SpellCastTargets::Update, SpellCastTargetsInfo.cpp:130-136).</summary>
    private Item? TradeItem(Player player, SpellCastTargets targets)
    {
        if (services.GetService<EconomyFeature>()?.TradeOf(player) is not { } trade || targets.Item.Value >= TradeRules.SlotCount)
        {
            return null;
        }

        TradeSide partner = trade.OtherSide(player);
        var offered = partner[(int)targets.Item.Value];
        return offered.IsEmpty ? null : partner.Player.Inventory.GetItemByGuid(offered);
    }

    private void OnMapCreated(Map map)
    {
        if (_maps.Add(map))
        {
            map.AddUpdater(new EnchantUpdater());
        }
    }

    /// <summary>
    /// Attach the player's enchantment engine and item hook. Items may or may not be loaded yet (hook order is not fixed): the engine applies what is
    /// already worn now and the hook covers every later equip, so either order ends the same.
    /// </summary>
    public Task OnPlayerLoadingAsync(WorldSession session, CharacterRecord character, Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (IsActive && _spells is not null && player.Enchantments is null)
        {
            var enchantments = new PlayerEnchantments(player, Catalog, _spells.System);
            player.AttachEnchantments(enchantments);
            player.Inventory.StatsApplier = new EnchantStatsApplier(player.Inventory.StatsApplier);
            enchantments.Attach();
        }

        return Task.CompletedTask;
    }
}

using ArcaneCore.Data.Characters.Life;
using ArcaneCore.Game;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Progression;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Items;

/// <summary>
/// The items world feature: item content (item_template, playercreateinfo_item) cached in memory
/// on first use (vmangos ObjectMgr::LoadItemPrototypes at startup), the item GUID allocator
/// (vmangos ObjectMgr::m_ItemGuids, seeded from MAX(item_instance.guid)), and the character
/// hooks: starting outfit on creation, equipment on the character list, inventory on login.
/// <para>
/// Without a registered <see cref="IItemStore"/> (a host without the characters database) the
/// hooks do nothing and inventories are never saved. A failed content load is not cached: the
/// next request retries, and the failing request fails (login is refused) instead of running
/// with no items (fails closed).
/// </para>
/// </summary>
public sealed partial class ItemsFeature(IServiceScopeFactory scopes, ILogger<ItemsFeature> logger, IConfiguration? configuration = null) : IWorldFeature, ICharacterHooks
{
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly LiveItemTemplateStore _live = new();
    private volatile IItemTemplateStore? _templates;

    /// <summary>
    /// Item content (empty until <see cref="EnsureLoadedAsync"/> succeeds). A stable view that follows
    /// <see cref="ReplaceTemplates"/>, so a reference kept by an inventory or another feature sees
    /// <c>.reload item_template</c> on its next lookup (docs/areas/hot-reload.md).
    /// </summary>
    public IItemTemplateStore Templates => _live;

    /// <summary>The immutable store lookups currently go to (empty until loaded).</summary>
    public IItemTemplateStore LoadedStore => _templates ?? ItemTemplateStore.Empty;

    /// <summary>Make <paramref name="store"/> the item content (the reload's swap; the world thread).</summary>
    public void ReplaceTemplates(IItemTemplateStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _live.Replace(store);
        _templates = store;
    }

    /// <summary>
    /// Item-mechanics options bound from the <c>Items</c> section (retail defaults when the section
    /// or the configuration is absent); handed to every inventory this feature creates or loads.
    /// </summary>
    public ItemMechanicsOptions Options { get; } = BindOptions(configuration);

    private static ItemMechanicsOptions BindOptions(IConfiguration? configuration)
    {
        var options = new ItemMechanicsOptions();
        configuration?.GetSection(ItemMechanicsOptions.SectionName).Bind(options);
        return options;
    }

    /// <summary>The process-wide item GUID source.</summary>
    public ItemGuidAllocator GuidAllocator { get; } = new();

    /// <summary>The random property roll handed to every inventory at login (set by <see cref="ItemRandomPropertyFeature"/>; null: none).</summary>
    public IItemRandomPropertySource? RandomProperties { get; set; }

    /// <summary>The world, once attached.</summary>
    public WorldRuntime? World { get; private set; }

    public void Attach(WorldRuntime world) => World = world;

    /// <summary>Load item content and seed the GUID allocator once (thread-safe).</summary>
    public async Task<IItemTemplateStore> EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_templates is not null)
        {
            return _live;
        }

        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_templates is not null)
            {
                return _live;
            }

            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            IItemTemplateStore store = ItemTemplateStore.Empty;
            if (scope.ServiceProvider.GetService<IItemTemplateSource>() is { } source)
            {
                IReadOnlyList<ItemTemplate> templates = await source.LoadTemplatesAsync(cancellationToken).ConfigureAwait(false);
                IReadOnlyList<StartingItem> starting = await source.LoadStartingItemsAsync(cancellationToken).ConfigureAwait(false);
                store = new ItemTemplateStore(templates, starting);
            }

            LoadStartingOutfitCatalog();

            if (scope.ServiceProvider.GetService<IItemStore>() is { } items)
            {
                GuidAllocator.Seed(await items.GetMaxItemGuidAsync(cancellationToken).ConfigureAwait(false));
            }

            // vmangos ObjectMgr::SetHighestGuids / CharacterDatabaseCleaner::CleanOrphanedItemData: container loot whose item is gone
            // (left by builds before the escrow paths deleted it with the item) is removed before any character loads.
            if (scope.ServiceProvider.GetService<IItemLootMaintenance>() is { } lootMaintenance)
            {
                int orphans = await lootMaintenance.DeleteOrphanedLootAsync(cancellationToken).ConfigureAwait(false);
                if (orphans > 0)
                {
                    logger.LogWarning("Deleted {Count} orphaned item loot rows (their items no longer exist)", orphans);
                }
            }

            logger.LogInformation("Loaded {Count} item templates", store.Count);
            _live.Replace(store);
            _templates = store;
            return _live;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>vmangos Player::Create → AddStartingItems, saved with the new character.</summary>
    public async Task OnCharacterCreatedAsync(WorldSession session, CharacterRecord character)
    {
        if (session.Services.GetService<IItemStore>() is not { } store)
        {
            return;
        }

        var inventory = new PlayerInventory(ObjectGuid.Player((uint)character.Id), (Race)character.Race, (Class)character.Class, character.Level)
        {
            Templates = await EnsureLoadedAsync().ConfigureAwait(false),
            GuidAllocator = GuidAllocator,
            Options = Options,
        };
        inventory.AddStartingItems(StartingItemsFor(character));
        await store.SaveInventoryAsync(character.Id, inventory.CreateSnapshot()).ConfigureAwait(false);
    }

    /// <summary>vmangos Player::BuildEnumData: display id and inventory type of the 20 visible slots.</summary>
    public async Task<IReadOnlyDictionary<int, CharEnumItem[]>?> GetCharEnumEquipmentAsync(WorldSession session, IReadOnlyList<CharacterRecord> characters)
    {
        if (characters.Count == 0 || session.Services.GetService<IItemStore>() is not { } store)
        {
            return null;
        }

        IItemTemplateStore templates = await EnsureLoadedAsync().ConfigureAwait(false);
        IReadOnlyDictionary<int, IReadOnlyDictionary<byte, uint>> equipped =
            await store.GetEquippedEntriesAsync(characters.Select(c => c.Id).ToList()).ConfigureAwait(false);
        var result = new Dictionary<int, CharEnumItem[]>();
        foreach ((int id, IReadOnlyDictionary<byte, uint> slots) in equipped)
        {
            var items = new CharEnumItem[InventorySlots.CharEnumSlots];
            foreach ((byte slot, uint entry) in slots)
            {
                if (slot < items.Length && templates.Find(entry) is { } template)
                {
                    items[slot] = new CharEnumItem(template.DisplayId, (byte)template.InventoryType);
                }
            }

            result[id] = items;
        }

        return result;
    }

    /// <summary>vmangos Player::LoadFromDB → _LoadInventory, before the player enters the world.</summary>
    public async Task OnPlayerLoadingAsync(WorldSession session, CharacterRecord character, Player player)
    {
        IItemTemplateStore templates = await EnsureLoadedAsync().ConfigureAwait(false);
        player.Inventory.Templates = templates;
        player.Inventory.GuidAllocator = GuidAllocator;
        player.Inventory.Options = Options;
        player.Inventory.RandomProperties = RandomProperties;
        if (session.Services.GetService<IItemStore>() is { } store)
        {
            IReadOnlyList<InventoryItemData> rows = await store.GetInventoryAsync(character.Id).ConfigureAwait(false);
            if (await OfflineSecondsAsync(session, character.Id).ConfigureAwait(false) is { } offline)
            {
                IReadOnlyList<InventoryItemData> kept = ConjuredItems.WithoutVanished(rows, templates, offline);
                if (kept.Count != rows.Count)
                {
                    logger.LogDebug("{Character}: {Count} conjured items vanished after {Seconds} s offline", character.Name, rows.Count - kept.Count, offline);
                    rows = kept;
                }
            }

            player.Inventory.Load(rows);
        }

        if (session.Services.GetService<IItemStateStore>() is { } states)
        {
            player.Inventory.RestoreAmmo(await states.GetAmmoAsync(character.Id).ConfigureAwait(false));
        }
    }

    /// <summary>
    /// Seconds since the character's last stored logout (vmangos <c>characters.logout_time</c>, written by every save), from the rested state
    /// (<see cref="ICharacterRestStore"/>, written at logout and periodically); null when none is stored. A logout write still queued is made
    /// durable first, so a quick relog never reads an older second (which would look like a longer absence).
    /// </summary>
    private static async Task<long?> OfflineSecondsAsync(WorldSession session, int characterId)
    {
        if (session.Services.GetService<ICharacterRestStore>() is not { } rest)
        {
            return null;
        }

        if (session.Services.GetService<RestFeature>() is { } restFeature)
        {
            await restFeature.Writes.FlushCharacterAsync(characterId).ConfigureAwait(false);
        }

        return await rest.LoadAsync(characterId).ConfigureAwait(false) is { } state
            ? DeathHooks.For(session.World).Clock.UnixSeconds - state.LogoutUnixSeconds
            : null;
    }
}

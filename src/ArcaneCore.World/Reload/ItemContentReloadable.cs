using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Reload;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Items;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Reload;

/// <summary>
/// <c>.reload item_template</c> (vmangos <c>HandleReloadItemTemplate</c>,
/// ServerCommands.cpp:1744-1749, Chat.cpp:855 → <c>ObjectMgr::LoadItemPrototypes</c>, ObjectMgr.cpp:3814): the item tables are read and indexed off
/// the world thread, then become the content every lookup by entry goes to
/// (<see cref="ItemsFeature.ReplaceTemplates"/>).
/// <list type="bullet">
/// <item>An empty <c>item_template</c> empties the store: vmangos clears its map first and only then
/// notices the empty result (ObjectMgr.cpp:3817, 3822-3830). <c>HotReload:EmptyTables = KeepLoaded</c> opts into the
/// early-out vmangos' spell and creature loaders have (SpellMgr.cpp:3724-3732, ObjectMgr.cpp:1190-1196).</item>
/// <item>Lookups by entry (the feature, vendors, the economy, every online inventory) see the new
/// content at once. An <see cref="Item"/> already created keeps its creation-time template until its
/// owner logs in again: vmangos resolves <c>Item::GetProto</c> by entry on every call
/// (Item.cpp:567-570); matching that needs <see cref="Item.Template"/> late-bound, a change in the
/// inventory code that is left to a later slice (docs/areas/hot-reload.md).</item>
/// </list>
/// </summary>
public sealed class ItemContentReloadable(IServiceProvider services) : IContentReloadable
{
    public string Name => "item_template";

    /// <summary>vmangos reload all (ServerCommands.cpp:885-905) reaches no item_template: all_item reloads page texts, enchantments and item_required_target only (:996-1002).</summary>
    public bool IncludedInAll => false;

    public async Task<ContentCandidate> BuildAsync(CancellationToken cancellationToken)
    {
        ItemsFeature feature = services.GetRequiredService<ItemsFeature>();
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        IItemTemplateSource source = scope.ServiceProvider.GetService<IItemTemplateSource>()
            ?? throw new InvalidOperationException("no item content source is registered");

        // The first load also seeds the item GUID allocator; a reload must not skip that.
        await feature.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<ItemTemplate> templates = await source.LoadTemplatesAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<StartingItem> starting = await source.LoadStartingItemsAsync(cancellationToken).ConfigureAwait(false);

        // The store is keyed by entry, so two rows for one entry cannot be built.
        string[] duplicates =
        [
            .. templates.GroupBy(t => t.Entry).Where(g => g.Count() > 1).OrderBy(g => g.Key)
                .Select(g => $"item_template has {g.Count()} rows for item {g.Key}"),
        ];
        IItemTemplateStore built = duplicates.Length == 0 ? new ItemTemplateStore(templates, starting) : ItemTemplateStore.Empty;
        return new ItemCandidate(feature, built, duplicates, ReloadPolicy.KeepsEmptyTables(services));
    }

    private sealed class ItemCandidate(ItemsFeature feature, IItemTemplateStore store, IReadOnlyList<string> problems, bool keepEmpty) : ContentCandidate
    {
        public override string Summary => $"{store.Count} item templates";

        public override IReadOnlyList<string> Validate() => problems;

        public override bool TryKeepCurrent(WorldRuntime world, out string reason)
        {
            int loaded = feature.LoadedStore.Count;
            if (keepEmpty && store.Count == 0 && loaded > 0)
            {
                reason = $"item_template is empty and HotReload:EmptyTables is KeepLoaded, {loaded} item templates stay loaded";
                return true;
            }

            reason = string.Empty;
            return false;
        }

        public override void Commit(WorldRuntime world, ReloadTransaction transaction)
        {
            IItemTemplateStore previous = feature.LoadedStore;
            transaction.Step("item templates", () => feature.ReplaceTemplates(store), () => feature.ReplaceTemplates(previous));
        }
    }
}

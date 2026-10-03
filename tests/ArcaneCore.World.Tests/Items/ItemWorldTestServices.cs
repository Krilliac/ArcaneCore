using System.Collections.Concurrent;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Tests.Items;

/// <summary>
/// Item doubles for <see cref="WorldTestHost"/>. They are registered only for hosts started inside
/// <see cref="ItemTestContent.Use"/>, so every other test keeps running without items (no item
/// store: the item hooks do nothing).
/// </summary>
internal sealed class ItemWorldTestServices : IWorldTestServices
{
    public void Register(IServiceCollection services)
    {
        if (ItemTestContent.Current is not { } content)
        {
            return;
        }

        services.AddSingleton<IItemTemplateSource>(content.Templates);
        services.AddSingleton<IItemStore>(content.Items);
        services.AddSingleton<IItemStateStore>(content.Items);

        // The in-memory character store ignores inventories; save them through to the item store.
        var inner = (ICharacterStore)services.Last(d => d.ServiceType == typeof(ICharacterStore)).ImplementationInstance!;
        services.AddSingleton<ICharacterStore>(new InventorySavingCharacterStore(inner, content.Items));
    }
}

/// <summary>The item content and stored inventories of one test host.</summary>
internal sealed class ItemTestContent
{
    private static readonly AsyncLocal<ItemTestContent?> Ambient = new();

    public static ItemTestContent? Current => Ambient.Value;

    public InMemoryItemTemplateSource Templates { get; } = new();

    public InMemoryItemStore Items { get; } = new();

    /// <summary>Hosts started (synchronously) inside the scope get this content.</summary>
    public IDisposable Use()
    {
        Ambient.Value = this;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose() => Ambient.Value = null;
    }
}

internal sealed class InMemoryItemTemplateSource : IItemTemplateSource
{
    public List<ItemTemplate> Templates { get; } = [];

    public List<StartingItem> StartingItems { get; } = [];

    public Task<IReadOnlyList<ItemTemplate>> LoadTemplatesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ItemTemplate>>([.. Templates]);

    public Task<IReadOnlyList<StartingItem>> LoadStartingItemsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<StartingItem>>([.. StartingItems]);
}

internal sealed class InMemoryItemStore : IItemStore, IItemStateStore
{
    private readonly ConcurrentDictionary<int, uint> _ammo = new();

    private readonly ConcurrentDictionary<int, IReadOnlyList<InventoryItemData>> _inventories = new();

    public int SaveCount { get; private set; }

    public IReadOnlyList<InventoryItemData> Get(int characterId) => _inventories.GetValueOrDefault(characterId) ?? [];

    public Task<IReadOnlyList<InventoryItemData>> GetInventoryAsync(int characterId, CancellationToken cancellationToken = default)
        => Task.FromResult(Get(characterId));

    public Task SaveInventoryAsync(int characterId, InventorySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        _inventories[characterId] = [.. snapshot.Items];
        if (snapshot.AmmoId is { } ammo)
        {
            _ammo[characterId] = ammo;
        }

        SaveCount++;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<int, IReadOnlyDictionary<byte, uint>>> GetEquippedEntriesAsync(
        IReadOnlyCollection<int> characterIds, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<int, IReadOnlyDictionary<byte, uint>>();
        foreach (int id in characterIds)
        {
            result[id] = Get(id).Where(r => r.ContainerGuid == 0 && r.Slot < 20).ToDictionary(r => r.Slot, r => r.Item.Entry);
        }

        return Task.FromResult<IReadOnlyDictionary<int, IReadOnlyDictionary<byte, uint>>>(result);
    }

    public Task<uint> GetMaxItemGuidAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_inventories.Values.SelectMany(i => i).Select(r => r.Item.Guid).DefaultIfEmpty(0u).Max());

    public Task<uint> GetAmmoAsync(int characterId, CancellationToken cancellationToken = default)
        => Task.FromResult(_ammo.GetValueOrDefault(characterId));

    public void Delete(int characterId)
    {
        _inventories.TryRemove(characterId, out _);
        _ammo.TryRemove(characterId, out _);
    }
}

/// <summary>Decorates the host's character store so inventory snapshots and deletes reach the item store (as EfCharacterStore does).</summary>
internal sealed class InventorySavingCharacterStore(ICharacterStore inner, InMemoryItemStore items) : ICharacterStore
{
    public Task<IReadOnlyList<CharacterRecord>> GetByAccountAsync(int accountId, CancellationToken cancellationToken = default) => inner.GetByAccountAsync(accountId, cancellationToken);

    public Task<CharacterRecord?> GetByIdAsync(int id, CancellationToken cancellationToken = default) => inner.GetByIdAsync(id, cancellationToken);

    public Task<bool> IsNameTakenAsync(string name, CancellationToken cancellationToken = default) => inner.IsNameTakenAsync(name, cancellationToken);

    public Task<int> CountByAccountAsync(int accountId, CancellationToken cancellationToken = default) => inner.CountByAccountAsync(accountId, cancellationToken);

    public Task<CharacterRecord> CreateAsync(CharacterRecord character, CancellationToken cancellationToken = default) => inner.CreateAsync(character, cancellationToken);

    public async Task<bool> DeleteAsync(int id, int accountId, CancellationToken cancellationToken = default)
    {
        bool deleted = await inner.DeleteAsync(id, accountId, cancellationToken);
        if (deleted)
        {
            items.Delete(id);
        }

        return deleted;
    }

    public async Task SaveStateAsync(CharacterState state, CancellationToken cancellationToken = default)
    {
        await inner.SaveStateAsync(state, cancellationToken);
        if (state.Inventory is { } inventory)
        {
            await items.SaveInventoryAsync(state.Id, inventory, cancellationToken);
        }
    }

    public Task<IReadOnlyList<ActionButton>> GetActionButtonsAsync(int characterId, CancellationToken cancellationToken = default) => inner.GetActionButtonsAsync(characterId, cancellationToken);

    public Task<IReadOnlyList<CharacterIdentity>> GetAllIdentitiesAsync(CancellationToken cancellationToken = default) => inner.GetAllIdentitiesAsync(cancellationToken);
}

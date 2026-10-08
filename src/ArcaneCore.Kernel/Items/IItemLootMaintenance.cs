namespace ArcaneCore.Kernel.Items;

/// <summary>
/// Housekeeping of the generated container loot (<c>item_loot_state</c>, <c>item_loot</c>). The world runs it once when item content loads,
/// before any character is loaded (vmangos ObjectMgr::SetHighestGuids deletes <c>item_loot</c> rows above the highest item guid at startup, and
/// CharacterDatabaseCleaner::CleanOrphanedItemData removes every <c>item_loot</c> row whose <c>item_instance</c> row is gone).
/// </summary>
public interface IItemLootMaintenance
{
    /// <summary>Delete the loot rows of items that no longer exist; returns how many rows went (both tables).</summary>
    Task<int> DeleteOrphanedLootAsync(CancellationToken cancellationToken = default);
}

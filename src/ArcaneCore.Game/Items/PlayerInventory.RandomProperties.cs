namespace ArcaneCore.Game.Items;

public sealed partial class PlayerInventory
{
    /// <summary>
    /// The random property content new items roll from (<see cref="ItemRandomProperties"/>; the items feature sets it at login). Null: new items
    /// get no random property, which is what an install without ItemRandomProperties.dbc gets.
    /// </summary>
    public IItemRandomPropertySource? RandomProperties { get; set; }

    /// <summary>
    /// vmangos StoreNewItem(…, randomPropertyId) with Item::GenerateItemRandomPropertyId at its callers: a new item takes
    /// <paramref name="randomPropertyId"/> when one is given (a loot roll already made, a copy), otherwise a fresh roll; 0 means none.
    /// </summary>
    private void ApplyNewItemRandomProperty(Item item, int? randomPropertyId)
    {
        int id = randomPropertyId ?? RandomProperties?.Generate(item.Template) ?? 0;
        if (id != 0 && RandomProperties?.Find(id) is { } property)
        {
            ItemRandomProperties.Apply(item, property);
        }
    }
}

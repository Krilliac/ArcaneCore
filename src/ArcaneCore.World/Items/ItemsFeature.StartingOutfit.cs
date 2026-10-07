using ArcaneCore.Data.Content.Items;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Items;

public sealed partial class ItemsFeature
{
    private IReadOnlyList<CharStartOutfit>? _startingOutfits;

    /// <summary>
    /// Optional Items:CharStartOutfitDbcPath. DBC rows are applied first, then SQL
    /// playercreateinfo_item rows, matching CMaNGOS Player::Create ordering.
    /// </summary>
    private void LoadStartingOutfitCatalog()
    {
        string? path = configuration?["Items:CharStartOutfitDbcPath"];
        if (string.IsNullOrWhiteSpace(path))
        {
            _startingOutfits = null;
            return;
        }

        // Load once while the existing item load lock is held. Invalid configured content throws
        // and leaves the item feature unloaded, rather than silently running without an outfit.
        _startingOutfits = CharStartOutfitDbcReader.Load(path);
    }

    private IReadOnlyList<StartingItem> StartingItemsFor(CharacterRecord character)
    {
        IReadOnlyList<StartingItem> sql = Templates.StartingItems(character.Race, character.Class);
        if (_startingOutfits is not { } outfits)
        {
            return sql;
        }

        CharStartOutfit? outfit = outfits.FirstOrDefault(row =>
            row.Race == character.Race && row.Class == character.Class && row.Gender == character.Gender);
        if (outfit is null)
        {
            return sql;
        }

        var merged = new List<StartingItem>();
        foreach (int rawItem in outfit.ItemIds)
        {
            if (rawItem <= 0)
            {
                continue;
            }

            if (LoadedStore.Find((uint)rawItem) is not { } item)
            {
                logger.LogWarning("CharStartOutfit.dbc: race {Race} class {Class} gender {Gender} references missing item {Item}",
                    outfit.Race, outfit.Class, outfit.Gender, rawItem);
                continue;
            }

            uint amount = Math.Max(1, item.BuyCount);
            if (item.Class == 0 && item.SubClass == 5 && item.Spells.Count > 0)
            {
                uint category = item.Spells[0].Category;
                if (category == 11 && item.Stackable > 4) amount = 4;
                if (category == 59 && item.Stackable > 2) amount = 2;
            }

            merged.Add(new StartingItem(outfit.Race, outfit.Class, (uint)rawItem, amount));
        }

        merged.AddRange(sql);
        return merged;
    }
}

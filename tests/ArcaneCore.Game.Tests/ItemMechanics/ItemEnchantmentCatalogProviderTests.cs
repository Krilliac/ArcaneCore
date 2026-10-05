using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.WorldData.Items;
using Xunit;

namespace ArcaneCore.Game.Tests.ItemMechanics;

public sealed class ItemEnchantmentCatalogProviderTests
{
    [Fact]
    public void EmptyReplacementClearsOverlay_AndSnapshotRestoreReturnsPreviousCatalog()
    {
        var baseCatalog = new ItemEnchantmentCatalog([new ItemEnchantmentDefinition(1, [])]);
        var provider = new ItemEnchantmentCatalogProvider(baseCatalog);
        provider.Replace([new ItemEnchantmentDefinition(2, [])], [], null);
        var snapshot = provider.Capture();
        provider.Replace([], [], null);
        Assert.NotNull(provider.Current.Find(1));
        Assert.Null(provider.Current.Find(2));
        provider.Restore(snapshot);
        Assert.NotNull(provider.Current.Find(2));
    }

    [Fact]
    public void ReplaceCopiesMutableSourceListsBeforeFuturePartialReplacement()
    {
        var baseCatalog = new ItemEnchantmentCatalog();
        var definitions = new List<ItemEnchantmentDefinition> { new(3, []) };
        var provider = new ItemEnchantmentCatalogProvider(baseCatalog);
        provider.ReplaceDefinitions(definitions, null);
        definitions.Clear();
        provider.ReplaceProcs([new ItemEnchantProc(77, 1)], null);
        Assert.NotNull(provider.Current.Find(3));
    }
}

using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.WorldData.Items;

namespace ArcaneCore.Game.Items;

public interface IItemEnchantmentCatalogProvider
{
    IItemEnchantmentCatalog Current { get; }
}

public sealed class ItemEnchantmentCatalogProvider : IItemEnchantmentCatalogProvider
{
    public readonly record struct Snapshot(IReadOnlyList<ItemEnchantmentDefinition> Definitions, IReadOnlyList<ItemEnchantProc> Procs, IItemEnchantmentCatalog Current);
    private readonly IItemEnchantmentCatalog _baseCatalog;
    private IReadOnlyList<ItemEnchantmentDefinition> _definitions = [];
    private IReadOnlyList<ItemEnchantProc> _procs = [];
    public ItemEnchantmentCatalogProvider(IItemEnchantmentCatalog baseCatalog)
    {
        _baseCatalog = baseCatalog ?? throw new ArgumentNullException(nameof(baseCatalog));
        Current = _baseCatalog;
    }
    public IItemEnchantmentCatalog Current { get; private set; }
    public void ReplaceDefinitions(IReadOnlyList<ItemEnchantmentDefinition> definitions, SpellRankChains? ranks)
    {
        _definitions = [.. definitions];
        Current = _definitions.Count == 0 && _procs.Count == 0
            ? _baseCatalog : new LayeredItemEnchantmentCatalog(_baseCatalog, _definitions, _procs, ranks);
    }

    public void ReplaceProcs(IReadOnlyList<ItemEnchantProc> procs, SpellRankChains? ranks)
    {
        _procs = [.. procs];
        Current = _definitions.Count == 0 && _procs.Count == 0
            ? _baseCatalog : new LayeredItemEnchantmentCatalog(_baseCatalog, _definitions, _procs, ranks);
    }

    public void Replace(IReadOnlyList<ItemEnchantmentDefinition> definitions, IReadOnlyList<ItemEnchantProc> procs, SpellRankChains? ranks)
    {
        _definitions = [.. definitions];
        _procs = [.. procs];
        Current = _definitions.Count == 0 && _procs.Count == 0
            ? _baseCatalog : new LayeredItemEnchantmentCatalog(_baseCatalog, _definitions, _procs, ranks);
    }

    public Snapshot Capture() => new(Array.AsReadOnly(_definitions.ToArray()), Array.AsReadOnly(_procs.ToArray()), Current);
    public void Restore(Snapshot snapshot)
    {
        _definitions = [.. snapshot.Definitions];
        _procs = [.. snapshot.Procs];
        Current = snapshot.Current;
    }
}

using ArcaneCore.Kernel.Skills;

namespace ArcaneCore.Kernel.WorldData.Items;

/// <summary>SpellItemEnchantment.dbc effect type and payload.</summary>
public readonly record struct ItemEnchantmentEffect(uint EffectType, uint SpellId, int Amount);

/// <summary>An immutable SpellItemEnchantment.dbc definition.</summary>
public sealed partial record ItemEnchantmentDefinition
{
    public ItemEnchantmentDefinition(uint entry, IEnumerable<ItemEnchantmentEffect> effects)
    {
        Entry = entry;
        Effects = Array.AsReadOnly(effects?.Take(3).ToArray() ?? throw new ArgumentNullException(nameof(effects)));
    }

    public uint Entry { get; }
    public IReadOnlyList<ItemEnchantmentEffect> Effects { get; }
}

/// <summary>spell_proc_item_enchant override for an enchantment proc spell.</summary>
public readonly record struct ItemEnchantProc(uint SpellId, float PpmRate);

public interface IItemEnchantmentCatalog
{
    ItemEnchantmentDefinition? Find(uint enchantmentId);
    float? PpmRate(uint spellId);
}

public interface IItemEnchantProcStore
{
    Task<IReadOnlyList<ItemEnchantProc>> LoadAsync(CancellationToken cancellationToken = default);
}

/// <summary>Read-only catalog suitable for startup content and synthetic tests.</summary>
public sealed class ItemEnchantmentCatalog : IItemEnchantmentCatalog
{
    private readonly IReadOnlyDictionary<uint, ItemEnchantmentDefinition> _definitions;
    private readonly IReadOnlyDictionary<uint, float> _ppm;
    private readonly SpellRankChains _ranks;

    public ItemEnchantmentCatalog(IEnumerable<ItemEnchantmentDefinition>? definitions = null, IEnumerable<ItemEnchantProc>? procs = null, SpellRankChains? ranks = null)
    {
        _definitions = new Dictionary<uint, ItemEnchantmentDefinition>((definitions ?? []).ToDictionary(x => x.Entry));
        _ppm = new Dictionary<uint, float>((procs ?? []).ToDictionary(x => x.SpellId, x => x.PpmRate));
        _ranks = ranks ?? SpellRankChains.Empty;
    }

    public ItemEnchantmentDefinition? Find(uint enchantmentId)
        => _definitions.TryGetValue(enchantmentId, out ItemEnchantmentDefinition? definition) ? definition : null;

    public float? PpmRate(uint spellId)
    {
        uint first = _ranks.First(spellId);
        if (first != 0) spellId = first;
        return _ppm.TryGetValue(spellId, out float rate) ? rate : null;
    }

    public IEnumerable<ItemEnchantmentDefinition> Definitions => _definitions.Values;
}

public sealed class LayeredItemEnchantmentCatalog : IItemEnchantmentCatalog
{
    private readonly IItemEnchantmentCatalog _baseCatalog;
    private readonly ItemEnchantmentCatalog _overlay;
    public LayeredItemEnchantmentCatalog(IItemEnchantmentCatalog baseCatalog, IEnumerable<ItemEnchantmentDefinition> definitions, IEnumerable<ItemEnchantProc> procs, SpellRankChains? ranks = null)
    {
        _baseCatalog = baseCatalog;
        _overlay = new ItemEnchantmentCatalog(definitions, procs, ranks);
    }
    public ItemEnchantmentDefinition? Find(uint id) => _overlay.Find(id) ?? _baseCatalog.Find(id);
    public float? PpmRate(uint spellId) => _overlay.PpmRate(spellId) ?? _baseCatalog.PpmRate(spellId);
}

public sealed class EmptyItemEnchantmentCatalog : IItemEnchantmentCatalog
{
    public static EmptyItemEnchantmentCatalog Instance { get; } = new();
    public ItemEnchantmentDefinition? Find(uint enchantmentId) => null;
    public float? PpmRate(uint spellId) => null;
}

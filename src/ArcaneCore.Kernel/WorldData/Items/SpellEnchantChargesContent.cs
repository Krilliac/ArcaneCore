namespace ArcaneCore.Kernel.WorldData.Items;

public readonly record struct SpellEnchantCharges(uint SpellId, uint Charges);

public interface ISpellEnchantChargesCatalog
{
    uint? Find(uint spellId);
}

public sealed class SpellEnchantChargesCatalog(IEnumerable<SpellEnchantCharges>? rows = null) : ISpellEnchantChargesCatalog
{
    private readonly IReadOnlyDictionary<uint, uint> _rows = new Dictionary<uint, uint>((rows ?? []).ToDictionary(r => r.SpellId, r => r.Charges));
    public uint? Find(uint spellId) => _rows.TryGetValue(spellId, out uint charges) ? charges : null;
}

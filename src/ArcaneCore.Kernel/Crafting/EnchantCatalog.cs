namespace ArcaneCore.Kernel.Crafting;

/// <summary>
/// One SpellItemEnchantment.dbc row (build 5875; vmangos Database/DBCStructure.h:639-651 <c>SpellItemEnchantmentEntry</c>, format
/// <c>"niiiiiixxxiiissssssssxii"</c>, DBCfmt.h:75): three effects, each a display type (<see cref="EnchantEffectType"/>), an amount and an argument
/// (a spell id, a resistance school or an <c>ITEM_MOD_*</c> stat id depending on the type), the English name, the visual id and the flags.
/// </summary>
/// <param name="Id">Field 0, <c>m_ID</c>.</param>
/// <param name="Types">Fields 1-3, <c>m_effect[3]</c>.</param>
/// <param name="Amounts">Fields 4-6, <c>m_effectPointsMin[3]</c> (read as signed so a negative stat is representable).</param>
/// <param name="Args">Fields 10-12, <c>m_effectArg[3]</c> (vmangos calls it <c>spellid</c>).</param>
/// <param name="Name">Field 13, the enUS <c>m_name_lang</c>.</param>
/// <param name="VisualId">Field 22, <c>m_itemVisual</c> (vmangos <c>aura_id</c>).</param>
/// <param name="Flags">Field 23, <c>m_flags</c> (vmangos <c>slot</c>): <see cref="EnchantCatalog.CanSoulboundFlag"/> and three unknown bits.</param>
public sealed record SpellItemEnchantment(
    uint Id, IReadOnlyList<uint> Types, IReadOnlyList<int> Amounts, IReadOnlyList<uint> Args, string Name, uint VisualId, uint Flags);

/// <summary>The effect display types of <see cref="SpellItemEnchantment.Types"/> (vmangos DBCEnums.h:164-170 <c>ItemEnchantmentType</c>).</summary>
public enum EnchantEffectType : uint
{
    None = 0,
    CombatSpell = 1,
    Damage = 2,
    EquipSpell = 3,
    Resistance = 4,
    Stat = 5,
    Totem = 6,
}

/// <summary>The enchantments of the client's SpellItemEnchantment.dbc by id. Immutable; <see cref="Empty"/> when no file is configured.</summary>
public sealed class EnchantCatalog
{
    /// <summary>vmangos <c>ENCHANTMENT_CAN_SOULBOUND</c> (Objects/ItemDefines.h:174): the enchantment may be applied to an item in a trade window.</summary>
    public const uint CanSoulboundFlag = 0x01;

    /// <summary>Effects per enchantment (vmangos <c>MAX_ENCHANTMENT_OFFSET</c>... the three arrays of the DBC row).</summary>
    public const int EffectCount = 3;

    private readonly Dictionary<uint, SpellItemEnchantment> _byId;

    public EnchantCatalog(IEnumerable<SpellItemEnchantment> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _byId = [];
        foreach (SpellItemEnchantment row in rows)
        {
            if (!_byId.TryAdd(row.Id, row))
            {
                throw new InvalidDataException($"duplicate SpellItemEnchantment id {row.Id}");
            }
        }
    }

    /// <summary>A catalog with no rows: every lookup misses.</summary>
    public static EnchantCatalog Empty { get; } = new([]);

    public int Count => _byId.Count;

    /// <summary>Every row, in no particular order.</summary>
    public IEnumerable<SpellItemEnchantment> All => _byId.Values;

    /// <summary>The row with <paramref name="id"/>, or null (vmangos <c>sSpellItemEnchantmentStore.LookupEntry</c>).</summary>
    public SpellItemEnchantment? Find(uint id) => _byId.GetValueOrDefault(id);
}

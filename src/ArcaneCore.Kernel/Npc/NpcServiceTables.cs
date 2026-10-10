using System.Collections.Frozen;

namespace ArcaneCore.Kernel.Npc;

/// <summary>
/// One TaxiPathNode.dbc row (build 5875: id, path, index, map, x, y, z, action flags, delay —
/// nine four-byte fields; vmangos DBCStructure.h TaxiPathNodeEntry, DBCfmt.h TaxiPathNodeEntryfmt).
/// </summary>
public sealed record TaxiPathNodeRecord(uint Id, uint PathId, uint Index, uint MapId, float X, float Y, float Z, uint Flags, uint Delay);

/// <summary>The waypoints of every flight path, ordered by node index (vmangos sTaxiPathNodesByPath).</summary>
public sealed class TaxiPathNodeCatalog
{
    private readonly FrozenDictionary<uint, TaxiPathNodeRecord[]> _byPath;

    public TaxiPathNodeCatalog(IEnumerable<TaxiPathNodeRecord> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        _byPath = nodes.GroupBy(n => n.PathId)
            .ToFrozenDictionary(g => g.Key, g => g.OrderBy(n => n.Index).ToArray());
    }

    public static TaxiPathNodeCatalog Empty { get; } = new([]);

    public int PathCount => _byPath.Count;

    /// <summary>The ordered waypoints of <paramref name="pathId"/> (empty when unknown).</summary>
    public IReadOnlyList<TaxiPathNodeRecord> Nodes(uint pathId) => _byPath.GetValueOrDefault(pathId) ?? [];
}

/// <summary>
/// One SkillLineAbility.dbc row (build 5875, fifteen fields, a fourteen-field image is read too; the two "not" masks are
/// unused, field 14 is the pet training-point cost reqtrainpoints: vmangos DBCStructure.h SkillLineAbilityEntry, fmt "niiiixxiiiiixxi").
/// </summary>
public sealed record SkillLineAbilityRecord(
    uint Id, uint SkillId, uint SpellId, uint RaceMask, uint ClassMask,
    uint ReqSkillValue, uint ForwardSpellId, uint LearnOnGetSkill, uint MaxValue, uint MinValue, uint ReqTrainPoints = 0);

/// <summary>
/// Spell ranks and race/class restrictions derived from SkillLineAbility.dbc (vmangos
/// SpellMgr::LoadSkillLineAbilityMap and the forward_spellid rank links of SpellMgr::LoadSpellChains).
/// </summary>
public sealed class SkillLineAbilityCatalog
{
    private readonly FrozenDictionary<uint, SkillLineAbilityRecord[]> _bySpell;
    private readonly FrozenDictionary<uint, uint> _previousRank;

    /// <param name="rows">The ability rows.</param>
    /// <param name="hasTrainingPoints">True when the rows were read from the fifteen-field image that carries reqtrainpoints.</param>
    public SkillLineAbilityCatalog(IEnumerable<SkillLineAbilityRecord> rows, bool hasTrainingPoints = false)
    {
        ArgumentNullException.ThrowIfNull(rows);
        SkillLineAbilityRecord[] all = rows.ToArray();
        _bySpell = all.GroupBy(r => r.SpellId).ToFrozenDictionary(g => g.Key, g => g.ToArray());
        var previous = new Dictionary<uint, uint>();
        foreach (SkillLineAbilityRecord row in all.Where(r => r.ForwardSpellId != 0 && r.ForwardSpellId != r.SpellId))
        {
            previous.TryAdd(row.ForwardSpellId, row.SpellId);
        }

        foreach ((uint rank, uint before) in PreviousRankSupplement)
        {
            previous.TryAdd(rank, before);
        }

        _previousRank = previous.ToFrozenDictionary();
        HasTrainingPoints = hasTrainingPoints;
    }

    /// <summary>
    /// Rank links the client DBC lacks: Boar Charge rank 6 (27685, 25 training points) has no forward_spellid link from rank 5 (26201), so
    /// the DBC chain 7371 -> 26177 -> 26178 -> 26179 -> 26201 stops one short. The reference servers keep the link in spell_chain
    /// (mangos-classic sql/archive/0.8/4096_pet.sql:116 <c>('27685','26201','7371','6')</c>; vmangos sql/old_migrations/20181119025556_world.sql:23 sets spell_chain build_min=5302 for 27685).
    /// A link the DBC does provide always wins. Charge is the only multi-chain name among the costed pet abilities of the build-5875 image.
    /// </summary>
    private static readonly (uint Rank, uint Previous)[] PreviousRankSupplement = [(27685, 26201)];

    public static SkillLineAbilityCatalog Empty { get; } = new([]);

    public int Count => _bySpell.Count;

    /// <summary>True when the rows carry reqtrainpoints (the fifteen-field image); a fourteen-field image reports 0 for every spell.</summary>
    public bool HasTrainingPoints { get; }

    public IReadOnlyList<SkillLineAbilityRecord> Abilities(uint spellId) => _bySpell.GetValueOrDefault(spellId) ?? [];

    /// <summary>The previous rank of <paramref name="spellId"/> (the ability whose forward_spellid it is), 0 when none.</summary>
    public uint PreviousRank(uint spellId) => _previousRank.GetValueOrDefault(spellId);

    /// <summary>
    /// The pet training-point cost of <paramref name="spellId"/>: the first ability row's reqtrainpoints, 0 when the spell has no row
    /// (vmangos Pet::GetTPForSpell breaks on the first row; the real build-5875 image has one value per spell across all its rows).
    /// </summary>
    public uint TrainingPoints(uint spellId) => _bySpell.TryGetValue(spellId, out SkillLineAbilityRecord[]? rows) ? rows[0].ReqTrainPoints : 0;

    /// <summary>The lowest rank of the chain <paramref name="spellId"/> belongs to (itself when it has no previous rank); the walk is bounded.</summary>
    public uint FirstInChain(uint spellId)
    {
        uint current = spellId;
        for (int hop = 0; hop < MaxChainLength; hop++)
        {
            uint previous = PreviousRank(current);
            if (previous == 0)
            {
                break;
            }

            current = previous;
        }

        return current;
    }

    private const int MaxChainLength = 32;

    /// <summary>
    /// vmangos Player::IsSpellFitByClassAndRace: a spell without abilities fits everyone; otherwise
    /// one ability must accept the race and class masks (0 = any).
    /// </summary>
    public bool FitsClassAndRace(uint spellId, uint raceMask, uint classMask)
    {
        IReadOnlyList<SkillLineAbilityRecord> abilities = Abilities(spellId);
        if (abilities.Count == 0)
        {
            return true;
        }

        foreach (SkillLineAbilityRecord ability in abilities)
        {
            if ((ability.RaceMask == 0 || (ability.RaceMask & raceMask) != 0)
                && (ability.ClassMask == 0 || (ability.ClassMask & classMask) != 0))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Repair prices: DurabilityCosts.dbc (item level → 29 multipliers: 21 weapon subclasses, then
/// 8 armor subclasses) and DurabilityQuality.dbc ((quality + 1) × 2 → factor). vmangos
/// Player::DurabilityRepair and ItemSubClassToDurabilityMultiplierId.
/// </summary>
public sealed class RepairCostTable
{
    /// <summary>Multipliers per DurabilityCosts row (DBC fields 1..29).</summary>
    public const int MultiplierCount = 29;

    private const uint ItemClassWeapon = 2;
    private const uint ItemClassArmor = 4;

    private readonly FrozenDictionary<uint, uint[]> _costs;
    private readonly FrozenDictionary<uint, float> _quality;

    public RepairCostTable(IEnumerable<(uint ItemLevel, uint[] Multipliers)> costs, IEnumerable<(uint Id, float Factor)> quality)
    {
        ArgumentNullException.ThrowIfNull(costs);
        ArgumentNullException.ThrowIfNull(quality);
        _costs = costs.ToFrozenDictionary(c => c.ItemLevel, c => c.Multipliers.Length == MultiplierCount
            ? c.Multipliers.ToArray()
            : throw new ArgumentException($"DurabilityCosts rows need {MultiplierCount} multipliers", nameof(costs)));
        _quality = quality.ToFrozenDictionary(q => q.Id, q => q.Factor);
    }

    public static RepairCostTable Empty { get; } = new([], []);

    public bool IsEmpty => _costs.Count == 0 || _quality.Count == 0;

    /// <summary>vmangos ItemSubClassToDurabilityMultiplierId: weapon subclass, armor subclass + 21, else 0.</summary>
    public static int MultiplierIndex(uint itemClass, uint subClass) => itemClass switch
    {
        ItemClassWeapon => (int)subClass,
        ItemClassArmor => (int)subClass + 21,
        _ => 0,
    };

    /// <summary>
    /// The undiscounted cost of restoring <paramref name="lostDurability"/> points:
    /// uint(lost × multiplier × quality factor). False when the item level or quality has no row
    /// (vmangos logs and repairs nothing).
    /// </summary>
    public bool TryGetCost(uint itemClass, uint subClass, uint itemLevel, uint quality, uint lostDurability, out uint cost)
    {
        cost = 0;
        int index = MultiplierIndex(itemClass, subClass);
        if (!_costs.TryGetValue(itemLevel, out uint[]? multipliers) || index < 0 || index >= multipliers.Length
            || !_quality.TryGetValue((quality + 1) * 2, out float factor))
        {
            return false;
        }

        double value = (double)lostDurability * multipliers[index] * factor;
        cost = value >= uint.MaxValue ? uint.MaxValue : (uint)value;
        return true;
    }
}

/// <summary>BankBagSlotPrices.dbc (slot number 1..6 → copper; vmangos HandleBuyBankSlotOpcode).</summary>
public sealed class BankBagSlotPriceTable
{
    private readonly FrozenDictionary<uint, uint> _prices;

    public BankBagSlotPriceTable(IEnumerable<(uint Slot, uint Price)> prices)
    {
        ArgumentNullException.ThrowIfNull(prices);
        _prices = prices.ToFrozenDictionary(p => p.Slot, p => p.Price);
    }

    public static BankBagSlotPriceTable Empty { get; } = new([]);

    public uint? Price(uint slot) => _prices.TryGetValue(slot, out uint price) ? price : null;
}

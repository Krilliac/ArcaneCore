using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Crafting;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.World.Gm.Character.Boost;

/// <summary>
/// The random properties ("of the Bear", "of the Owl") a template of <c>.character boost</c>'s pool can be created with, as
/// <see cref="BoostSuffix"/> values for <see cref="BoostGearPlanner.Plan"/>: the template's <c>random_property</c> names an
/// <c>item_enchantment_template</c> group, each row of it an ItemRandomProperties.dbc id, and each id's three enchantments are
/// SpellItemEnchantment.dbc rows (vmangos Item::SetItemRandomProperties). In build 5875 those rows are ITEM_ENCHANTMENT_TYPE_EQUIP_SPELL
/// (3) whose argument is a spell ("of the Bear" 1179 = enchantments 71 "+1 Stamina" and 68 "+1 Strength", spells 7477 and 7464): the stat
/// is that spell's SPELL_AURA_MOD_STAT (misc value 0-4 one stat, -1/-2 all five) and the armor its SPELL_AURA_MOD_RESISTANCE on the
/// physical school ("of Toughness"), applied while the item is worn (vmangos Player::ApplyEnchantment). An ITEM_ENCHANTMENT_TYPE_STAT
/// (5) row counts directly. Built per command, memoised per group; not thread-safe (world thread).
/// </summary>
internal sealed class BoostSuffixes
{
    private const uint EnchantEquipSpell = 3;   // vmangos DBCEnums.h ITEM_ENCHANTMENT_TYPE_EQUIP_SPELL
    private const uint EnchantStat = 5;         // ITEM_ENCHANTMENT_TYPE_STAT
    private const int SchoolMaskNormal = 1;     // SPELL_SCHOOL_MASK_NORMAL: armor

    /// <summary>UNIT_MOD_STAT order (strength, agility, stamina, intellect, spirit) to item_template stat_type.</summary>
    private static readonly ItemStatType[] UnitStats =
        [ItemStatType.Strength, ItemStatType.Agility, ItemStatType.Stamina, ItemStatType.Intellect, ItemStatType.Spirit];

    private readonly ItemRandomPropertyCatalog _properties;
    private readonly Func<uint, SpellItemEnchantment?> _enchantments;
    private readonly Func<uint, SpellInfo?> _spells;
    private readonly Dictionary<uint, IReadOnlyList<BoostSuffix>> _byGroup = [];

    public BoostSuffixes(ItemRandomPropertyCatalog properties, Func<uint, SpellItemEnchantment?> enchantments, Func<uint, SpellInfo?> spells)
    {
        _properties = properties ?? throw new ArgumentNullException(nameof(properties));
        _enchantments = enchantments ?? throw new ArgumentNullException(nameof(enchantments));
        _spells = spells ?? throw new ArgumentNullException(nameof(spells));
    }

    /// <summary>
    /// The suffix source, or null when part of the data is missing (no ItemRandomProperties.dbc and item_enchantment_template, or no
    /// SpellItemEnchantment.dbc): then a random-property template's stats are unknown and the planner leaves it out.
    /// </summary>
    public static BoostSuffixes? TryCreate(ItemRandomPropertyCatalog? properties, EnchantCatalog? enchantments, Func<uint, SpellInfo?> spells)
        => properties is { PropertyCount: > 0, GroupCount: > 0 } && enchantments is { Count: > 0 }
            ? new BoostSuffixes(properties, enchantments.Find, spells)
            : null;

    /// <summary>The properties an item of <paramref name="template"/> can roll (empty for a template without one, or a group with no known row).</summary>
    public IReadOnlyList<BoostSuffix> For(ItemTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (template.RandomProperty == 0)
        {
            return [];
        }

        if (_byGroup.TryGetValue(template.RandomProperty, out IReadOnlyList<BoostSuffix>? known))
        {
            return known;
        }

        var list = new List<BoostSuffix>();
        var seen = new HashSet<uint>();
        foreach (ItemEnchantmentChance row in _properties.Group(template.RandomProperty))
        {
            if (!seen.Add(row.EnchantId) || _properties.FindProperty(row.EnchantId) is not { } property)
            {
                continue;
            }

            var stats = new Dictionary<uint, int>();
            int armor = 0;
            foreach (uint enchantId in property.EnchantIds)
            {
                if (enchantId == 0 || _enchantments(enchantId) is not { } enchantment)
                {
                    continue;
                }

                int effects = Math.Min(enchantment.Types.Count, Math.Min(enchantment.Amounts.Count, enchantment.Args.Count));
                for (int i = 0; i < effects; i++)
                {
                    if (enchantment.Types[i] == EnchantStat)
                    {
                        Add(stats, enchantment.Args[i], enchantment.Amounts[i]);
                    }
                    else if (enchantment.Types[i] == EnchantEquipSpell && _spells(enchantment.Args[i]) is { } spell)
                    {
                        armor += AddSpell(stats, spell);
                    }
                }
            }

            list.Add(new BoostSuffix((int)property.Id, property.Name,
                [.. stats.Where(p => p.Value != 0).OrderBy(p => p.Key).Select(p => new ItemStat(p.Key, p.Value))]) { Armor = armor });
        }

        _byGroup[template.RandomProperty] = list;
        return list;
    }

    private static void Add(Dictionary<uint, int> stats, uint type, int amount)
        => stats[type] = stats.GetValueOrDefault(type) + amount;

    /// <summary>The stats of one equip spell's auras into <paramref name="stats"/>; returns the armor it adds.</summary>
    private static int AddSpell(Dictionary<uint, int> stats, SpellInfo spell)
    {
        int armor = 0;
        foreach (SpellEffectInfo effect in spell.Effects)
        {
            if (effect.Effect != SpellEffectName.ApplyAura)
            {
                continue;
            }

            int amount = Amount(effect);
            if (effect.AuraType == AuraType.ModStat && effect.MiscValue is >= -2 and <= 4)
            {
                for (int stat = 0; stat < UnitStats.Length; stat++)
                {
                    if (effect.MiscValue < 0 || effect.MiscValue == stat)
                    {
                        Add(stats, (uint)UnitStats[stat], amount);
                    }
                }
            }
            else if (effect.AuraType == AuraType.ModResistance && (effect.MiscValue & SchoolMaskNormal) != 0)
            {
                armor += amount;
            }
        }

        return armor;
    }

    /// <summary>
    /// The effect's value without a roll or level scaling: vmangos SpellEntry::CalculateSimpleValue (SpellEntry.h:1232), base points plus
    /// base dice. The suffix spells have one-sided dice and no per-level points (spell 7477 "+1 Stamina": 0 + 1).
    /// </summary>
    private static int Amount(SpellEffectInfo effect) => effect.BasePoints + effect.BaseDice;
}

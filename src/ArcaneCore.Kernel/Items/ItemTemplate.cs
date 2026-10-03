namespace ArcaneCore.Kernel.Items;

/// <summary>One stat bonus of an item (vmangos ItemPrototype.h <c>_ItemStat</c>).</summary>
public readonly record struct ItemStat(uint Type, int Value);

/// <summary>One damage range of a weapon (vmangos ItemPrototype.h <c>_ItemDamage</c>).</summary>
public readonly record struct ItemDamage(float Min, float Max, uint School);

/// <summary>One spell of an item (vmangos ItemPrototype.h <c>_ItemSpell</c>).</summary>
public readonly record struct ItemSpell(
    uint SpellId, uint Trigger, int Charges, float PpmRate, int Cooldown, uint Category, int CategoryCooldown);

/// <summary>
/// Static item data: one <c>item_template</c> row. Fields, types and order follow vmangos
/// ItemPrototype (ItemPrototype.h) as loaded by ObjectMgr::LoadItemPrototypes. Immutable; one
/// instance per entry is shared by every item of that entry.
/// </summary>
public sealed record ItemTemplate
{
    /// <summary>vmangos MAX_ITEM_PROTO_STATS.</summary>
    public const int MaxStats = 10;

    /// <summary>vmangos MAX_ITEM_PROTO_DAMAGES.</summary>
    public const int MaxDamages = 5;

    /// <summary>vmangos MAX_ITEM_PROTO_SPELLS.</summary>
    public const int MaxSpells = 5;

    public uint Entry { get; init; }
    public uint Class { get; init; }
    public uint SubClass { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public uint DisplayId { get; init; }
    public uint Quality { get; init; }
    public uint Flags { get; init; }
    public uint BuyCount { get; init; } = 1;
    public uint BuyPrice { get; init; }
    public uint SellPrice { get; init; }
    public uint InventoryType { get; init; }

    /// <summary>Class bit mask (bit = 1 &lt;&lt; (class - 1)); all bits set means every class.</summary>
    public uint AllowableClass { get; init; } = uint.MaxValue;

    /// <summary>Race bit mask (bit = 1 &lt;&lt; (race - 1)); all bits set means every race.</summary>
    public uint AllowableRace { get; init; } = uint.MaxValue;

    public uint ItemLevel { get; init; }
    public uint RequiredLevel { get; init; }
    public uint RequiredSkill { get; init; }
    public uint RequiredSkillRank { get; init; }
    public uint RequiredSpell { get; init; }
    public uint RequiredHonorRank { get; init; }
    public uint RequiredCityRank { get; init; }
    public uint RequiredReputationFaction { get; init; }
    public uint RequiredReputationRank { get; init; }

    /// <summary>Most a character may own (0 = no limit).</summary>
    public uint MaxCount { get; init; }

    /// <summary>Maximum stack size (1..255 after the load-time fixes).</summary>
    public uint Stackable { get; init; } = 1;

    public uint ContainerSlots { get; init; }
    public IReadOnlyList<ItemStat> Stats { get; init; } = [];
    public uint Delay { get; init; }
    public float RangedModRange { get; init; }
    public uint AmmoType { get; init; }
    public IReadOnlyList<ItemDamage> Damages { get; init; } = [];
    public uint Block { get; init; }
    public int Armor { get; init; }
    public int HolyRes { get; init; }
    public int FireRes { get; init; }
    public int NatureRes { get; init; }
    public int FrostRes { get; init; }
    public int ShadowRes { get; init; }
    public int ArcaneRes { get; init; }
    public IReadOnlyList<ItemSpell> Spells { get; init; } = [];
    public uint Bonding { get; init; }
    public uint PageText { get; init; }
    public uint PageLanguage { get; init; }
    public uint PageMaterial { get; init; }
    public uint StartQuest { get; init; }
    public uint LockId { get; init; }
    public uint Material { get; init; }
    public uint Sheath { get; init; }
    public uint RandomProperty { get; init; }
    public uint SetId { get; init; }
    public uint MaxDurability { get; init; }
    public uint AreaBound { get; init; }
    public uint MapBound { get; init; }
    public uint Duration { get; init; }
    public uint BagFamily { get; init; }
    public uint DisenchantId { get; init; }
    public uint FoodType { get; init; }
    public uint MinMoneyLoot { get; init; }
    public uint MaxMoneyLoot { get; init; }
    public uint WrappedGift { get; init; }
    public uint ExtraFlags { get; init; }
    public uint OtherTeamEntry { get; init; }

    /// <summary>
    /// The template with vmangos ObjectMgr::LoadItemPrototypes' load-time corrections applied:
    /// stackable 0 → 1 and &gt; 255 → 255, container slots capped at MAX_BAG_SIZE (36),
    /// buy count 0 → 1. Arrays are padded to their fixed protocol lengths.
    /// </summary>
    public ItemTemplate Normalized() => this with
    {
        Stackable = Stackable == 0 ? 1 : Math.Min(Stackable, 255u),
        ContainerSlots = Math.Min(ContainerSlots, 36u),
        BuyCount = BuyCount == 0 ? 1 : BuyCount,
        Stats = Pad(Stats, MaxStats),
        Damages = Pad(Damages, MaxDamages),
        Spells = Pad(Spells, MaxSpells),
    };

    private static T[] Pad<T>(IReadOnlyList<T> values, int length)
    {
        var result = new T[length];
        for (int i = 0; i < length && i < values.Count; i++)
        {
            result[i] = values[i];
        }

        return result;
    }
}

/// <summary>One starting item of a race/class (vmangos <c>playercreateinfo_item</c>: race, class, itemid, amount).</summary>
public readonly record struct StartingItem(byte Race, byte Class, uint ItemId, uint Amount);

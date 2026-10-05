using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Game.Items;

/// <summary>
/// Character abilities the equip rules ask about (vmangos Player::CanUseItem, CanDualWield,
/// GetSkillValue, HasSpell, honor and reputation ranks). Skills, spells, honor and reputation
/// belong to other areas; they replace <see cref="PlayerInventory.Requirements"/> when they land.
/// </summary>
public interface IItemRequirements
{
    /// <summary>vmangos Player::CanDualWield (the Dual Wield spell, 674, is known).</summary>
    bool CanDualWield(PlayerInventory inventory);

    /// <summary>vmangos Player::GetSkillValue (0 = skill unknown).</summary>
    uint SkillValue(PlayerInventory inventory, uint skill);

    bool HasSpell(PlayerInventory inventory, uint spellId);

    /// <summary>Highest PvP rank reached (vmangos HonorMgr::GetHighestRank().rank).</summary>
    byte HonorRank(PlayerInventory inventory);

    /// <summary>vmangos Player::GetReputationRank (ReputationRank: 3 = neutral).</summary>
    uint ReputationRank(PlayerInventory inventory, uint faction);
}

/// <summary>
/// Until skills/spells exist: every skill is known at max rank and every spell is known, so only
/// class/race masks, required level and honor rank gate equipment. Dual wield is refused (the
/// spell is learned in game; vmangos offers the off hand to one-hand weapons only after it).
/// Nobody has a PvP rank yet; reputation reads as neutral.
/// </summary>
public sealed class DefaultItemRequirements : IItemRequirements
{
    public static DefaultItemRequirements Instance { get; } = new();

    public bool CanDualWield(PlayerInventory inventory) => false;

    public uint SkillValue(PlayerInventory inventory, uint skill) => 300;

    public bool HasSpell(PlayerInventory inventory, uint spellId) => true;

    public byte HonorRank(PlayerInventory inventory) => 0;

    public uint ReputationRank(PlayerInventory inventory, uint faction) => 3;
}

/// <summary>
/// The stat application hook: called when an item starts or stops counting as worn
/// (vmangos Player::_ApplyItemMods from EquipItem / RemoveItem / DestroyItem / login).
/// Calls are always paired: every apply is followed by exactly one remove for the same item.
/// </summary>
public interface IItemStatsApplier
{
    void Apply(Player player, Item item, byte slot, bool apply);
}

/// <summary>
/// Default stat application, as deltas so other modifiers compose: item stats move
/// UNIT_FIELD_STATn and PLAYER_FIELD_POSSTATn / NEGSTATn, armor and resistances move
/// UNIT_FIELD_RESISTANCES (vmangos Player::_ApplyItemBonuses → HandleStatModifier with
/// TOTAL_VALUE for stats and BASE_VALUE for armor/resistances). Health and mana stat bonuses
/// move UNIT_FIELD_MAXHEALTH / MAXPOWER1. Derived values (attack power, health from stamina,
/// weapon damage) are the combat area's: it reads <see cref="PlayerInventory.Equipped"/>.
/// Broken items (durability 0 of a non-zero maximum) count as not worn, as in vmangos.
/// </summary>
public sealed class EquipmentStatsApplier : IItemStatsApplier
{
    public static EquipmentStatsApplier Instance { get; } = new();

    public void Apply(Player player, Item item, byte slot, bool apply)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(item);
        ItemTemplate t = item.Template;
        int sign = apply ? 1 : -1;

        foreach (ItemStat stat in t.Stats)
        {
            if (stat.Value == 0)
            {
                continue;
            }

            int value = stat.Value * sign;
            switch ((ItemStatType)stat.Type)
            {
                case ItemStatType.Health:
                    player.MaxHealth = Add(player.MaxHealth, value);
                    break;
                case ItemStatType.Mana:
                    player.SetUInt32(UpdateFields.UnitFieldMaxpower1, Add(player.GetUInt32(UpdateFields.UnitFieldMaxpower1), value));
                    break;
                default:
                    if (StatIndex((ItemStatType)stat.Type) is { } index)
                    {
                        StatAuras.ApplyExternalStatDelta(player, index, value, updateBuffFields: true, buffPositive: stat.Value > 0);
                    }

                    break;
            }
        }

        int[] resistances = [t.Armor, t.HolyRes, t.FireRes, t.NatureRes, t.FrostRes, t.ShadowRes, t.ArcaneRes];
        for (int school = 0; school < resistances.Length; school++)
        {
            if (resistances[school] != 0)
            {
                int field = UpdateFields.UnitFieldResistances + school;
                player.SetUInt32(field, Add(player.GetUInt32(field), resistances[school] * sign));
            }
        }
    }

    /// <summary>ItemModType → Stats (vmangos SharedDefines.h: STAT_STRENGTH 0, AGILITY 1, STAMINA 2, INTELLECT 3, SPIRIT 4).</summary>
    private static int? StatIndex(ItemStatType type) => type switch
    {
        ItemStatType.Strength => 0,
        ItemStatType.Agility => 1,
        ItemStatType.Stamina => 2,
        ItemStatType.Intellect => 3,
        ItemStatType.Spirit => 4,
        _ => null,
    };

    private static uint Add(uint value, int delta) => unchecked((uint)((int)value + delta));
}

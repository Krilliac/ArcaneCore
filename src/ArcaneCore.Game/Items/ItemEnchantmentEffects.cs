using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Stats;
using ArcaneCore.Kernel.WorldData.Items;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Items;

public sealed class ItemEnchantmentEffects : IItemEnchantmentSink
{
    private readonly IItemEnchantmentCatalog? _catalog;
    private readonly IItemEnchantmentCatalogProvider? _provider;
    private readonly Dictionary<(ObjectGuid Item, int Slot), (ItemEnchantmentDefinition Definition, byte EquipmentSlot)> _applied = [];

    public ItemEnchantmentEffects(IItemEnchantmentCatalog catalog) => _catalog = catalog;
    public ItemEnchantmentEffects(IItemEnchantmentCatalogProvider provider) => _provider = provider;
    private IItemEnchantmentCatalog Catalog => _provider?.Current ?? _catalog ?? EmptyItemEnchantmentCatalog.Instance;

    public void ApplyEnchantment(Player player, Item item, int enchantmentSlot, bool apply)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(item);
        if ((uint)enchantmentSlot >= Item.EnchantmentValues / 3u) return;

        var key = (item.Guid, enchantmentSlot);
        if (!apply)
        {
            if (_applied.Remove(key, out var old))
            {
                ApplyDefinition(player, item, old.EquipmentSlot, old.Definition, false);
                item.Inventory?.RefreshVisibleEnchantment(item, enchantmentSlot, 0);
            }
            return;
        }

        if (_applied.ContainsKey(key)) ApplyEnchantment(player, item, enchantmentSlot, false);
        uint enchantmentId = item.EnchantmentId(enchantmentSlot);
        if (enchantmentId == 0 || item.Inventory != player.Inventory || item.OwnerGuid != player.Guid
            || !IsEquippedAndUsable(item) || Catalog.Find(enchantmentId) is not { } definition) return;
        // Restrictions apply to each effect, not the entire three-effect definition.
        var appliedDefinition = definition.WithEffects(definition.Effects.Where(effect => effect.EffectType switch
        {
            2 => item.Slot is InventorySlots.MainHand or InventorySlots.OffHand or InventorySlots.Ranged,
            3 => player.Inventory.EnchantmentSpellSink is not null,
            6 => player.Class == Class.Shaman && item.Slot is InventorySlots.MainHand or InventorySlots.OffHand,
            _ => true,
        }));
        ApplyDefinition(player, item, item.Slot, appliedDefinition, true);
        item.Inventory.RefreshVisibleEnchantment(item, enchantmentSlot);
        _applied[key] = (appliedDefinition, item.Slot);
    }

    private static bool IsEquippedAndUsable(Item item)
        => item.Container is null && item.Slot < InventorySlots.EquipmentEnd
            && (item.MaxDurability == 0 || item.Durability > 0);

    private static void ApplyDefinition(Player player, Item item, byte equipmentSlot, ItemEnchantmentDefinition definition, bool apply)
    {
        foreach (ItemEnchantmentEffect effect in definition.Effects)
        {
            switch (effect.EffectType)
            {
                case 2: ApplyDamage(player, equipmentSlot, effect.Amount, apply, allowRanged: true); break;
                case 3: player.Inventory.EnchantmentSpellSink?.ApplyEnchantmentSpell(player, item, effect.SpellId, apply); break;
                case 4: ApplyResistance(player, effect, apply); break;
                case 5: ApplyStat(player, effect, apply); break;
                case 6:
                    ApplyDamage(player, equipmentSlot, effect.Amount * item.Template.Delay / 1000f, apply, allowRanged: false);
                    break;
            }
        }
    }

    private static void ApplyDamage(Player player, byte equipmentSlot, float amount, bool apply, bool allowRanged)
    {
        WeaponAttackType attack;
        if (equipmentSlot == InventorySlots.MainHand) attack = WeaponAttackType.BaseAttack;
        else if (equipmentSlot == InventorySlots.OffHand) attack = WeaponAttackType.OffAttack;
        else if (allowRanged && equipmentSlot == InventorySlots.Ranged) attack = WeaponAttackType.RangedAttack;
        else return;
        player.StatState.ApplyEnchantmentDamageBonus(attack, apply ? amount : -amount);
        player.StatState.Maintainer?.UpdateAll(player);
    }

    private static void ApplyResistance(Player player, ItemEnchantmentEffect effect, bool apply)
    {
        int school = (int)effect.SpellId;
        if ((uint)school >= 7) return;
        int delta = apply ? effect.Amount : -effect.Amount;
        player.SetInt32(UpdateFields.UnitFieldResistances + school, player.GetInt32(UpdateFields.UnitFieldResistances + school) + delta);
    }

    private static void ApplyStat(Player player, ItemEnchantmentEffect effect, bool apply)
    {
        int delta = apply ? effect.Amount : -effect.Amount;
        if (effect.SpellId == 0)
        {
            uint max = (uint)Math.Clamp((long)player.GetUInt32(UpdateFields.UnitFieldMaxpower1) + delta, 0, uint.MaxValue);
            player.SetUInt32(UpdateFields.UnitFieldMaxpower1, max);
            if (player.GetUInt32(UpdateFields.UnitFieldPower1) > max) player.SetUInt32(UpdateFields.UnitFieldPower1, max);
        }
        else if (effect.SpellId == 1)
        {
            player.MaxHealth = (uint)Math.Clamp((long)player.MaxHealth + delta, 1, uint.MaxValue);
            if (player.Health > player.MaxHealth) player.Health = player.MaxHealth;
        }
        else if (effect.SpellId is 3 or 4 or 5 or 6 or 7)
            StatAuras.ApplyExternalStatDelta(player, effect.SpellId switch
            {
                3 => 1, // agility
                4 => 0, // strength
                5 => 3, // intellect
                6 => 4, // spirit
                7 => 2, // stamina
                _ => 0,
            }, delta, true, effect.Amount > 0);
    }
}

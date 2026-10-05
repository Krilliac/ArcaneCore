using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Items;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    private bool _itemCombatProcActive;

    /// <summary>vmangos Player::CastItemCombatSpell: trigger-2 weapon spells after a qualifying hit.</summary>
    public void HandleItemCombatProc(MeleeDamageInfo info)
    {
        if (_itemCombatProcActive || info.Attacker is not Player player || ReferenceEquals(info.Attacker, info.Target) || !info.Target.IsAlive
            || info.AttackType == WeaponAttackType.RangedAttack
            || (info.HitInfo & HitInfo.AffectsVictim) == 0
            || info.TargetState is VictimState.Dodge or VictimState.Parry)
        {
            return;
        }

        HandleItemCombatProcForTarget(player, info.Target, info.AttackType);
    }

    /// <summary>Shared vmangos Player::CastItemCombatSpell item-proc loop for white and special attacks.</summary>
    private void HandleItemCombatProcForTarget(Player player, Unit target, WeaponAttackType attackType)
    {
        if (_itemCombatProcActive || ReferenceEquals(player, target) || !target.IsAlive || attackType == WeaponAttackType.RangedAttack)
            return;

        // Keep weapon-class, slot, disarm, usability, and durability checks in the shared
        // combat seam that mirrors Player::GetWeaponForAttack.
        Item? item = PlayerCombatSkills.WeaponForAttack(player, attackType, nonBroken: true, useable: true);
        if (item is null || !ReferenceEquals(item.Inventory, player.Inventory)) return;

        _itemCombatProcActive = true;
        try
        {
            for (byte index = 0; index < item.Template.Spells.Count; index++)
            {
                ItemSpell itemSpell = item.Template.Spells[index];
                if (itemSpell.SpellId == 0 || itemSpell.Trigger != 2 || Store.Get(itemSpell.SpellId) is not { } spell
                    || (player.Combat.HasPendingExtraAttacks && spell.HasEffect(SpellEffectName.AddExtraAttacks))
                    || IsGlobalCooldownActive(player, spell)) continue;
                float chance = itemSpell.PpmRate > 0 ? item.Template.Delay * itemSpell.PpmRate / 600f : spell.ProcChance;
                if (itemSpell.PpmRate == 0 && chance > 100f) chance = item.Template.Delay / 600f;
                if (chance <= 0 || Random.NextDouble() * 100d >= chance) continue;
                Prepare(player, spell, SpellCastTargets.ForUnit(target.Guid), triggered: true, castItem: item,
                    itemSpellIndex: index, itemCooldownMs: itemSpell.Cooldown,
                    itemCategoryCooldownMs: itemSpell.CategoryCooldown,
                    itemCategory: itemSpell.Category == 0 ? null : itemSpell.Category,
                    itemCombatProcCast: true);
            }

            // Slot zero is a permanent weapon enchant (and petition metadata on non-weapons).
            // Process all stored enchant slots so existing auxiliary combat-spell ids remain
            // usable; random-property expansion is intentionally separate.
            for (int slot = 0; slot < Item.EnchantmentValues / 3; slot++)
            {
                uint enchantmentId = item.EnchantmentId(slot);
                if (enchantmentId == 0 || ItemEnchantments.Find(enchantmentId) is not { } definition) continue;
                foreach (ItemEnchantmentEffect effect in definition.Effects)
                {
                    if (effect.EffectType != 1 || effect.SpellId == 0 || Store.Get(effect.SpellId) is not { } spell
                        ) continue;
                    float? overridePpm = ItemEnchantments.PpmRate(effect.SpellId);
                    float chance = overridePpm is > 0
                        ? item.Template.Delay * overridePpm.Value / 600f
                        : effect.Amount != 0 ? effect.Amount : item.Template.Delay / 600f;
                    chance = SpellModifiers.Apply(player, spell, SpellModOp.ChanceOfSuccess, chance);
                    if (chance <= 0 || Random.NextDouble() * 100d >= chance) continue;
                    SpellCastResult result = Prepare(player, spell,
                        SpellCastTargets.ForUnit(spell.IsPositive ? player.Guid : target.Guid),
                        triggered: true, castItem: item, itemCombatProcCast: true);
                    if (result == SpellCastResult.CastOk && slot != 0) item.ConsumeEnchantmentCharge(slot);
                }
            }
        }
        finally
        {
            _itemCombatProcActive = false;
        }
    }

    private bool IsGlobalCooldownActive(Player player, SpellInfo spell)
        => GetState(player.Guid) is { } state
            && state.GlobalCooldowns.TryGetValue(spell.StartRecoveryCategory, out uint until)
            && until > NowMs;
}

using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Game.Stats;

/// <summary>
/// The player's skill values the combat stats read (vmangos SpellCaster::GetWeaponSkillValue and
/// GetDefenseSkillValue, SpellCaster.cpp:116-159). The skills area replaces
/// <see cref="LevelMaximumSkills"/> when it lands.
/// </summary>
public interface IPlayerSkillSource
{
    /// <summary>The skill with the weapon in the hand's slot; <paramref name="hasWeapon"/> is false when it holds none.</summary>
    int WeaponSkill(Player player, WeaponAttackType attackType, bool hasWeapon);

    /// <summary>The defense skill.</summary>
    int DefenseSkill(Player player);
}

/// <summary>
/// Every skill at the maximum for the level (level × 5, vmangos SpellCaster::GetSkillMaxForLevel), and 0 for
/// the off-hand and ranged skills without a weapon (SpellCaster.cpp:122-124). This is what the combat hooks
/// assume until the skills area exists.
/// </summary>
public sealed class LevelMaximumSkills : IPlayerSkillSource
{
    public static LevelMaximumSkills Instance { get; } = new();

    public int WeaponSkill(Player player, WeaponAttackType attackType, bool hasWeapon)
        => attackType != WeaponAttackType.BaseAttack && !hasWeapon ? 0 : player.Level * 5;

    public int DefenseSkill(Player player) => player.Level * 5;
}

/// <summary>
/// The player's combat stat fields, written from the equipment and the learned abilities: attack power,
/// per-hand weapon damage and attack speed, crit / dodge / parry / block percentages and the agility part of
/// armor. A port of vmangos Player::UpdateAttackPowerAndDamage, CalculateMinMaxDamage, UpdateDamagePhysical,
/// UpdateCritPercentage, UpdateBlockPercentage, UpdateParryPercentage, UpdateDodgePercentage, UpdateArmor
/// (StatSystem.cpp) and the weapon part of Player::_ApplyItemMods / _ApplyItemBonuses (Player.cpp:6826-7008),
/// over the pure formulas in <see cref="StatFormulas"/>.
/// <para>
/// The stat fields themselves (UNIT_FIELD_STAT0..4, item armor and resistances, health and mana bonuses) are
/// still maintained by the level application (<see cref="Progression.PlayerProgression"/>) and the item hook
/// (<see cref="EquipmentStatsApplier"/>) as deltas; this system reads them as its input and derives from them.
/// Every update is a recompute from those inputs, so calling it twice changes nothing. Implements
/// <see cref="ICombatStatSource"/> so the hit table asks it about off-hand weapons, parry, block and the shield
/// block value. World thread only.
/// </para>
/// </summary>
public sealed class PlayerStatSystem : ICombatStatSource
{
    private const byte ItemClassWeapon = 2;

    private readonly AgilityRates? _rates;
    private readonly IPlayerSkillSource _skills;

    /// <param name="rates">Crit and dodge per agility; null when no rate data is available (the agility terms are then left out).</param>
    /// <param name="skills">Skill values; defaults to <see cref="LevelMaximumSkills"/>.</param>
    public PlayerStatSystem(AgilityRates? rates = null, IPlayerSkillSource? skills = null)
    {
        _rates = rates;
        _skills = skills ?? LevelMaximumSkills.Instance;
    }

    /// <summary>
    /// Start maintaining a player: route its item hook and dual wield rule through this system, rebuild the weapon
    /// state from the equipment already worn (items load before the player is attached) and write every value.
    /// </summary>
    public void Attach(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        PlayerStatState state = player.StatState;
        state.Maintainer = this;
        PlayerInventory inventory = player.Inventory;
        if (inventory.StatsApplier is not StatSystemItemApplier)
        {
            inventory.StatsApplier = new StatSystemItemApplier(this, inventory.StatsApplier);
        }

        if (inventory.Requirements is not StatStateItemRequirements)
        {
            inventory.Requirements = new StatStateItemRequirements(inventory.Requirements);
        }

        // vmangos Player::Create: the ranged attack time starts at BASE_ATTACK_TIME (Player.cpp:3306).
        if (player.GetUInt32(UpdateFields.UnitFieldRangedattacktime) == 0)
        {
            player.SetUInt32(UpdateFields.UnitFieldRangedattacktime, CombatConstants.BaseAttackTimeMs);
        }

        state.ResetWeaponDamage();
        state.ShieldBlockFlat = 0;
        foreach ((byte slot, Item item) in inventory.Equipped)
        {
            ApplyItemCore(player, item, slot, apply: true, resetTimer: false);
        }

        UpdateAll(player);
    }

    /// <summary>Player::UpdateAllStats' combat part: armor, attack power and damage, crit, block, parry and dodge.</summary>
    public void UpdateAll(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        UpdateArmor(player);
        UpdateAttackPowerAndDamage(player, ranged: false);
        UpdateAttackPowerAndDamage(player, ranged: true);
        UpdateAllCritPercentages(player);
        UpdateDefenseBonuses(player);
    }

    /// <summary>
    /// The weapon and block part of vmangos Player::_ApplyItemMods for one item that starts or stops counting as
    /// worn: the hand's damage entries, its attack time (restarting the swing when in combat), the shield block
    /// value, then the damage and, for the main hand, the parry percentage. The item hook calls it after the
    /// stat deltas were applied (<see cref="StatSystemItemApplier"/>).
    /// </summary>
    public void ApplyItem(Player player, Item item, byte slot, bool apply)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(item);
        ApplyItemCore(player, item, slot, apply, resetTimer: player.Combat.IsInCombat);
    }

    // --- items -----------------------------------------------------------------------

    private void ApplyItemCore(Player player, Item item, byte slot, bool apply, bool resetTimer)
    {
        // Player::_ApplyItemMods: nothing for slots past the equipment and for broken items (Player.cpp:6828-6833).
        if (slot >= InventorySlots.EquipmentEnd || IsBroken(item))
        {
            return;
        }

        PlayerStatState state = player.StatState;
        ItemTemplate template = item.Template;
        if (template.Block != 0)
        {
            state.ShieldBlockFlat += apply ? template.Block : -(float)template.Block;
        }

        if (template.Class == ItemClassWeapon)
        {
            ApplyWeapon(player, state, template, slot, apply, resetTimer);
        }

        // Some bonus parry talents are weapon specific in early patches (Player.cpp:6871-6873).
        if (slot == InventorySlots.MainHand)
        {
            UpdateParryPercentage(player);
        }
    }

    /// <summary>Player::_ApplyItemBonuses, weapon branch (Player.cpp:6945-7008).</summary>
    private void ApplyWeapon(Player player, PlayerStatState state, ItemTemplate template, byte slot, bool apply, bool resetTimer)
    {
        WeaponAttackType attackType = slot == InventorySlots.Ranged && IsRangedWeapon(template)
            ? WeaponAttackType.RangedAttack
            : slot == InventorySlots.OffHand ? WeaponAttackType.OffAttack : WeaponAttackType.BaseAttack;

        int count = 0;
        for (int i = 0; i < PlayerStatState.MaxDamages && i < template.Damages.Count; i++)
        {
            ItemDamage damage = template.Damages[i];
            if (damage.Max == 0)
            {
                break;
            }

            state.SetWeaponDamage(attackType, i, apply
                ? new WeaponDamageEntry(damage.Min, damage.Max, damage.School)
                : new WeaponDamageEntry(0, 0, 0));
            if (apply)
            {
                count++;
            }
        }

        state.SetWeaponDamageCount(attackType, count == 0 ? 1 : count);
        if (!CanUseEquippedWeapon(player, attackType) || template.Delay == 0)
        {
            return;
        }

        uint time = apply ? template.Delay : CombatConstants.BaseAttackTimeMs;
        player.SetUInt32(UpdateFields.UnitFieldBaseattacktime + (int)attackType, time);
        if (resetTimer)
        {
            player.Combat.ResetAttackTimer(attackType);
        }

        UpdateDamagePhysical(player, attackType);
    }

    // --- stats -----------------------------------------------------------------------

    /// <summary>
    /// Player::UpdateArmor (StatSystem.cpp:128-147): the item armor stays in the field as the item hook left it;
    /// the dynamic part (2 per agility) is added as the difference to what this system added before.
    /// </summary>
    private static void UpdateArmor(Player player)
    {
        PlayerStatState state = player.StatState;
        int desired = (int)StatFormulas.ArmorFromAgility(Stat(player, 1));
        int delta = desired - state.AppliedDynamicArmor;
        if (delta != 0)
        {
            int field = UpdateFields.UnitFieldResistances;
            player.SetUInt32(field, (uint)Math.Max(0L, (long)player.GetUInt32(field) + delta));
            state.AppliedDynamicArmor = desired;
        }
    }

    /// <summary>
    /// Player::UpdateAttackPowerAndDamage (StatSystem.cpp:309-352): the base attack power field from strength,
    /// agility and level, then the damage of the hands that can attack (the off hand only with Dual Wield and an
    /// off-hand weapon, StatSystem.cpp:349-350). The positive and negative modifier halves and the multiplier
    /// field are owned by the aura code and are read, not written.
    /// </summary>
    private void UpdateAttackPowerAndDamage(Player player, bool ranged)
    {
        float baseAttackPower = StatFormulas.AttackPowerFromStrengthAndAgility(ranged, player.Class, player.Level, Stat(player, 0), Stat(player, 1));
        player.SetInt32(ranged ? UpdateFields.UnitFieldRangedAttackPower : UpdateFields.UnitFieldAttackPower, (int)baseAttackPower);
        if (ranged)
        {
            UpdateDamagePhysical(player, WeaponAttackType.RangedAttack);
            return;
        }

        UpdateDamagePhysical(player, WeaponAttackType.BaseAttack);
        if (player.StatState.CanDualWield && GetWeaponForAttack(player, WeaponAttackType.OffAttack, nonBroken: true, useable: true) is not null)
        {
            UpdateDamagePhysical(player, WeaponAttackType.OffAttack);
        }
    }

    /// <summary>Player::UpdateDamagePhysical (StatSystem.cpp:457-480): the min/max damage fields of one hand.</summary>
    private void UpdateDamagePhysical(Player player, WeaponAttackType attackType)
    {
        PlayerStatState state = player.StatState;
        bool offHand = attackType == WeaponAttackType.OffAttack;
        WeaponDamageEntry weapon = offHand && GetWeaponForAttack(player, attackType, nonBroken: true, useable: true) is null
            ? default
            : state.WeaponDamage(attackType, 0);

        var inputs = new DamageInputs(
            attackType,
            Index: 0,
            AttSpeed: player.Combat.GetAttackTime(attackType) / 1000.0f,
            TotalAttackPower: TotalAttackPower(player, attackType),
            BaseValue: 0.0f,
            BasePct: 1.0f,
            TotalValue: 0.0f,
            TotalPct: UnitModConstants.Default(UnitModifierType.TotalPct, offHand ? UnitMods.DamageOffHand : UnitMods.DamageMainHand),
            TotalPhysical: 0.0f,
            WeaponMin: weapon.Min,
            WeaponMax: weapon.Max,
            Mode: CanUseEquippedWeapon(player, attackType) ? WeaponDamageMode.Weapon : WeaponDamageMode.CannotUseWeapon,
            Level: player.Level,
            AmmoDps: 0.0f);
        DamageRange range = StatFormulas.CalculateMinMaxDamage(inputs);

        (int min, int max) = attackType switch
        {
            WeaponAttackType.OffAttack => (UpdateFields.UnitFieldMinoffhanddamage, UpdateFields.UnitFieldMaxoffhanddamage),
            WeaponAttackType.RangedAttack => (UpdateFields.UnitFieldMinrangeddamage, UpdateFields.UnitFieldMaxrangeddamage),
            _ => (UpdateFields.UnitFieldMindamage, UpdateFields.UnitFieldMaxdamage),
        };
        SetStatFloat(player, min, range.Min);
        SetStatFloat(player, max, range.Max);
    }

    /// <summary>Unit::GetTotalAttackPowerValue (Unit.cpp:8037-8061) for a hand.</summary>
    private static float TotalAttackPower(Player player, WeaponAttackType attackType)
    {
        bool ranged = attackType == WeaponAttackType.RangedAttack;
        int power = ranged ? UpdateFields.UnitFieldRangedAttackPower : UpdateFields.UnitFieldAttackPower;
        int mods = ranged ? UpdateFields.UnitFieldRangedAttackPowerMods : UpdateFields.UnitFieldAttackPowerMods;
        int multiplier = ranged ? UpdateFields.UnitFieldRangedAttackPowerMultiplier : UpdateFields.UnitFieldAttackPowerMultiplier;
        return StatFormulas.TotalAttackPower(player.GetInt32(power), (short)player.GetUInt16(mods, 0), (short)player.GetUInt16(mods, 1), player.GetFloat(multiplier));
    }

    private void UpdateAllCritPercentages(Player player)
    {
        UpdateCritPercentage(player, WeaponAttackType.BaseAttack);
        UpdateCritPercentage(player, WeaponAttackType.RangedAttack);
    }

    /// <summary>Player::UpdateCritPercentage (StatSystem.cpp:531-577); the off hand has no client field.</summary>
    private void UpdateCritPercentage(Player player, WeaponAttackType attackType)
    {
        float critFromAgility = _rates?.MeleeCritFromAgility(player.Class, player.Level, Stat(player, 1)) ?? 0.0f;
        bool hasWeapon = GetWeaponForAttack(player, attackType, nonBroken: true, useable: true) is not null;
        int skill = _skills.WeaponSkill(player, attackType, hasWeapon);
        float value = StatFormulas.CritPercentage(player.Class, 0.0f, critFromAgility, skill, player.Level * 5);
        SetStatFloat(player, attackType == WeaponAttackType.RangedAttack ? UpdateFields.PlayerRangedCritPercentage : UpdateFields.PlayerCritPercentage, value);
    }

    /// <summary>Player::UpdateDefenseBonusesMod (StatSystem.cpp:507-512).</summary>
    private void UpdateDefenseBonuses(Player player)
    {
        UpdateBlockPercentage(player);
        UpdateParryPercentage(player);
        UpdateDodgePercentage(player);
    }

    internal void UpdateBlockPercentage(Player player)
    {
        float value = StatFormulas.BlockPercentage(player.StatState.CanBlock, _skills.DefenseSkill(player), player.Level * 5, 0.0f);
        SetStatFloat(player, UpdateFields.PlayerBlockPercentage, value);
    }

    internal void UpdateParryPercentage(Player player)
    {
        float value = StatFormulas.ParryPercentage(player.StatState.CanParry, _skills.DefenseSkill(player), player.Level * 5, 0.0f);
        SetStatFloat(player, UpdateFields.PlayerParryPercentage, value);
    }

    private void UpdateDodgePercentage(Player player)
    {
        float fromAgility = _rates?.DodgeFromAgility(player.Class, player.Level, Stat(player, 1)) ?? 0.0f;
        float value = StatFormulas.DodgePercentage(player.Class, fromAgility, _skills.DefenseSkill(player), player.Level * 5, 0.0f);
        SetStatFloat(player, UpdateFields.PlayerDodgePercentage, value);
    }

    // --- ICombatStatSource -----------------------------------------------------------

    /// <inheritdoc/>
    public bool? HasOffhandWeapon(Unit unit)
        => unit is Player player ? GetWeaponForAttack(player, WeaponAttackType.OffAttack, nonBroken: true, useable: true) is not null : null;

    /// <inheritdoc/>
    public bool? PlayerCanParry(Player player)
        => player.StatState.CanParry && GetWeaponForParry(player) is not null;

    /// <inheritdoc/>
    public bool? PlayerCanBlock(Player player)
    {
        if (!player.StatState.CanBlock || !CanUseEquippedWeapon(player, WeaponAttackType.OffAttack))
        {
            return false;
        }

        // Unit::GetUnitBlockChance (Unit.cpp:2534-2539): an unbroken item with a block value in the off-hand slot.
        return player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.OffHand) is { } item && !IsBroken(item) && item.Template.Block != 0;
    }

    /// <inheritdoc/>
    public uint? ShieldBlockValue(Unit unit)
        => unit is Player player ? StatFormulas.ShieldBlockValue(player.StatState.ShieldBlockFlat, Stat(player, 0), 1.0f) : null;

    // --- vmangos helpers -------------------------------------------------------------

    /// <summary>
    /// Player::GetWeaponForAttack (Player.cpp:8487-8516): the weapon in the hand's slot, optionally only when
    /// unbroken and usable.
    /// </summary>
    internal static Item? GetWeaponForAttack(Player player, WeaponAttackType attackType, bool nonBroken, bool useable)
    {
        byte slot = attackType switch
        {
            WeaponAttackType.OffAttack => InventorySlots.OffHand,
            WeaponAttackType.RangedAttack => InventorySlots.Ranged,
            _ => InventorySlots.MainHand,
        };

        if (player.Inventory.GetItem(InventorySlots.Bag0, slot) is not { } item || item.Template.Class != ItemClassWeapon)
        {
            return null;
        }

        if ((useable && !CanUseEquippedWeapon(player, attackType)) || (nonBroken && IsBroken(item)))
        {
            return null;
        }

        return item;
    }

    /// <summary>Player::GetWeaponForParry for build 5875: the main hand weapon, otherwise the off-hand weapon (Player.cpp:8518-8539).</summary>
    private static Item? GetWeaponForParry(Player player)
        => GetWeaponForAttack(player, WeaponAttackType.BaseAttack, nonBroken: true, useable: true)
            ?? GetWeaponForAttack(player, WeaponAttackType.OffAttack, nonBroken: true, useable: true);

    /// <summary>
    /// Unit::CanUseEquippedWeapon (Unit.h:964-978): true except for a disarmed main hand. The shapeshift part
    /// (IsAttackSpeedOverridenShapeShift) is the spell system's, which has no forms yet.
    /// </summary>
    private static bool CanUseEquippedWeapon(Player player, WeaponAttackType attackType)
        => attackType != WeaponAttackType.BaseAttack || (player.UnitFlags & UnitFlags.Disarmed) == 0;

    /// <summary>vmangos Item::IsBroken: a maximum durability and none left.</summary>
    private static bool IsBroken(Item item) => item.MaxDurability > 0 && item.Durability == 0;

    /// <summary>ItemPrototype::IsRangedWeapon (ItemPrototype.h:534).</summary>
    private static bool IsRangedWeapon(ItemTemplate template)
        => template.Class == ItemClassWeapon
            && (InventoryType)template.InventoryType is InventoryType.Ranged or InventoryType.Thrown or InventoryType.RangedRight;

    /// <summary>Unit::GetStat as a float.</summary>
    private static float Stat(Player player, int stat) => player.GetUInt32(UpdateFields.UnitFieldStat0 + stat);

    /// <summary>Object::SetStatFloatValue (Object.cpp:1266-1272): a negative value is stored as 0.</summary>
    private static void SetStatFloat(Player player, int field, float value) => player.SetFloat(field, value < 0 ? 0.0f : value);
}

/// <summary>
/// The item stat hook of a player attached to a <see cref="PlayerStatSystem"/>: the wrapped hook applies the stat
/// deltas, then the system updates the weapon state and recomputes the derived values.
/// </summary>
public sealed class StatSystemItemApplier(PlayerStatSystem system, IItemStatsApplier inner) : IItemStatsApplier
{
    public void Apply(Player player, Item item, byte slot, bool apply)
    {
        inner.Apply(player, item, slot, apply);
        system.ApplyItem(player, item, slot, apply);
        system.UpdateAll(player);
    }
}

/// <summary>
/// The item requirements of a player attached to a <see cref="PlayerStatSystem"/>: Dual Wield is whatever the
/// Dual Wield ability says (vmangos Player::CanDualWield is m_canDualWield, set by SPELL_EFFECT_DUAL_WIELD);
/// everything else is the wrapped provider's.
/// </summary>
public sealed class StatStateItemRequirements(IItemRequirements inner) : IItemRequirements
{
    public bool CanDualWield(PlayerInventory inventory) => inventory.Player?.StatState.CanDualWield ?? inner.CanDualWield(inventory);

    public uint SkillValue(PlayerInventory inventory, uint skill) => inner.SkillValue(inventory, skill);

    public bool HasSpell(PlayerInventory inventory, uint spellId) => inner.HasSpell(inventory, spellId);

    public byte HonorRank(PlayerInventory inventory) => inner.HonorRank(inventory);

    public uint ReputationRank(PlayerInventory inventory, uint faction) => inner.ReputationRank(inventory, faction);
}

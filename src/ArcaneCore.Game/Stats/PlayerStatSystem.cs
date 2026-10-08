using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Game.Stats;

/// <summary>
/// The player's skill values the combat stats read (vmangos SpellCaster::GetWeaponSkillValue and
/// GetDefenseSkillValue, SpellCaster.cpp:116-159). The daemon uses
/// <see cref="PlayerSkillStatSource"/> when skills are attached; standalone systems default
/// to <see cref="LevelMaximumSkills"/>.
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
/// assume when no skill content is configured.
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

        // ranged (autorepeat lane): the ammo DPS is part of the ranged damage (StatSystem.cpp:440-443); recompute when the ammo changes.
        inventory.AmmoChanged -= OnAmmoChanged;
        inventory.AmmoChanged += OnAmmoChanged;

        state.ResetWeaponDamage();
        state.ShieldBlockFlat = 0;
        state.ResetItemResistances();
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
        // Flat first, then percent (UnitStatModifier.cpp:117-125): the stat percentages first, because the health and mana bonuses and the
        // agility armor read the modified stat; then the maximum pools, then armor and resistances once the dynamic armor is in.
        PercentStatAuras.RefreshStats(player);
        StatBonuses.Update(player);
        PercentStatAuras.RefreshPools(player);
        UpdateArmor(player);
        PercentStatAuras.RefreshResistances(player);
        UpdateAttackPowerAndDamage(player, ranged: false);
        UpdateAttackPowerAndDamage(player, ranged: true);
        UpdateAllCritPercentages(player);
        UpdateDefenseBonuses(player);
        UpdateDamageDoneFields(player);
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
        // Player::_ApplyItemMods: nothing for slots past the equipment and for broken items (Player.cpp:6828-6833). The reference sees an unbroken
        // item on the remove of a breaking item because DurabilityPointsLoss calls _ApplyItemMods(false) before it writes the 0 ("modify item stats
        // _before_ Durability set to 0 to pass _ApplyItemMods internal check": mangos PlayerDurability.cpp:230-236, azerothcore Player.cpp:4898-4902);
        // PlayerInventory writes the durability first, so the broken check here guards the apply only. The hook pairs its calls (an item is removed
        // only when it was counted), so a remove always has a matching apply to undo: skipping it would leave the item's armor, resistances, block
        // value and weapon damage behind, and a repair would count them a second time.
        if (slot >= InventorySlots.EquipmentEnd || (apply && IsBroken(item)))
        {
            return;
        }

        PlayerStatState state = player.StatState;
        ItemTemplate template = item.Template;
        if (template.Block != 0)
        {
            state.ShieldBlockFlat += apply ? template.Block : -(float)template.Block;
        }

        state.AddItemResistances(template, apply ? 1 : -1);

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
        // ranged (autorepeat lane): SetAttackTime stores time * speed multiplier (vmangos Unit::SetAttackTime), so a weapon swap
        // under haste keeps the haste.
        player.Combat.SetAttackTime(attackType, time, resetTimer);

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
    public void UpdateAttackPowerAndDamage(Player player, bool ranged)
    {
        // The form (UNIT_FIELD_BYTES_1 byte 2) and Predatory Strikes only matter for the druid forms (StatSystem.cpp:194-296).
        var form = (ShapeshiftForm)FormQueries.GetForm(player);
        float baseAttackPower = StatFormulas.AttackPowerFromStrengthAndAgility(ranged, player.Class, player.Level, Stat(player, 0), Stat(player, 1),
            Enum.IsDefined(form) ? form : ShapeshiftForm.None, player.StatState.PredatoryStrikesPercent);
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

    /// <summary>
    /// The stat part of Player::InitDataForForm (Player.cpp:18271-18312), run after the player's form changed to
    /// <paramref name="newForm"/>: Cat sets both hands' attack time to 1.0 s, Bear and Dire Bear to 2.5 s, any other form
    /// restores the weapons' own delays (SetRegularAttackTime, Player.cpp:5158-5172); then attack power and damage are
    /// recomputed. vmangos' SetRegularAttackTime only rewrites a hand that holds a weapon, so an unarmed hand would keep
    /// the 1.0 or 2.5 s of the form after it ends; <paramref name="resetFistAttackTime"/> (option Forms:ResetFistAttackTimeOnFormLoss,
    /// default false = vmangos literal) is an opt-in deviation that gives such a hand the 2.0 s base time instead.
    /// </summary>
    public void OnFormChanged(Player player, byte newForm, bool resetFistAttackTime)
    {
        ArgumentNullException.ThrowIfNull(player);
        switch (newForm)
        {
            case (byte)ShapeshiftForm.Cat:
                SetFormAttackTime(player, 1000);
                break;
            case (byte)ShapeshiftForm.Bear:
            case (byte)ShapeshiftForm.DireBear:
                SetFormAttackTime(player, 2500);
                break;
            default:
                SetRegularAttackTime(player, resetFistAttackTime);
                break;
        }

        UpdateAttackPowerAndDamage(player, ranged: false);
        UpdateAttackPowerAndDamage(player, ranged: true);
    }

    /// <summary>
    /// The stat part of vmangos Aura::HandleAuraModDisarm (SpellAuras.cpp:3502-3545), run after UNIT_FLAG_DISARMED followed the player's disarm
    /// auras: outside a weaponless form a disarmed main hand swings at the 2.0 s base attack time and gets the weapon's own delay back when the
    /// last disarm goes (Player::SetRegularAttackTime), without restarting the swing; then everything is recomputed, so the main hand deals unarmed
    /// damage and its weapon-restricted crit and damage auras stop counting while it is disarmed (Player::_ApplyWeaponDependentAuraMods).
    /// </summary>
    public void OnDisarmChanged(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (!FormQueries.IsAttackSpeedOverridden(FormQueries.GetForm(player)))
        {
            uint time = CombatConstants.BaseAttackTimeMs;
            if ((player.UnitFlags & UnitFlags.Disarmed) == 0
                && GetWeaponForAttack(player, WeaponAttackType.BaseAttack, nonBroken: true, useable: false) is { Template.Delay: > 0 } weapon)
            {
                time = weapon.Template.Delay;
            }

            player.Combat.SetAttackTime(WeaponAttackType.BaseAttack, time, resetTimer: false);
        }

        UpdateAll(player);
    }

    private static void SetFormAttackTime(Player player, uint time)
    {
        player.SetUInt32(UpdateFields.UnitFieldBaseattacktime + (int)WeaponAttackType.BaseAttack, time);
        player.SetUInt32(UpdateFields.UnitFieldBaseattacktime + (int)WeaponAttackType.OffAttack, time);
    }

    /// <summary>Player::SetRegularAttackTime (Player.cpp:5158-5172) without a swing timer reset.</summary>
    private static void SetRegularAttackTime(Player player, bool resetFistAttackTime)
    {
        foreach (WeaponAttackType type in new[] { WeaponAttackType.BaseAttack, WeaponAttackType.OffAttack, WeaponAttackType.RangedAttack })
        {
            Item? item = GetWeaponForAttack(player, type, nonBroken: true, useable: false);
            if (item is not null)
            {
                uint delay = item.Template.Delay;
                player.SetUInt32(UpdateFields.UnitFieldBaseattacktime + (int)type, delay != 0 ? delay : CombatConstants.BaseAttackTimeMs);
            }
            else if (resetFistAttackTime && type != WeaponAttackType.RangedAttack)
            {
                player.SetUInt32(UpdateFields.UnitFieldBaseattacktime + (int)type, CombatConstants.BaseAttackTimeMs);
            }
        }
    }

    private void OnAmmoChanged(PlayerInventory inventory)
    {
        if (inventory.Player is { } player && ReferenceEquals(player.StatState.Maintainer, this))
        {
            UpdateDamagePhysical(player, WeaponAttackType.RangedAttack);
        }
    }

    /// <summary>Player::UpdateDamagePhysical (StatSystem.cpp:457-480): the min/max damage fields of one hand.</summary>
    internal void ApplyEnchantmentDamage(Player player, WeaponAttackType attackType, float delta)
    {
        player.StatState.ApplyEnchantmentDamageBonus(attackType, delta);
        UpdateDamagePhysical(player, attackType);
    }

    private void UpdateDamagePhysical(Player player, WeaponAttackType attackType)
    {
        PlayerStatState state = player.StatState;
        bool offHand = attackType == WeaponAttackType.OffAttack;
        Item? usable = GetWeaponForAttack(player, attackType, nonBroken: true, useable: true);
        WeaponDamageEntry weapon = offHand && usable is null
            ? default
            : state.WeaponDamage(attackType, 0);
        UnitMods group = attackType switch
        {
            WeaponAttackType.OffAttack => UnitMods.DamageOffHand,
            WeaponAttackType.RangedAttack => UnitMods.DamageRanged, // vmangos Player::CalculateMinMaxDamage (StatSystem.cpp:358-370): UNIT_MOD_DAMAGE_RANGED
            _ => UnitMods.DamageMainHand,
        };

        // The aura terms of the hand's modifier group (StatSystem.cpp:374-378): TOTAL_VALUE is the enchantments plus the weapon-restricted
        // MOD_DAMAGE_DONE, TOTAL_PCT the group's default (0.5 off hand) times MOD_DAMAGE_PERCENT_DONE / MOD_OFFHAND_DAMAGE_PCT (the ledger) times
        // the weapon-restricted MOD_DAMAGE_PERCENT_DONE, and UNIT_MOD_DAMAGE_PHYSICAL the generic physical MOD_DAMAGE_DONE. A weapon-restricted
        // aura counts while the hand's usable, unbroken weapon fits (Player::_ApplyWeaponDependentAuraDamageMod, Player.cpp:7072-7114).
        PlayerStatAuras auras = state.Auras;
        bool wandUser = player.Class is Class.Priest or Class.Mage or Class.Warlock;

        var inputs = new DamageInputs(
            attackType,
            Index: 0,
            AttSpeed: player.Combat.GetAttackTime(attackType) / 1000.0f,
            TotalAttackPower: TotalAttackPower(player, attackType),
            BaseValue: 0.0f,
            BasePct: 1.0f,
            TotalValue: state.TotalDamage(attackType) + auras.WeaponDamage(usable, percent: false, wandUser),
            TotalPct: state.Mods.TotalPct(group) * auras.WeaponDamage(usable, percent: true, wandUser),
            TotalPhysical: auras.PhysicalFlat(),
            WeaponMin: weapon.Min,
            WeaponMax: weapon.Max,
            Mode: FormQueries.IsAttackSpeedOverridden(FormQueries.GetForm(player)) ? WeaponDamageMode.ShapeshiftForm
                : CanUseEquippedWeapon(player, attackType) ? WeaponDamageMode.Weapon : WeaponDamageMode.CannotUseWeapon,
            Level: player.Level,
            AmmoDps: attackType == WeaponAttackType.RangedAttack ? player.Inventory.AmmoDps : 0.0f); // ranged (autorepeat lane): StatSystem.cpp:440-443
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

    /// <summary>
    /// Player::UpdateCritPercentage (StatSystem.cpp:531-577); the off hand has no client field. The FLAT_MOD of the group is the generic
    /// MOD_CRIT_PERCENT plus the weapon-restricted ones the hand's weapon earns; CRIT_PERCENTAGE also takes those an off-hand weapon's own
    /// enchantment cast (Player::_ApplyWeaponDependentAuraCritMod, Player.cpp:7029-7070).
    /// </summary>
    private void UpdateCritPercentage(Player player, WeaponAttackType attackType)
    {
        float critFromAgility = _rates?.MeleeCritFromAgility(player.Class, player.Level, Stat(player, 1)) ?? 0.0f;
        Item? weapon = GetWeaponForAttack(player, attackType, nonBroken: true, useable: true);
        int skill = _skills.WeaponSkill(player, attackType, weapon is not null);
        PlayerStatAuras auras = player.StatState.Auras;
        float flat = auras.GenericCrit() + auras.WeaponCrit(weapon, attackType);
        if (attackType == WeaponAttackType.BaseAttack)
        {
            flat += auras.WeaponCrit(GetWeaponForAttack(player, WeaponAttackType.OffAttack, nonBroken: true, useable: true), WeaponAttackType.OffAttack);
        }

        float value = StatFormulas.CritPercentage(player.Class, flat, critFromAgility, skill, player.Level * 5);
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
        float value = StatFormulas.BlockPercentage(player.StatState.CanBlock, _skills.DefenseSkill(player), player.Level * 5, player.StatState.BlockAuraBonus);
        SetStatFloat(player, UpdateFields.PlayerBlockPercentage, value);
    }

    /// <summary>
    /// Player::UpdateParryPercentage (StatSystem.cpp:590-605): the parry aura term is GetWeaponBasedAuraModifier(BASE_ATTACK, MOD_PARRY_PERCENT),
    /// the generic auras plus the weapon-restricted ones (Sword Finesse and the like) the main-hand item fits, whatever its state.
    /// </summary>
    internal void UpdateParryPercentage(Player player)
    {
        float aura = player.StatState.ParryAuraBonus
            + player.StatState.Auras.WeaponParry(GetWeaponForAttack(player, WeaponAttackType.BaseAttack, nonBroken: false, useable: false));
        float value = StatFormulas.ParryPercentage(player.StatState.CanParry, _skills.DefenseSkill(player), player.Level * 5, aura);
        SetStatFloat(player, UpdateFields.PlayerParryPercentage, value);
    }

    internal void UpdateDodgePercentage(Player player)
    {
        float fromAgility = _rates?.DodgeFromAgility(player.Class, player.Level, Stat(player, 1)) ?? 0.0f;
        float value = StatFormulas.DodgePercentage(player.Class, fromAgility, _skills.DefenseSkill(player), player.Level * 5, player.StatState.DodgeAuraBonus);
        SetStatFloat(player, UpdateFields.PlayerDodgePercentage, value);
    }

    /// <summary>
    /// The client's spell damage display (vmangos Player::UpdateSpellDamageAndHealingBonus, StatSystem.cpp:80-88, run by every stat update, and the
    /// field writes of Aura::HandleModDamageDone / Player::UpdateDamageDonePercent, SpellAuras.cpp:5262-5306, Player.cpp:7116-7136):
    /// PLAYER_FIELD_MOD_DAMAGE_DONE_POS / _NEG / _PCT per school from <see cref="PlayerStatState.Auras"/> and the current spirit. The engine's spell
    /// damage reads the auras at the cast (SpellBonusModule); these fields are what the character sheet shows, so they are refreshed after
    /// every stat change and every damage done aura change. Unlike the reference, which overwrites the positive field with the net bonus at a stat
    /// update while its aura handlers add positive and negative amounts apart, the two stay apart here.
    /// </summary>
    internal static void UpdateDamageDoneFields(Player player)
    {
        PlayerStatAuras auras = player.StatState.Auras;
        uint spirit = player.GetUInt32(UpdateFields.UnitFieldStat0 + 4);
        for (int school = 0; school < 7; school++)
        {
            (int positive, int negative) = auras.DamageDone(school, spirit);
            player.SetUInt32(UpdateFields.PlayerFieldModDamageDonePos + school, (uint)Math.Max(0, positive));
            player.SetInt32(UpdateFields.PlayerFieldModDamageDoneNeg + school, negative);
            player.SetFloat(UpdateFields.PlayerFieldModDamageDonePct + school, auras.DamageDonePercent(school));
        }
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
        => unit is Player player ? StatFormulas.ShieldBlockValue(player.StatState.ShieldBlockFlat + player.StatState.ShieldBlockAuraFlat, Stat(player, 0), player.StatState.ShieldBlockPct) : null;

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
    /// Unit::CanUseEquippedWeapon (Unit.h:963-978): false for every hand in Cat, Bear and Dire Bear Form
    /// (IsAttackSpeedOverridenShapeShift), otherwise true except for a disarmed main hand.
    /// </summary>
    private static bool CanUseEquippedWeapon(Player player, WeaponAttackType attackType)
        => !FormQueries.IsAttackSpeedOverridden(FormQueries.GetForm(player))
            && (attackType != WeaponAttackType.BaseAttack || (player.UnitFlags & UnitFlags.Disarmed) == 0);

    /// <summary>vmangos Item::IsBroken: a maximum durability and none left.</summary>
    internal static bool IsBroken(Item item) => item.MaxDurability > 0 && item.Durability == 0;

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
/// The item requirements of a player attached to a <see cref="PlayerStatSystem"/>: Dual Wield is true when the
/// Dual Wield ability was applied (vmangos Player::CanDualWield is m_canDualWield, set by SPELL_EFFECT_DUAL_WIELD)
/// or the wrapped provider says so (the skills area's flag); everything else is the wrapped provider's.
/// </summary>
public sealed class StatStateItemRequirements(IItemRequirements inner) : IItemRequirements
{
    public bool CanDualWield(PlayerInventory inventory) => (inventory.Player?.StatState.CanDualWield ?? false) || inner.CanDualWield(inventory);

    public uint SkillValue(PlayerInventory inventory, uint skill) => inner.SkillValue(inventory, skill);

    public bool HasSpell(PlayerInventory inventory, uint spellId) => inner.HasSpell(inventory, spellId);

    public byte HonorRank(PlayerInventory inventory) => inner.HonorRank(inventory);

    public uint ReputationRank(PlayerInventory inventory, uint faction) => inner.ReputationRank(inventory, faction);
}

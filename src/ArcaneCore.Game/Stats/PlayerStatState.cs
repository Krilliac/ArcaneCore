using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Game.Stats;

/// <summary>One weapon damage entry: vmangos <c>WeaponDamageInfo</c> (damage range and spell school).</summary>
public readonly record struct WeaponDamageEntry(float Min, float Max, uint School);

/// <summary>
/// The combat stat state of one player that has no update field of its own: the per-hand weapon damage
/// entries and their count (vmangos Unit::m_weaponDamage / m_weaponDamageCount, Unit.cpp:129-139), the
/// Parry / Block / Dual Wield abilities (Player::m_canParry, m_canBlock, m_canDualWield, Player.cpp:210-212),
/// the flat shield block value (SHIELD_BLOCK_VALUE FLAT_MOD) and the dynamic armor already added to the
/// armor field. Maintained by <see cref="PlayerStatSystem"/> on the world thread.
/// </summary>
public sealed class PlayerStatState
{
    /// <summary>vmangos MAX_ITEM_PROTO_DAMAGES.</summary>
    public const int MaxDamages = ItemTemplate.MaxDamages;

    private readonly WeaponDamageEntry[][] _damage = new WeaponDamageEntry[3][];
    private readonly int[] _count = new int[3];
    private readonly float[] _totalDamage = new float[3];

    internal PlayerStatState(Player owner)
    {
        Owner = owner;
        ResetWeaponDamage();
    }

    public Player Owner { get; }

    /// <summary>The stat system that maintains this player (set when it attaches); ability changes are reported to it.</summary>
    public PlayerStatSystem? Maintainer { get; internal set; }

    /// <summary>
    /// The amount of the Predatory Strikes dummy aura (spell icon 1563) the player has, 0 when absent: the percent of the level
    /// a cat or bear adds to its attack power (StatSystem.cpp:253-271). Kept by <see cref="FormStatListener"/> from the aura
    /// events; read by <see cref="PlayerStatSystem"/> only while the player is in a form that uses it.
    /// </summary>
    public int PredatoryStrikesPercent { get; set; }

    /// <summary>Player::CanParry: the Parry ability (spell effect PARRY) is known.</summary>
    public bool CanParry { get; private set; }

    /// <summary>Player::CanBlock: the Block ability (spell effect BLOCK) is known.</summary>
    public bool CanBlock { get; private set; }

    /// <summary>Player::CanDualWield: the Dual Wield ability (spell effect DUAL_WIELD) is known.</summary>
    public bool CanDualWield { get; private set; }

    /// <summary>The sum of the block values of the worn, unbroken items (SHIELD_BLOCK_VALUE FLAT_MOD).</summary>
    public float ShieldBlockFlat { get; internal set; }

    /// <summary>
    /// The percent slots of this player's modifier groups and what they add to the fields (<see cref="UnitModLedger"/>); written by
    /// the percent stat auras, read back by every <see cref="PlayerStatSystem"/> update. World thread only.
    /// </summary>
    public UnitModLedger Mods { get; } = new();

    /// <summary>
    /// The crit, weapon damage, weapon parry and damage done auras the formulas fold in at every recompute (<see cref="PlayerStatAuras"/>); kept by
    /// <see cref="Spells.CombatStatAuras"/> and the weapon-restricted parry auras of <see cref="Spells.PercentStatAuras"/>. World thread only.
    /// </summary>
    public PlayerStatAuras Auras { get; } = new();

    /// <summary>
    /// The sum of the amounts of the dodge percent auras (SPELL_AURA_MOD_DODGE_PERCENT; vmangos adds GetTotalAuraModifier of the type
    /// in Player::UpdateDodgePercentage, StatSystem.cpp:607-640). Kept by the stat auras, which recompute the percentage when it changes.
    /// </summary>
    public float DodgeAuraBonus { get; private set; }

    /// <summary>The sum of the parry percent auras (SPELL_AURA_MOD_PARRY_PERCENT, Player::UpdateParryPercentage).</summary>
    public float ParryAuraBonus { get; private set; }

    /// <summary>The sum of the block percent auras (SPELL_AURA_MOD_BLOCK_PERCENT, Player::UpdateBlockPercentage).</summary>
    public float BlockAuraBonus { get; private set; }

    /// <summary>The flat shield block value the auras add (SHIELD_BLOCK_VALUE FLAT_MOD, SPELL_AURA_MOD_SHIELD_BLOCKVALUE).</summary>
    public float ShieldBlockAuraFlat { get; private set; }

    private PercentFactor _shieldBlockPct;

    /// <summary>The shield block value multiplier of the auras (SHIELD_BLOCK_VALUE PCT_MOD, SPELL_AURA_MOD_SHIELD_BLOCKVALUE_PCT; 1 when none).</summary>
    public float ShieldBlockPct => _shieldBlockPct.Value;

    /// <summary>Add (or take back) a dodge percent aura amount and recompute the dodge percentage.</summary>
    public void AddDodgeBonus(float delta)
    {
        DodgeAuraBonus += delta;
        Maintainer?.UpdateDodgePercentage(Owner);
    }

    /// <summary>Add (or take back) a parry percent aura amount and recompute the parry percentage.</summary>
    public void AddParryBonus(float delta)
    {
        ParryAuraBonus += delta;
        Maintainer?.UpdateParryPercentage(Owner);
    }

    /// <summary>Add (or take back) a block percent aura amount and recompute the block percentage.</summary>
    public void AddBlockBonus(float delta)
    {
        BlockAuraBonus += delta;
        Maintainer?.UpdateBlockPercentage(Owner);
    }

    /// <summary>Player::HandleBaseModValue(SHIELD_BLOCK_VALUE, FLAT_MOD, ...): the shield block value is read on demand, so nothing is recomputed.</summary>
    public void AddShieldBlockFlat(float delta) => ShieldBlockAuraFlat += delta;

    /// <summary>Player::HandleBaseModValue(SHIELD_BLOCK_VALUE, PCT_MOD, ...): multiply in or divide out a percent.</summary>
    public void ApplyShieldBlockPct(float amount, bool apply) => _shieldBlockPct.Apply(amount, apply);

    private readonly int[] _itemResistance = new int[7];

    /// <summary>
    /// The armor (school 0) or resistance the worn items give to a school: BASE_VALUE of that group in the reference (Player::_ApplyItemBonuses),
    /// what MOD_BASE_RESISTANCE_PCT scales. Kept by <see cref="PlayerStatSystem"/> from the same item hook calls that move the fields, so it is right
    /// while an item is being put on or taken off (the inventory's own list is not).
    /// </summary>
    public int ItemResistance(int school) => _itemResistance[school];

    internal void ResetItemResistances() => Array.Clear(_itemResistance);

    internal void AddItemResistances(ItemTemplate template, int sign)
    {
        _itemResistance[0] += template.Armor * sign;
        _itemResistance[1] += template.HolyRes * sign;
        _itemResistance[2] += template.FireRes * sign;
        _itemResistance[3] += template.NatureRes * sign;
        _itemResistance[4] += template.FrostRes * sign;
        _itemResistance[5] += template.ShadowRes * sign;
        _itemResistance[6] += template.ArcaneRes * sign;
    }

    /// <summary>The health from stamina already included in the maximum health field (<see cref="StatBonuses"/>).</summary>
    internal uint HealthBonusIncluded { get; set; }

    /// <summary>The mana from intellect already included in the maximum mana field (<see cref="StatBonuses"/>).</summary>
    internal uint ManaBonusIncluded { get; set; }

    /// <summary>The agility based armor already added to the armor field (Player::UpdateArmor's dynamic part).</summary>
    internal int AppliedDynamicArmor { get; set; }

    internal float[] EnchantmentDamageBonus { get; } = new float[3];

    internal void ApplyEnchantmentDamageBonus(WeaponAttackType attackType, float delta)
        => EnchantmentDamageBonus[(int)attackType] += delta;

    /// <summary>Player::SetCanParry (Player.cpp:20421): a change recomputes the parry percentage.</summary>
    public void SetCanParry(bool value)
    {
        if (CanParry == value)
        {
            return;
        }

        CanParry = value;
        Maintainer?.UpdateParryPercentage(Owner);
    }

    /// <summary>Player::SetCanBlock (Player.cpp:20430): a change recomputes the block percentage.</summary>
    public void SetCanBlock(bool value)
    {
        if (CanBlock == value)
        {
            return;
        }

        CanBlock = value;
        Maintainer?.UpdateBlockPercentage(Owner);
    }

    /// <summary>
    /// Player::SetCanDualWield (Player.h:1503) is a plain flag in the reference. Here a change also recomputes the
    /// attack power and damage fields: the Dual Wield passive is cast after the items were attached at login, and
    /// the off hand damage written then had no attack power yet (StatSystem.cpp:349-350 writes it only with the ability).
    /// </summary>
    public void SetCanDualWield(bool value)
    {
        if (CanDualWield == value)
        {
            return;
        }

        CanDualWield = value;
        Maintainer?.UpdateAttackPowerAndDamage(Owner, ranged: false);
    }

    /// <summary>
    /// The flat damage added to a hand (vmangos <c>m_auraModifiersGroup[UNIT_MOD_DAMAGE_MAINHAND|OFFHAND|RANGED][TOTAL_VALUE]</c>, the term a weapon enchantment
    /// with a damage or totem effect adds through Player::ApplyEnchantment, Player.cpp:11771-11780 and :11846-11863). Read by <see cref="PlayerStatSystem"/>.
    /// </summary>
    public float TotalDamage(WeaponAttackType type) => _totalDamage[(int)type];

    /// <summary>Add <paramref name="delta"/> to a hand's flat damage and recompute the damage fields of that hand.</summary>
    public void AddTotalDamage(WeaponAttackType type, float delta)
    {
        if (delta == 0)
        {
            return;
        }

        _totalDamage[(int)type] += delta;
        Maintainer?.UpdateAttackPowerAndDamage(Owner, ranged: type == WeaponAttackType.RangedAttack);
    }

    /// <summary>Unit::GetWeaponDamageCount.</summary>
    public int WeaponDamageCount(WeaponAttackType type) => _count[(int)type];

    /// <summary>The raw entry (Unit::m_weaponDamage[type][index]).</summary>
    public WeaponDamageEntry WeaponDamage(WeaponAttackType type, int index) => _damage[(int)type][index];

    internal void SetWeaponDamage(WeaponAttackType type, int index, WeaponDamageEntry entry) => _damage[(int)type][index] = entry;

    internal void SetWeaponDamageCount(WeaponAttackType type, int count) => _count[(int)type] = count;

    /// <summary>The state a unit starts with: entry 0 is the fist range, the others empty, count 1 (Unit.cpp:129-139).</summary>
    internal void ResetWeaponDamage()
    {
        for (int hand = 0; hand < _damage.Length; hand++)
        {
            _damage[hand] = new WeaponDamageEntry[MaxDamages];
            _damage[hand][0] = new WeaponDamageEntry(UnitModConstants.BaseMinDamage, UnitModConstants.BaseMaxDamage, 0);
            _count[hand] = 1;
        }
    }
}

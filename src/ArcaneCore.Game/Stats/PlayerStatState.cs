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

    /// <summary>The health from stamina already included in the maximum health field (<see cref="StatBonuses"/>).</summary>
    internal uint HealthBonusIncluded { get; set; }

    /// <summary>The mana from intellect already included in the maximum mana field (<see cref="StatBonuses"/>).</summary>
    internal uint ManaBonusIncluded { get; set; }

    /// <summary>The agility based armor already added to the armor field (Player::UpdateArmor's dynamic part).</summary>
    internal int AppliedDynamicArmor { get; set; }

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

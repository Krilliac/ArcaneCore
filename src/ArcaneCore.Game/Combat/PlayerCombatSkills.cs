using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Skills;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.Skills;

namespace ArcaneCore.Game.Combat;

/// <summary>
/// What a player's skills, equipment and learned abilities contribute to melee: the weapon and defense skill the
/// hit table rolls with, the off hand, parry and block predicates, and the skill-ups a resolved swing earns.
/// <see cref="CombatHooks"/> consults it for a <see cref="Player"/> that has <see cref="Player.Skills"/> attached
/// (a player without skills keeps the level-based defaults). vmangos SpellCaster::GetWeaponSkillValue /
/// GetDefenseSkillValue (SpellCaster.cpp:116-152), Unit::HaveOffhandWeapon (Unit.cpp:499-509),
/// Player::GetWeaponForAttack / GetWeaponForParry (Player.cpp:8487-8535), Unit::GetUnitBlockChance
/// (Unit.cpp:2515-2545) and Unit::ProcSkillsAndReactives (Unit.cpp:8834-8846).
/// </summary>
/// <remarks>
/// Weapon-skill gain is suppressed while shapeshifted in the DBC sense (<see cref="FormQueries.IsShapeShifted(Unit, ArcaneCore.Kernel.WorldData.ShapeshiftFormCatalog?)"/>:
/// a form whose SpellShapeshiftForm row lacks the Stance flag; vmangos Player.cpp:5351), so warrior stances,
/// Stealth and Moonkin still gain skill. Not modelled: the weapon skill of a form without weapons (the level
/// maximum in vmangos), and pets (a pet's owner counts as a player-controlled victim in vmangos).
/// </remarks>
public static class PlayerCombatSkills
{
    /// <summary>vmangos ITEM_SUBCLASS_WEAPON_FISHING_POLE (ItemPrototype.h): a fishing pole never raises a weapon skill.</summary>
    private const uint FishingPoleSubClass = 20;

    /// <summary>STAT_INTELLECT (vmangos SharedDefines.h Stats): the index of UNIT_FIELD_STAT0 + n.</summary>
    private const int IntellectStat = 3;

    /// <summary>vmangos Unit::CanUseEquippedWeapon (Unit.h:964-977): a disarmed unit cannot use its main hand.</summary>
    public static bool CanUseEquippedWeapon(Unit unit, WeaponAttackType attackType)
        => attackType != WeaponAttackType.BaseAttack || (unit.UnitFlags & UnitFlags.Disarmed) == 0;

    /// <summary>
    /// vmangos Player::GetWeaponForAttack (Player.cpp:8487-8516): the weapon (item class weapon) in the slot of the
    /// attack type; <paramref name="useable"/> also needs <see cref="CanUseEquippedWeapon"/> and
    /// <paramref name="nonBroken"/> an item that is not broken (durability 0 of a non-zero maximum).
    /// </summary>
    public static Item? WeaponForAttack(Player player, WeaponAttackType attackType, bool nonBroken, bool useable)
    {
        byte slot = attackType switch
        {
            WeaponAttackType.BaseAttack => InventorySlots.MainHand,
            WeaponAttackType.OffAttack => InventorySlots.OffHand,
            WeaponAttackType.RangedAttack => InventorySlots.Ranged,
            _ => InventorySlots.NullSlot,
        };
        if (slot == InventorySlots.NullSlot || player.Inventory.GetItem(InventorySlots.Bag0, slot) is not { } item
            || (ItemClass)item.Template.Class != ItemClass.Weapon)
        {
            return null;
        }

        if (useable && !CanUseEquippedWeapon(player, attackType))
        {
            return null;
        }

        return nonBroken && IsBroken(item) ? null : item;
    }

    /// <summary>vmangos Player::GetWeaponForParry (Player.cpp:8518-8535, the 1.12 branch): the main-hand weapon, else the off-hand one.</summary>
    public static Item? WeaponForParry(Player player)
        => WeaponForAttack(player, WeaponAttackType.BaseAttack, true, true) ?? WeaponForAttack(player, WeaponAttackType.OffAttack, true, true);

    /// <summary>
    /// The skill value a swing with this hand rolls against (vmangos GetWeaponSkillValue): 0 for an empty off hand or
    /// ranged slot, Unarmed for an empty main hand, otherwise the proficiency skill of the weapon, with bonuses.
    /// </summary>
    public static int WeaponSkill(Player player, PlayerSkills skills, WeaponAttackType attackType)
    {
        Item? item = WeaponForAttack(player, attackType, true, true);
        if (attackType != WeaponAttackType.BaseAttack && item is null)
        {
            return 0;
        }

        return skills.GetValue(item is null ? SkillIds.Unarmed : item.Template.ProficiencySkill());
    }

    /// <summary>
    /// vmangos GetDefenseSkillValue (SpellCaster.cpp:143-152): against another player the full maximum counts, otherwise
    /// the current value, both with bonuses.
    /// </summary>
    public static int DefenseSkill(PlayerSkills skills, Unit? attacker)
        => attacker is Player ? skills.GetMax(SkillIds.Defense) : skills.GetValue(SkillIds.Defense);

    /// <summary>vmangos Unit::HaveOffhandWeapon for a player: a usable, unbroken weapon in the off hand.</summary>
    public static bool HasOffhandWeapon(Player player)
        => CanUseEquippedWeapon(player, WeaponAttackType.OffAttack) && WeaponForAttack(player, WeaponAttackType.OffAttack, true, true) is not null;

    /// <summary>vmangos Unit::GetUnitParryChance (Unit.cpp:2498): the Parry ability and a weapon to parry with.</summary>
    public static bool CanParry(Player player, PlayerSkills skills) => skills.CanParry && WeaponForParry(player) is not null;

    /// <summary>vmangos Unit::GetUnitBlockChance (Unit.cpp:2534-2540): the Block ability and an unbroken off-hand item with a block value.</summary>
    public static bool CanBlock(Player player, PlayerSkills skills)
        => skills.CanBlock && CanUseEquippedWeapon(player, WeaponAttackType.OffAttack)
            && player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.OffHand) is { } offHand
            && !IsBroken(offHand) && offHand.Template.Block != 0;

    /// <summary>
    /// A melee swing resolved (vmangos Unit::ProcSkillsAndReactives via ProcDamageAndSpell, SpellCaster.cpp:271-283):
    /// every outcome but evade is a chance for the attacker's weapon skill, and for the victim's defense skill while
    /// the victim is still alive. Spell weapon-damage skill-ups (a procSpell requiring a weapon) wait for the
    /// spell item data.
    /// </summary>
    public static void OnMeleeResolved(Unit attacker, Unit victim, WeaponAttackType attackType, MeleeHitOutcome outcome, ShapeshiftFormCatalog? forms = null)
    {
        if (outcome == MeleeHitOutcome.Evade)
        {
            return;
        }

        if (attacker is Player { Skills: { } attackerSkills } attackingPlayer)
        {
            Item? item = WeaponForAttack(attackingPlayer, attackType, true, true);
            uint skill = item is not null ? item.Template.ProficiencySkill() : attackType == WeaponAttackType.BaseAttack ? SkillIds.Unarmed : 0;
            bool fishingPole = attackType == WeaponAttackType.BaseAttack && item is not null && item.Template.SubClass == FishingPoleSubClass;
            attackerSkills.UpdateCombatSkills(new CombatSkillContext(
                Defence: false,
                Attack: (SkillAttack)attackType,
                VictimLevel: victim.Level,
                VictimIsPlayerControlled: victim is Player,
                ShapeShifted: FormQueries.IsShapeShifted(attackingPlayer, forms),
                WeaponSkillId: skill,
                CanGainSkill: !fishingPole,
                Intellect: attackingPlayer.GetUInt32(UpdateFields.UnitFieldStat0 + IntellectStat)));
        }

        if (victim is Player { Skills: { } victimSkills } defendingPlayer && victim.IsAlive)
        {
            victimSkills.UpdateCombatSkills(new CombatSkillContext(
                Defence: true,
                Attack: SkillAttack.Base,
                VictimLevel: attacker.Level,
                VictimIsPlayerControlled: attacker is Player,
                ShapeShifted: FormQueries.IsShapeShifted(defendingPlayer, forms),
                WeaponSkillId: 0,
                CanGainSkill: true,
                Intellect: 0f));
        }
    }

    private static bool IsBroken(Item item) => item.MaxDurability > 0 && item.Durability == 0;
}

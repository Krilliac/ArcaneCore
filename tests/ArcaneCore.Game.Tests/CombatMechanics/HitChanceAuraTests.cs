using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.SpellRules;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Items;
using Xunit;

namespace ArcaneCore.Game.Tests.CombatMechanics;

/// <summary>
/// The +hit and attacker-side auras of the melee and ranged tables (vmangos SpellCaster::GetMeleeMissChance, SpellCaster.cpp:336-424;
/// Player::GetWeaponBasedAuraModifier / Creature::GetWeaponBasedAuraModifier, StatSystem.cpp:482-503, 959-976; Unit::GetUnitCriticalChance,
/// GetUnitDodgeChance, GetUnitParryChance, GetUnitBlockChance, Unit.cpp:2474-2595):
/// <list type="bullet">
/// <item>SPELL_AURA_MOD_HIT_CHANCE (54) counts only when the spell's EquippedItemClass requirement fits the hand's weapon (Precision fits
/// melee weapons, a ranged hit bonus fits bows and guns).</item>
/// <item>The victim's SPELL_AURA_MOD_ATTACKER_MELEE_HIT_CHANCE (184) / _RANGED_ (185) lower the miss chance of white swings and of melee and
/// ranged spells alike; MOD_ATTACKER_MELEE/RANGED_CRIT_CHANCE (187/188) raise the white crit chance.</item>
/// <item>A creature attacker's base crit is 5 plus its MOD_CRIT_PERCENT; a creature victim's 5% dodge, parry and block take its
/// MOD_DODGE/PARRY/BLOCK_PERCENT auras.</item>
/// </list>
/// </summary>
public sealed class HitChanceAuraTests
{
    private const uint MeleeHit3 = 989_400;         // Precision-like: +3 hit with a one-handed sword, axe or mace
    private const uint RangedHit3 = 989_401;        // +3 hit with a bow or gun
    private const uint GenericHit2 = 989_402;       // +2 hit, any weapon
    private const uint AttackerMelee10 = 989_403;   // victim aura: attackers' melee hit +10
    private const uint AttackerRanged10 = 989_404;  // victim aura: attackers' ranged hit +10
    private const uint AttackerMeleeCrit5 = 989_405;
    private const uint CreatureCrit4 = 989_406;
    private const uint CreatureDodge3 = 989_407;
    private const uint CreatureParry2 = 989_408;
    private const uint CreatureBlock4 = 989_409;
    private const uint MeleeStrike = 989_410;       // a melee class spell (Sinister Strike-like) for the spell table
    private const uint RangedShot = 989_411;        // a ranged class spell

    private const uint Sword = 989_500;
    private const uint Bow = 989_501;
    private const uint Dagger = 989_502;

    private const int SubclassMaskOneHandedMelee = (1 << 0) | (1 << 4) | (1 << 7); // axe, mace, sword
    private const int SubclassMaskBowGun = (1 << 2) | (1 << 3);

    private static readonly ItemTemplateStore s_items = new(
    [
        new() { Entry = Sword, Class = 2, SubClass = 7, Name = "Test Sword", InventoryType = 13, Delay = 2000, MaxDurability = 50, Damages = [new ItemDamage(10, 20, 0)] },
        new() { Entry = Dagger, Class = 2, SubClass = 15, Name = "Test Dagger", InventoryType = 13, Delay = 1500, MaxDurability = 50, Damages = [new ItemDamage(5, 10, 0)] },
        new() { Entry = Bow, Class = 2, SubClass = 2, Name = "Test Bow", InventoryType = 15, Delay = 3000, MaxDurability = 50, Damages = [new ItemDamage(10, 20, 0)] },
    ], []);

    private static SpellInfo WeaponAura(uint id, AuraType type, int amount, int itemClass, int subclassMask) =>
        RuleTestSupport.Grant(id, type, amount) with
        {
            EquippedItemClass = itemClass,
            EquippedItemSubClassMask = subclassMask,
            Attributes = SpellAttributes.Passive,
        };

    private static SpellTestKit Kit() => new(
        WeaponAura(MeleeHit3, AuraType.ModHitChance, 3, 2, SubclassMaskOneHandedMelee),
        WeaponAura(RangedHit3, AuraType.ModHitChance, 3, 2, SubclassMaskBowGun),
        RuleTestSupport.Grant(GenericHit2, AuraType.ModHitChance, 2),
        RuleTestSupport.Grant(AttackerMelee10, AuraType.ModAttackerMeleeHitChance, 10),
        RuleTestSupport.Grant(AttackerRanged10, AuraType.ModAttackerRangedHitChance, 10),
        RuleTestSupport.Grant(AttackerMeleeCrit5, AuraType.ModAttackerMeleeCritChance, 5),
        RuleTestSupport.Grant(CreatureCrit4, AuraType.ModCritPercent, 4),
        RuleTestSupport.Grant(CreatureDodge3, AuraType.ModDodgePercent, 3),
        RuleTestSupport.Grant(CreatureParry2, AuraType.ModParryPercent, 2),
        RuleTestSupport.Grant(CreatureBlock4, AuraType.ModBlockPercent, 4),
        SpellTestKit.Spell(MeleeStrike, SpellTestKit.Effect(SpellEffectName.WeaponDamage, 10, SpellImplicitTarget.UnitEnemy)) with { DamageClass = SpellDamageClass.Melee },
        SpellTestKit.Spell(RangedShot, SpellTestKit.Effect(SpellEffectName.WeaponDamage, 10, SpellImplicitTarget.UnitEnemy)) with { DamageClass = SpellDamageClass.Ranged });

    private static (SpellTestKit Kit, Player Attacker, Player Victim, MapCombat Combat) PvpScene()
    {
        SpellTestKit kit = Kit();
        (Player attacker, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2);
        attacker.Level = victim.Level = 60;
        MapCombat combat = attacker.Map!.Combat;
        combat.SpellMitigation = kit.System;
        return (kit, attacker, victim, combat);
    }

    private static void Equip(Player player, uint entry)
    {
        PlayerInventory inventory = player.Inventory;
        ItemTestData.Wire(inventory).Templates = s_items;
        Item item = ItemTestData.Give(inventory, entry);
        inventory.AutoEquipItem(item.BagSlot, item.Slot);
        Assert.Contains(inventory.Equipped, e => ReferenceEquals(e.Item, item));
    }

    private static float Miss(MapCombat combat, Unit attacker, Unit victim, WeaponAttackType attack)
    {
        MeleeRollInput input = combat.BuildRollInput(attacker, victim, attack);
        return MeleeHitTable.MissChance(input, input.AttackerWeaponSkill - input.VictimDefenseSkill);
    }

    // --- registration ----------------------------------------------------------------------------

    [Fact]
    public void TheHitAndAttackerAuras_HaveHandlers_AndTheirSupportRowsSayHandler()
    {
        using SpellTestKit kit = Kit();
        AuraType[] types =
        [
            AuraType.ModHitChance, AuraType.ModSpellHitChance, AuraType.ModAttackerMeleeHitChance, AuraType.ModAttackerRangedHitChance,
            AuraType.ModAttackerSpellHitChance, AuraType.ModAttackerMeleeCritChance, AuraType.ModAttackerRangedCritChance,
        ];

        Assert.All(types, type =>
        {
            Assert.True(kit.System.HasAuraHandler(type), type.ToString());
            Assert.Equal(AuraSupportLevel.Handler, AuraSupport.Get(type).Level);
        });
    }

    // --- MOD_HIT_CHANCE weapon requirement -------------------------------------------------------

    [Fact]
    public void AWeaponRestrictedHitAura_CountsOnlyForAHandWhoseWeaponFits()
    {
        (SpellTestKit kit, Player attacker, Player victim, MapCombat combat) = PvpScene();
        using SpellTestKit _ = kit;
        RuleTestSupport.Apply(kit, attacker, MeleeHit3);
        RuleTestSupport.Apply(kit, attacker, RangedHit3);
        RuleTestSupport.Apply(kit, attacker, GenericHit2);

        Assert.Equal(2f, combat.BuildRollInput(attacker, victim, WeaponAttackType.BaseAttack).HitBonus);   // no weapon: only the generic aura
        Equip(attacker, Sword);
        Assert.Equal(5f, combat.BuildRollInput(attacker, victim, WeaponAttackType.BaseAttack).HitBonus);   // the sword fits the melee aura
        Assert.Equal(2f, combat.BuildRollInput(attacker, victim, WeaponAttackType.RangedAttack).HitBonus); // no ranged weapon
        Equip(attacker, Bow);
        Assert.Equal(5f, combat.BuildRollInput(attacker, victim, WeaponAttackType.RangedAttack).HitBonus);
    }

    [Fact]
    public void AWeaponRestrictedHitAura_IgnoresAWeaponOutsideItsSubclassMask()
    {
        (SpellTestKit kit, Player attacker, Player victim, MapCombat combat) = PvpScene();
        using SpellTestKit _ = kit;
        RuleTestSupport.Apply(kit, attacker, MeleeHit3);
        Equip(attacker, Dagger);

        Assert.Equal(0f, combat.BuildRollInput(attacker, victim, WeaponAttackType.BaseAttack).HitBonus);
    }

    [Fact]
    public void ACreatureWeaponRestrictedHitAura_ReadsItsVirtualItem()
    {
        using SpellTestKit kit = Kit();
        (Player victim, _) = kit.AddPlayer(1);
        var attacker = new CombatTestUnit();
        attacker.Spawn(victim.Map!, 2, 0);
        MapCombat combat = victim.Map!.Combat;
        combat.SpellMitigation = kit.System;
        RuleTestSupport.Apply(kit, attacker, MeleeHit3);

        Assert.Equal(0f, combat.BuildRollInput(attacker, victim, WeaponAttackType.BaseAttack).HitBonus); // no virtual weapon

        attacker.SetUInt32(UpdateFields.UnitVirtualItemSlotDisplay, 1234);
        attacker.SetByte(UpdateFields.UnitVirtualItemInfo, 0, 2);  // class weapon
        attacker.SetByte(UpdateFields.UnitVirtualItemInfo, 1, 7);  // subclass sword
        Assert.Equal(3f, combat.BuildRollInput(attacker, victim, WeaponAttackType.BaseAttack).HitBonus);
    }

    // --- victim side -----------------------------------------------------------------------------

    [Fact]
    public void TheVictimsAttackerMeleeHitAura_LowersTheWhiteSwingMissChance()
    {
        (SpellTestKit kit, Player attacker, Player victim, MapCombat combat) = PvpScene();
        using SpellTestKit _ = kit;
        Assert.Equal(5f, Miss(combat, attacker, victim, WeaponAttackType.BaseAttack));
        float ranged = Miss(combat, attacker, victim, WeaponAttackType.RangedAttack);

        RuleTestSupport.Apply(kit, victim, AttackerMelee10);

        Assert.Equal(0f, Miss(combat, attacker, victim, WeaponAttackType.BaseAttack));        // 5 - 10, floored at 0
        Assert.Equal(ranged, Miss(combat, attacker, victim, WeaponAttackType.RangedAttack));  // the melee aura leaves ranged alone

        RuleTestSupport.Apply(kit, victim, AttackerRanged10);
        Assert.Equal(ranged - 10f, Miss(combat, attacker, victim, WeaponAttackType.RangedAttack), 3);
    }

    [Fact]
    public void TheVictimsAttackerHitAura_IsNotTheFirstPercentOfPlusHit()
    {
        // vmangos ignores the first 1% of the attacker's +hit against a defense 11+ points above the weapon skill; the victim's aura is
        // subtracted afterwards and loses nothing.
        MeleeRollInput input = new()
        {
            VictimStanding = true,
            VictimLevel = 60,
            HitBonus = 0f,
            VictimAttackerHitBonus = 2f,
        };

        Assert.Equal(5f + (15 * 0.2f) - 2f, MeleeHitTable.MissChance(input, -15), 3);
    }

    [Fact]
    public void TheVictimsAttackerMeleeHitAura_LowersTheMeleeSpellMissChance()
    {
        (SpellTestKit kit, Player attacker, Player victim, MapCombat combat) = PvpScene();
        using SpellTestKit _ = kit;
        RuleTestSupport.Apply(kit, victim, AttackerMelee10);
        var rules = new VanillaSpellCombatRules();
        kit.System.Random = new Random(3);

        // With the 5% miss gone and the victim facing away (no dodge or parry from behind against a player), every swing lands.
        SpellInfo strike = kit.Store.Get(MeleeStrike)!;
        victim.Relocate(2, 0, victim.Z, 0f, 0);
        for (int i = 0; i < 200; i++)
        {
            Assert.Equal(SpellMissInfo.None, rules.RollHit(kit.System, attacker, victim, strike));
        }
    }

    [Fact]
    public void TheVictimsAttackerMeleeCritAura_RaisesTheWhiteCritChance()
    {
        (SpellTestKit kit, Player attacker, Player victim, MapCombat combat) = PvpScene();
        using SpellTestKit _ = kit;
        attacker.SetFloat(UpdateFields.PlayerCritPercentage, 10f);
        float before = MeleeHitTable.CritChance(combat.BuildRollInput(attacker, victim, WeaponAttackType.BaseAttack));

        RuleTestSupport.Apply(kit, victim, AttackerMeleeCrit5);

        Assert.Equal(before + 5f, MeleeHitTable.CritChance(combat.BuildRollInput(attacker, victim, WeaponAttackType.BaseAttack)), 3);
    }

    [Fact]
    public void TheMeleeSpellCritChance_CountsTheVictimsAttackerCritAuraOnce()
    {
        (SpellTestKit kit, Player attacker, Player victim, MapCombat combat) = PvpScene();
        using SpellTestKit _ = kit;
        attacker.SetFloat(UpdateFields.PlayerCritPercentage, 10f);
        var rules = new VanillaSpellCombatRules();
        SpellInfo strike = kit.Store.Get(MeleeStrike)!;
        float before = rules.CritChance(kit.System, attacker, victim, strike);

        RuleTestSupport.Apply(kit, victim, AttackerMeleeCrit5);

        Assert.Equal(before + 5f, rules.CritChance(kit.System, attacker, victim, strike), 3);
    }

    // --- creatures -------------------------------------------------------------------------------

    [Fact]
    public void ACreatureAttackersBaseCrit_IsFivePlusItsCritPercentAuras()
    {
        using SpellTestKit kit = Kit();
        (Player victim, _) = kit.AddPlayer(1);
        var attacker = new CombatTestUnit();
        attacker.Spawn(victim.Map!, 2, 0);
        MapCombat combat = victim.Map!.Combat;
        combat.SpellMitigation = kit.System;
        Assert.Equal(5f, combat.BuildRollInput(attacker, victim, WeaponAttackType.BaseAttack).BaseCritChance);

        RuleTestSupport.Apply(kit, attacker, CreatureCrit4);

        Assert.Equal(9f, combat.BuildRollInput(attacker, victim, WeaponAttackType.BaseAttack).BaseCritChance);
    }

    [Fact]
    public void ACreatureVictimsDefense_TakesItsDodgeParryAndBlockAuras()
    {
        using SpellTestKit kit = Kit();
        (Player attacker, _) = kit.AddPlayer(1);
        var victim = new CombatTestUnit();
        victim.Spawn(attacker.Map!, 2, 0, orientation: MathF.PI); // faces the attacker
        MapCombat combat = attacker.Map!.Combat;
        combat.SpellMitigation = kit.System;
        RuleTestSupport.Apply(kit, victim, CreatureDodge3);
        RuleTestSupport.Apply(kit, victim, CreatureParry2);
        RuleTestSupport.Apply(kit, victim, CreatureBlock4);

        MeleeRollInput input = combat.BuildRollInput(attacker, victim, WeaponAttackType.BaseAttack);

        Assert.Equal((8f, 7f, 9f), (input.DodgeChance, input.ParryChance, input.BlockChance));
    }

    [Fact]
    public void APlayersRangedRoll_StartsFromTheRangedCritField()
    {
        (SpellTestKit kit, Player attacker, Player victim, MapCombat combat) = PvpScene();
        using SpellTestKit _ = kit;
        attacker.SetFloat(UpdateFields.PlayerCritPercentage, 10f);
        attacker.SetFloat(UpdateFields.PlayerRangedCritPercentage, 7f);

        Assert.Equal(7f, combat.BuildRollInput(attacker, victim, WeaponAttackType.RangedAttack).BaseCritChance);
        Assert.Equal(10f, combat.BuildRollInput(attacker, victim, WeaponAttackType.BaseAttack).BaseCritChance);
    }
}

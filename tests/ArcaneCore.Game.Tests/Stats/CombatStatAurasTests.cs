using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Stats;
using ArcaneCore.Game.Tests.SpellRules;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;

namespace ArcaneCore.Game.Tests.Stats;

/// <summary>
/// The combat stat auras through the spell system and a <see cref="PlayerStatSystem"/>: each aura is applied, the field the client and the hit
/// table read is compared with the value the vmangos formula gives (StatSystem.cpp, SpellAuras.cpp, Player.cpp _ApplyWeaponDependentAura*Mod), then
/// removed, and the field must return. Expected values are relative to the field before the aura (the formulas multiply or add on top of it), so
/// they do not depend on the base data.
/// </summary>
public sealed class CombatStatAurasTests
{
    private const float Tol = 0.01f;

    private const uint Crit5 = 989_600;
    private const uint AxeCrit3 = 989_601;
    private const uint Disarm = 989_602;
    private const uint Offhand50 = 989_603;
    private const uint PhysicalPct10 = 989_604;
    private const uint TwoHandPct5 = 989_605;
    private const uint PhysicalFlat5 = 989_606;
    private const uint AttackPower100 = 989_607;
    private const uint SwordParry2 = 989_608;
    private const uint FireDamage20 = 989_609;
    private const uint SpiritSpellDamage25 = 989_610;
    private const uint Spirit40 = 989_611;
    private const uint FirePct10 = 989_612;
    private const uint AttackPowerMinus150 = 989_613;
    private const uint SwordDamage7 = 989_614;
    private const uint TotalSpirit100 = 989_615;

    private const uint Sword = 989_700;   // one-handed sword, 2.6 s
    private const uint Axe = 989_701;     // one-handed axe
    private const uint OffhandSword = 989_702;
    private const uint Bow = 989_703;

    private static uint s_creatureGuid = 989_800;

    private static readonly ItemTemplateStore s_items = new(
    [
        new() { Entry = Sword, Class = 2, SubClass = 7, Name = "Test Sword", InventoryType = 13, Delay = 2600, MaxDurability = 50, Damages = [new ItemDamage(20, 30, 0)] },
        new() { Entry = Axe, Class = 2, SubClass = 0, Name = "Test Axe", InventoryType = 13, Delay = 2600, MaxDurability = 50, Damages = [new ItemDamage(20, 30, 0)] },
        new() { Entry = OffhandSword, Class = 2, SubClass = 7, Name = "Test Offhand Sword", InventoryType = 13, Delay = 1800, MaxDurability = 50, Damages = [new ItemDamage(10, 16, 0)] },
        new() { Entry = Bow, Class = 2, SubClass = 2, Name = "Test Bow", InventoryType = 15, Delay = 3000, MaxDurability = 50, Damages = [new ItemDamage(10, 20, 0)] },
    ], []);

    private static SpellInfo Weapon(SpellInfo spell, int subclassMask) => spell with
    {
        EquippedItemClass = 2,
        EquippedItemSubClassMask = subclassMask,
        Attributes = SpellAttributes.Passive,
    };

    private static SpellTestKit Kit() => new(
        RuleTestSupport.Grant(Crit5, AuraType.ModCritPercent, 5),
        Weapon(RuleTestSupport.Grant(AxeCrit3, AuraType.ModCritPercent, 3), 1 << 0),
        RuleTestSupport.Grant(Disarm, AuraType.ModDisarm, 0),
        RuleTestSupport.Grant(Offhand50, AuraType.ModOffhandDamagePct, 50),
        RuleTestSupport.Grant(PhysicalPct10, AuraType.ModDamagePercentDone, 10, misc: 1),
        Weapon(RuleTestSupport.Grant(TwoHandPct5, AuraType.ModDamagePercentDone, 5, misc: 1), (1 << 1) | (1 << 5) | (1 << 8)),
        RuleTestSupport.Grant(PhysicalFlat5, AuraType.ModDamageDone, 5, misc: 1),
        RuleTestSupport.Grant(AttackPower100, AuraType.ModAttackPower, 100),
        Weapon(RuleTestSupport.Grant(SwordParry2, AuraType.ModParryPercent, 2), 1 << 7),
        RuleTestSupport.Grant(FireDamage20, AuraType.ModDamageDone, 20, misc: 1 << (int)SpellSchool.Fire),
        RuleTestSupport.Grant(SpiritSpellDamage25, AuraType.ModSpellDamageOfStatPercent, 25, misc: 0x7E),
        RuleTestSupport.Grant(Spirit40, AuraType.ModStat, 40, misc: 4),
        RuleTestSupport.Grant(FirePct10, AuraType.ModDamagePercentDone, 10, misc: 1 << (int)SpellSchool.Fire),
        RuleTestSupport.Grant(AttackPowerMinus150, AuraType.ModAttackPower, -150),
        Weapon(RuleTestSupport.Grant(SwordDamage7, AuraType.ModDamageDone, 7, misc: 1), 1 << 7),
        RuleTestSupport.Grant(TotalSpirit100, AuraType.ModTotalStatPercentage, 100, misc: 4));

    /// <summary>A level 60 player with str 120, agi 80, sta 100, int 20, spi 100, attached to a stat system.</summary>
    private static Player Player(SpellTestKit kit, uint guid = 1)
    {
        (Player player, _) = kit.AddPlayer(guid);
        player.Level = 60;
        uint[] stats = [120, 80, 100, 20, 100];
        for (int i = 0; i < stats.Length; i++)
        {
            player.SetUInt32(UpdateFields.UnitFieldStat0 + i, stats[i]);
        }

        ItemTestData.Wire(player.Inventory).Templates = s_items;
        new PlayerStatSystem().Attach(player);
        return player;
    }

    private static Item Equip(Player player, uint entry, byte? slot = null)
    {
        PlayerInventory inventory = player.Inventory;
        Item item = ItemTestData.Give(inventory, entry);
        if (slot is { } target)
        {
            inventory.SwapItem(item.BagSlot, item.Slot, InventorySlots.Bag0, target);
        }
        else
        {
            inventory.AutoEquipItem(item.BagSlot, item.Slot);
        }

        Assert.Contains(inventory.Equipped, e => ReferenceEquals(e.Item, item));
        return item;
    }

    private static void Apply(SpellTestKit kit, Unit unit, uint spell) => RuleTestSupport.Apply(kit, unit, spell);

    private static float F(Unit unit, int field) => unit.GetFloat(field);

    private static (float Min, float Max) MainHand(Unit unit) => (F(unit, UpdateFields.UnitFieldMindamage), F(unit, UpdateFields.UnitFieldMaxdamage));

    private static (float Min, float Max) OffHand(Unit unit) => (F(unit, UpdateFields.UnitFieldMinoffhanddamage), F(unit, UpdateFields.UnitFieldMaxoffhanddamage));

    // --- registration ----------------------------------------------------------------------------

    [Fact]
    public void TheStatAuraTypes_HaveHandlers_AndTheirSupportRowsSayHandler()
    {
        using SpellTestKit kit = Kit();
        AuraType[] types = [AuraType.ModCritPercent, AuraType.ModDamageDone, AuraType.ModDamagePercentDone, AuraType.ModOffhandDamagePct, AuraType.ModSpellDamageOfStatPercent];

        Assert.All(types, type =>
        {
            Assert.True(kit.System.HasAuraHandler(type), type.ToString());
            Assert.Equal(AuraSupportLevel.Handler, AuraSupport.Get(type).Level);
        });
    }

    // --- crit ------------------------------------------------------------------------------------

    [Fact]
    public void AGenericCritAura_RaisesBothCritFields_AndRemovalGivesThemBack()
    {
        using SpellTestKit kit = Kit();
        Player player = Player(kit);
        Equip(player, Sword);
        Equip(player, Bow);   // a ranged skill, so the ranged field is above its 0 floor
        float melee = F(player, UpdateFields.PlayerCritPercentage);
        float ranged = F(player, UpdateFields.PlayerRangedCritPercentage);

        Apply(kit, player, Crit5);
        Assert.Equal(melee + 5f, F(player, UpdateFields.PlayerCritPercentage), Tol);
        Assert.Equal(ranged + 5f, F(player, UpdateFields.PlayerRangedCritPercentage), Tol);

        kit.System.RemoveAuras(player, Crit5);
        Assert.Equal(melee, F(player, UpdateFields.PlayerCritPercentage), Tol);
        Assert.Equal(ranged, F(player, UpdateFields.PlayerRangedCritPercentage), Tol);
    }

    [Fact]
    public void AWeaponCritAura_CountsWithAFittingMainHand_AndNotWithAnotherWeapon()
    {
        using SpellTestKit kit = Kit();
        Player axeUser = Player(kit, 1);
        Player swordUser = Player(kit, 2);
        Equip(axeUser, Axe);
        Equip(swordUser, Sword);
        float axeBefore = F(axeUser, UpdateFields.PlayerCritPercentage);
        float swordBefore = F(swordUser, UpdateFields.PlayerCritPercentage);

        Apply(kit, axeUser, AxeCrit3);
        Apply(kit, swordUser, AxeCrit3);

        Assert.Equal(axeBefore + 3f, F(axeUser, UpdateFields.PlayerCritPercentage), Tol);
        Assert.Equal(swordBefore, F(swordUser, UpdateFields.PlayerCritPercentage), Tol);
    }

    // --- disarm ----------------------------------------------------------------------------------

    [Fact]
    public void Disarm_SwingsTheMainHandUnarmedAtTheBaseTime_DropsItsWeaponCrit_AndRemovalRestoresBoth()
    {
        using SpellTestKit kit = Kit();
        Player player = Player(kit);
        Equip(player, Axe);
        Apply(kit, player, AxeCrit3);
        (float min, float max) armed = MainHand(player);
        float crit = F(player, UpdateFields.PlayerCritPercentage);
        Assert.Equal(2600u, player.GetUInt32(UpdateFields.UnitFieldBaseattacktime));
        float apOver14 = player.GetInt32(UpdateFields.UnitFieldAttackPower) / 14f;

        Apply(kit, player, Disarm);

        Assert.True((player.UnitFlags & UnitFlags.Disarmed) != 0);
        Assert.Equal(2000u, player.GetUInt32(UpdateFields.UnitFieldBaseattacktime));                 // SetAttackTime(BASE_ATTACK, BASE_ATTACK_TIME)
        Assert.Equal(1f + (apOver14 * 2.0f), F(player, UpdateFields.UnitFieldMindamage), Tol);         // BASE_MINDAMAGE + AP / 14 at 2.0 s
        Assert.Equal(2f + (apOver14 * 2.0f), F(player, UpdateFields.UnitFieldMaxdamage), Tol);
        Assert.Equal(crit - 3f, F(player, UpdateFields.PlayerCritPercentage), Tol);                    // the axe no longer counts

        kit.System.RemoveAuras(player, Disarm);

        Assert.True((player.UnitFlags & UnitFlags.Disarmed) == 0);
        Assert.Equal(2600u, player.GetUInt32(UpdateFields.UnitFieldBaseattacktime));
        Assert.Equal(armed, MainHand(player));
        Assert.Equal(crit, F(player, UpdateFields.PlayerCritPercentage), Tol);
    }

    // --- weapon damage ---------------------------------------------------------------------------

    [Fact]
    public void OffhandDamagePercent_RaisesTheOffHandFactorFromHalf()
    {
        using SpellTestKit kit = Kit();
        Player player = Player(kit);
        player.StatState.SetCanDualWield(true);
        Equip(player, Sword, InventorySlots.MainHand);
        Equip(player, OffhandSword, InventorySlots.OffHand);
        (float min, float max) before = OffHand(player);
        (float min, float max) mainHand = MainHand(player);
        Assert.True(before.min > 0f);

        Apply(kit, player, Offhand50);

        Assert.Equal(before.min * 1.5f, OffHand(player).Min, Tol);   // TOTAL_PCT 0.5 * 1.5
        Assert.Equal(before.max * 1.5f, OffHand(player).Max, Tol);
        Assert.Equal(mainHand, MainHand(player));
        kit.System.RemoveAuras(player, Offhand50);
        Assert.Equal(before.min, OffHand(player).Min, Tol);
        Assert.Equal(before.max, OffHand(player).Max, Tol);
    }

    [Fact]
    public void AGenericPhysicalDamagePercent_ScalesEveryHand()
    {
        using SpellTestKit kit = Kit();
        Player player = Player(kit);
        player.StatState.SetCanDualWield(true);
        Equip(player, Sword, InventorySlots.MainHand);
        Equip(player, OffhandSword, InventorySlots.OffHand);
        (float min, float max) main = MainHand(player);
        (float min, float max) off = OffHand(player);

        Apply(kit, player, PhysicalPct10);

        Assert.Equal(main.min * 1.1f, MainHand(player).Min, Tol);
        Assert.Equal(main.max * 1.1f, MainHand(player).Max, Tol);
        Assert.Equal(off.min * 1.1f, OffHand(player).Min, Tol);
        kit.System.RemoveAuras(player, PhysicalPct10);
        Assert.Equal(main.min, MainHand(player).Min, Tol);
    }

    [Fact]
    public void AWeaponRestrictedDamagePercent_IgnoresAWeaponOutsideItsMask()
    {
        using SpellTestKit kit = Kit();
        Player player = Player(kit);
        Equip(player, Sword);
        (float min, float max) before = MainHand(player);

        Apply(kit, player, TwoHandPct5);

        Assert.Equal(before, MainHand(player));
    }

    [Fact]
    public void PhysicalFlatDamage_AddsToTheHand_AndAWeaponRestrictedOneOnlyToTheFittingHand()
    {
        using SpellTestKit kit = Kit();
        Player player = Player(kit);
        player.StatState.SetCanDualWield(true);
        Equip(player, Axe, InventorySlots.MainHand);
        Equip(player, OffhandSword, InventorySlots.OffHand);
        (float min, float max) main = MainHand(player);
        (float min, float max) off = OffHand(player);

        Apply(kit, player, PhysicalFlat5);
        Assert.Equal(main.min + 5f, MainHand(player).Min, Tol);          // UNIT_MOD_DAMAGE_PHYSICAL, TOTAL_PCT 1
        Assert.Equal(off.min + 2.5f, OffHand(player).Min, Tol);          // ... times the off hand's 0.5

        Apply(kit, player, SwordDamage7);
        Assert.Equal(main.min + 5f, MainHand(player).Min, Tol);          // the axe does not fit
        Assert.Equal(off.min + 2.5f + 3.5f, OffHand(player).Min, Tol);   // the off-hand sword does: TOTAL_VALUE of that hand

        kit.System.RemoveAuras(player, PhysicalFlat5);
        kit.System.RemoveAuras(player, SwordDamage7);
        Assert.Equal(main, MainHand(player));
        Assert.Equal(off.min, OffHand(player).Min, Tol);
    }

    [Fact]
    public void AFlatAttackPowerAura_RecomputesTheDamageFields()
    {
        using SpellTestKit kit = Kit();
        Player player = Player(kit);
        Equip(player, Sword);
        (float min, float max) before = MainHand(player);

        Apply(kit, player, AttackPower100);

        Assert.Equal(before.min + (100f / 14f * 2.6f), MainHand(player).Min, Tol);
        kit.System.RemoveAuras(player, AttackPower100);
        Assert.Equal(before.min, MainHand(player).Min, Tol);
    }

    // --- parry -----------------------------------------------------------------------------------

    [Fact]
    public void AWeaponParryAura_CountsWithAFittingMainHandOnly()
    {
        using SpellTestKit kit = Kit();
        Player swordUser = Player(kit, 1);
        Player axeUser = Player(kit, 2);
        swordUser.StatState.SetCanParry(true);
        axeUser.StatState.SetCanParry(true);
        Equip(swordUser, Sword);
        Equip(axeUser, Axe);
        float sword = F(swordUser, UpdateFields.PlayerParryPercentage);
        float axe = F(axeUser, UpdateFields.PlayerParryPercentage);

        Apply(kit, swordUser, SwordParry2);
        Apply(kit, axeUser, SwordParry2);

        Assert.Equal(sword + 2f, F(swordUser, UpdateFields.PlayerParryPercentage), Tol);
        Assert.Equal(axe, F(axeUser, UpdateFields.PlayerParryPercentage), Tol);
        kit.System.RemoveAuras(swordUser, SwordParry2);
        Assert.Equal(sword, F(swordUser, UpdateFields.PlayerParryPercentage), Tol);
    }

    // --- spell damage display --------------------------------------------------------------------

    [Fact]
    public void TheDamageDoneFields_FollowTheAurasAndTheSpirit()
    {
        using SpellTestKit kit = Kit();
        Player player = Player(kit);
        int fire = (int)SpellSchool.Fire;
        int pos = UpdateFields.PlayerFieldModDamageDonePos;
        int pct = UpdateFields.PlayerFieldModDamageDonePct;
        Assert.All(Enumerable.Range(0, 7), school => Assert.Equal(1.0f, player.GetFloat(pct + school)));

        Apply(kit, player, FireDamage20);
        Assert.Equal(20u, player.GetUInt32(pos + fire));
        Assert.Equal(0u, player.GetUInt32(pos + (int)SpellSchool.Frost));

        Apply(kit, player, SpiritSpellDamage25);
        Assert.Equal(45u, player.GetUInt32(pos + fire));                               // + 25% of 100 spirit
        Assert.Equal(25u, player.GetUInt32(pos + (int)SpellSchool.Frost));
        Assert.Equal(0u, player.GetUInt32(pos));                                         // not the physical school

        Apply(kit, player, Spirit40);
        Assert.Equal(55u, player.GetUInt32(pos + fire));                               // a stat change recomputes: 25% of 140

        Apply(kit, player, FirePct10);
        Assert.Equal(1.1f, player.GetFloat(pct + fire), 3);

        kit.System.RemoveAuras(player, Spirit40);
        kit.System.RemoveAuras(player, FireDamage20);
        kit.System.RemoveAuras(player, FirePct10);
        Assert.Equal(25u, player.GetUInt32(pos + fire));
        Assert.Equal(1.0f, player.GetFloat(pct + fire));
    }

    // --- mana regeneration -----------------------------------------------------------------------

    [Fact]
    public void ManaRegeneration_ReadsTheSpiritAfterAPercentStatChange()
    {
        using SpellTestKit kit = Kit();
        Player player = Player(kit);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Mage);
        player.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
        player.SetUInt32(UpdateFields.UnitFieldMaxpower1, 5000);
        CombatEnvironment.Register(kit.World, new CombatEnvironment(new CombatOptions(), new SpellSystemPowerAuras(kit.System)));

        uint Tick()
        {
            player.SetUInt32(UpdateFields.UnitFieldPower1, 0);
            player.Combat.LastManaUseTimer = 0;
            player.Combat.RegenTimer = 0;
            kit.World.RunTick(50);
            return player.GetUInt32(UpdateFields.UnitFieldPower1);
        }

        Assert.Equal(PowerRules.ManaPerTick(MapCombat.RegenManaPerSpirit(Class.Mage, 100), 1f), Tick());
        Apply(kit, player, TotalSpirit100);
        Assert.Equal(200u, player.GetUInt32(UpdateFields.UnitFieldStat0 + 4));
        Assert.Equal(PowerRules.ManaPerTick(MapCombat.RegenManaPerSpirit(Class.Mage, 200), 1f), Tick());
    }

    // --- creatures -------------------------------------------------------------------------------

    private static Creature Creature(Player near, bool weapon = false)
    {
        CreatureTemplate template = new()
        {
            Entry = 989_800, Name = "Stat Dummy", MinLevel = 60, MaxLevel = 60, MinLevelHealth = 10_000, MaxLevelHealth = 10_000, DisplayIds = [1],
            Faction = 14, MinMeleeDamage = 100, MaxMeleeDamage = 200, MeleeAttackPower = 300,
        };
        var creature = new Creature(Interlocked.Increment(ref s_creatureGuid), template, null, new CreatureContent([template], [], [], [], []), new Random(1));
        creature.Relocate(2, 0, 83.5f, MathF.PI, 0);
        creature.MapId = 0;
        near.Map!.AddObject(creature);
        if (weapon)
        {
            creature.SetUInt32(UpdateFields.UnitVirtualItemSlotDisplay, 1234);
            creature.SetByte(UpdateFields.UnitVirtualItemInfo, 0, 2);
            creature.SetByte(UpdateFields.UnitVirtualItemInfo, 1, 7);
        }

        return creature;
    }

    [Fact]
    public void ACreaturesAttackPowerAura_MovesItsDamageByThirtyPercentOfTheRatio()
    {
        using SpellTestKit kit = Kit();
        Player player = Player(kit);
        Creature creature = Creature(player);
        Assert.Equal((100f, 200f), MainHand(creature));

        Apply(kit, creature, AttackPowerMinus150);   // Demoralizing Shout-like: 300 -> 150 attack power

        Assert.Equal(85f, MainHand(creature).Min, Tol);   // 100 * (0.7 + 0.3 * 150 / 300)
        Assert.Equal(170f, MainHand(creature).Max, Tol);
        kit.System.RemoveAuras(creature, AttackPowerMinus150);
        Assert.Equal((100f, 200f), MainHand(creature));
    }

    [Fact]
    public void ADisarmedCreature_KeepsFortyPercentWithAWeapon_AndAllWithout()
    {
        using SpellTestKit kit = Kit();
        Player player = Player(kit);
        Creature armed = Creature(player, weapon: true);
        Creature unarmed = Creature(player);

        Apply(kit, armed, Disarm);
        Apply(kit, unarmed, Disarm);

        Assert.Equal((40f, 80f), MainHand(armed));
        Assert.Equal((100f, 200f), MainHand(unarmed));
        kit.System.RemoveAuras(armed, Disarm);
        Assert.Equal((100f, 200f), MainHand(armed));
    }

    [Fact]
    public void ACreaturesPhysicalDamagePercentAndFlat_MoveItsDamage()
    {
        using SpellTestKit kit = Kit();
        Player player = Player(kit);
        Creature creature = Creature(player);

        Apply(kit, creature, PhysicalPct10);
        Assert.Equal(110f, MainHand(creature).Min, Tol);
        Apply(kit, creature, PhysicalFlat5);
        Assert.Equal(115.5f, MainHand(creature).Min, Tol);   // (100 + 5) * 1.1
        kit.System.RemoveAuras(creature, PhysicalPct10);
        kit.System.RemoveAuras(creature, PhysicalFlat5);
        Assert.Equal((100f, 200f), MainHand(creature));
    }
}

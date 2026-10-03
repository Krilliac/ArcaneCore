using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Stats;
using ArcaneCore.Kernel.Items;
using Xunit;

namespace ArcaneCore.Game.Tests.Stats;

/// <summary>
/// The player stat system through its production write path: items are equipped through the inventory,
/// the abilities are set the way the spell effects set them, and the update fields a client and the
/// combat code read are inspected. Expected values are worked by hand from vmangos 4b3d241
/// StatSystem.cpp, Player.cpp (_ApplyItemMods / _ApplyItemBonuses) and Unit.cpp.
/// </summary>
public sealed class PlayerStatSystemTests
{
    private const float Tol = 0.001f;
    private const float ApOver14 = 400f / 14f;                 // level 60 human warrior, str 120: 3 * 60 + 2 * 120 - 20 = 400

    private const uint Sword = 91001;
    private const uint Dagger = 91002;
    private const uint Greataxe = 91003;
    private const uint Bow = 91004;
    private const uint Shield = 91005;
    private const uint AgilityRing = 91006;
    private const uint OffhandSword = 91007;

    private static readonly ItemTemplateStore s_store = new(
    [
        new() { Entry = Sword, Class = 2, SubClass = 7, Name = "Test Sword", InventoryType = 13, Delay = 2000, MaxDurability = 50, Damages = [new ItemDamage(2, 4, 0)] },
        new() { Entry = Dagger, Class = 2, SubClass = 15, Name = "Test Dagger", InventoryType = 13, Delay = 1700, MaxDurability = 50, Damages = [new ItemDamage(3, 5, 0)] },
        new() { Entry = Greataxe, Class = 2, SubClass = 1, Name = "Test Greataxe", InventoryType = 17, Delay = 3300, MaxDurability = 50, Damages = [new ItemDamage(50, 90, 0), new ItemDamage(5, 8, 2)] },
        new() { Entry = Bow, Class = 2, SubClass = 2, Name = "Test Bow", InventoryType = 15, Delay = 2500, MaxDurability = 50, Damages = [new ItemDamage(20, 30, 0)] },
        new() { Entry = Shield, Class = 4, SubClass = 6, Name = "Test Shield", InventoryType = 14, Block = 20, Armor = 5, MaxDurability = 40 },
        new() { Entry = AgilityRing, Class = 4, SubClass = 0, Name = "Test Ring", InventoryType = 11, Stats = [new ItemStat((uint)ItemStatType.Agility, 10)] },
        new() { Entry = OffhandSword, Class = 2, SubClass = 7, Name = "Test Offhand Sword", InventoryType = 13, Delay = 1500, MaxDurability = 50, Damages = [new ItemDamage(2, 3, 0)] },
    ], []);

    private static AgilityRates Rates() => new(
        new Dictionary<Class, AgilityRateTable> { [Class.Warrior] = AgilityRateTable.FromEntries([new(1, 4f), new(60, 40f)]) },
        new Dictionary<Class, AgilityRateTable> { [Class.Warrior] = AgilityRateTable.FromEntries([new(1, 2f), new(60, 20f)]) });

    /// <summary>A level 60 human warrior with str 120, agi 80, sta 100, int 20, spi 30, attached to a stat system.</summary>
    private static (Player Player, PlayerStatSystem System, FakeSession Session) Create(AgilityRates? rates = null, bool attach = true, bool noRates = false)
    {
        (Player player, FakeSession session) = ItemTestData.CreatePlayer(level: 60);
        player.Inventory.Templates = s_store;
        uint[] stats = [120, 80, 100, 20, 30];
        for (int i = 0; i < stats.Length; i++)
        {
            player.SetUInt32(UpdateFields.UnitFieldStat0 + i, stats[i]);
        }

        var system = new PlayerStatSystem(noRates ? null : rates ?? Rates());
        if (attach)
        {
            system.Attach(player);
        }

        return (player, system, session);
    }

    private static Item Equip(Player player, uint entry, byte? slot = null)
    {
        PlayerInventory inv = player.Inventory;
        Item item = ItemTestData.Give(inv, entry);
        if (slot is { } target)
        {
            inv.SwapItem(item.BagSlot, item.Slot, InventorySlots.Bag0, target);
        }
        else
        {
            inv.AutoEquipItem(item.BagSlot, item.Slot);   // returns true only for bags; the check below is the real one
            Assert.Contains(inv.Equipped, e => ReferenceEquals(e.Item, item));
        }

        return item;
    }

    private static float F(Player player, int field) => player.GetFloat(field);

    [Fact]
    public void Attach_WritesAttackPowerArmorCritDodgeAndFistDamageForAnUnarmedWarrior()
    {
        (Player player, _, _) = Create();

        Assert.Equal(400, player.GetInt32(UpdateFields.UnitFieldAttackPower));
        Assert.Equal(130, player.GetInt32(UpdateFields.UnitFieldRangedAttackPower));   // 60 + 80 - 10
        Assert.Equal(0u, player.GetUInt32(UpdateFields.UnitFieldAttackPowerMods));
        Assert.Equal(0f, F(player, UpdateFields.UnitFieldAttackPowerMultiplier), Tol);
        Assert.Equal(160u, player.GetUInt32(UpdateFields.UnitFieldResistances));        // 2 per agility
        Assert.Equal(2.0f, F(player, UpdateFields.PlayerCritPercentage), Tol);          // 80 agility / rate 40
        Assert.Equal(0f, F(player, UpdateFields.PlayerRangedCritPercentage), Tol);      // no ranged weapon: skill 0 clamps to 0
        Assert.Equal(4.0f, F(player, UpdateFields.PlayerDodgePercentage), Tol);         // 80 agility / rate 20
        Assert.Equal(0f, F(player, UpdateFields.PlayerParryPercentage), Tol);           // the Parry ability is not known
        Assert.Equal(0f, F(player, UpdateFields.PlayerBlockPercentage), Tol);
        Assert.Equal(1f + ApOver14 * 2f, F(player, UpdateFields.UnitFieldMindamage), Tol);   // fists 1-2 + AP / 14 * 2.0
        Assert.Equal(2f + ApOver14 * 2f, F(player, UpdateFields.UnitFieldMaxdamage), Tol);
        Assert.Equal(2000u, player.GetUInt32(UpdateFields.UnitFieldBaseattacktime));
    }

    [Fact]
    public void EquippingAWeapon_WritesItsDamageAndSpeedIntoTheMainHandFields()
    {
        (Player player, _, _) = Create();

        Equip(player, Sword);

        Assert.Equal(2f + ApOver14 * 2f, F(player, UpdateFields.UnitFieldMindamage), Tol);   // 59.14, never the 0-5 fallback
        Assert.Equal(4f + ApOver14 * 2f, F(player, UpdateFields.UnitFieldMaxdamage), Tol);
        Assert.Equal(2000u, player.GetUInt32(UpdateFields.UnitFieldBaseattacktime));
        Assert.Equal(1, player.StatState.WeaponDamageCount(WeaponAttackType.BaseAttack));
    }

    [Fact]
    public void AFasterWeaponChangesTheAttackTimeAndTheApScaling()
    {
        (Player player, _, _) = Create();

        Equip(player, Dagger);

        Assert.Equal(1700u, player.GetUInt32(UpdateFields.UnitFieldBaseattacktime));
        Assert.Equal(3f + (400f / 14f * 1.7f), F(player, UpdateFields.UnitFieldMindamage), Tol);
        Assert.Equal(5f + (400f / 14f * 1.7f), F(player, UpdateFields.UnitFieldMaxdamage), Tol);
    }

    [Fact]
    public void RemovingTheWeaponRestoresTheDefaultSpeedAndReproducesTheReferenceZeroedEntry()
    {
        (Player player, _, _) = Create();
        Equip(player, Dagger);

        player.Inventory.SwapItem(InventorySlots.Bag0, InventorySlots.MainHand, InventorySlots.Bag0, InventorySlots.ItemStart + 7);

        Assert.Equal(2000u, player.GetUInt32(UpdateFields.UnitFieldBaseattacktime));
        // vmangos Player::_ApplyItemBonuses (Player.cpp:6954-6976) zeroes the entries the item used, so a hand that
        // held a weapon and lost it deals only the attack power part (the fist range is the never-armed state).
        Assert.Equal(ApOver14 * 2f, F(player, UpdateFields.UnitFieldMindamage), Tol);
        Assert.Equal(ApOver14 * 2f, F(player, UpdateFields.UnitFieldMaxdamage), Tol);
        Assert.Equal(new WeaponDamageEntry(0, 0, 0), player.StatState.WeaponDamage(WeaponAttackType.BaseAttack, 0));
    }

    [Fact]
    public void ATwoHanderWithTwoDamageEntriesKeepsBothAndTheFirstDrivesTheFields()
    {
        (Player player, _, _) = Create();

        Equip(player, Greataxe);

        PlayerStatState state = player.StatState;
        Assert.Equal(2, state.WeaponDamageCount(WeaponAttackType.BaseAttack));
        Assert.Equal(new WeaponDamageEntry(50, 90, 0), state.WeaponDamage(WeaponAttackType.BaseAttack, 0));
        Assert.Equal(new WeaponDamageEntry(5, 8, 2), state.WeaponDamage(WeaponAttackType.BaseAttack, 1));
        Assert.Equal(3300u, player.GetUInt32(UpdateFields.UnitFieldBaseattacktime));
        Assert.Equal(50f + (400f / 14f * 3.3f), F(player, UpdateFields.UnitFieldMindamage), Tol);
        Assert.Equal(90f + (400f / 14f * 3.3f), F(player, UpdateFields.UnitFieldMaxdamage), Tol);

        player.Inventory.SwapItem(InventorySlots.Bag0, InventorySlots.MainHand, InventorySlots.Bag0, InventorySlots.ItemStart + 7);
        Assert.Equal(1, state.WeaponDamageCount(WeaponAttackType.BaseAttack));
    }

    [Fact]
    public void ARangedWeaponWritesTheRangedFieldsAndRangedCrit()
    {
        (Player player, _, _) = Create();

        Equip(player, Bow);

        Assert.Equal(2500u, player.GetUInt32(UpdateFields.UnitFieldRangedattacktime));
        Assert.Equal(20f + (130f / 14f * 2.5f), F(player, UpdateFields.UnitFieldMinrangeddamage), Tol);
        Assert.Equal(30f + (130f / 14f * 2.5f), F(player, UpdateFields.UnitFieldMaxrangeddamage), Tol);
        Assert.Equal(2.0f, F(player, UpdateFields.PlayerRangedCritPercentage), Tol);   // weapon skill is the level maximum
        Assert.Equal(1, player.StatState.WeaponDamageCount(WeaponAttackType.RangedAttack));
    }

    [Fact]
    public void DualWield_NeedsTheAbility_ThenWritesTheOffHandFieldsAtHalfDamage()
    {
        (Player player, PlayerStatSystem system, FakeSession session) = Create();
        Equip(player, Sword);
        session.Clear();

        Item off = ItemTestData.Give(player.Inventory, OffhandSword);
        player.Inventory.SwapItem(off.BagSlot, off.Slot, InventorySlots.Bag0, InventorySlots.OffHand);
        // Without Dual Wield a one-hand weapon is not an allowed off-hand item at all (vmangos ItemPrototype::GetAllowedEquipSlots).
        Assert.Equal([InventoryResult.ItemCantBeEquipped], ItemTestData.EquipErrors(session));
        Assert.Null(player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.OffHand));
        Assert.False(system.HasOffhandWeapon(player));

        player.StatState.SetCanDualWield(true);
        player.Inventory.SwapItem(off.BagSlot, off.Slot, InventorySlots.Bag0, InventorySlots.OffHand);

        Assert.True(system.HasOffhandWeapon(player));
        Assert.Equal(1500u, player.GetUInt32(UpdateFields.UnitFieldBaseattacktime + 1));
        Assert.Equal((2f + (400f / 14f * 1.5f)) * 0.5f, F(player, UpdateFields.UnitFieldMinoffhanddamage), Tol);
        Assert.Equal((3f + (400f / 14f * 1.5f)) * 0.5f, F(player, UpdateFields.UnitFieldMaxoffhanddamage), Tol);
        Assert.Equal(2000u, player.GetUInt32(UpdateFields.UnitFieldBaseattacktime));

        Assert.Null(system.HasOffhandWeapon(new CombatTestUnit()));
    }

    [Fact]
    public void ABrokenWeaponContributesNothing()
    {
        (Player player, _, _) = Create();
        Item sword = ItemTestData.Give(player.Inventory, Sword);
        sword.Durability = 0;

        player.Inventory.AutoEquipItem(sword.BagSlot, sword.Slot);

        Assert.Equal(1f + ApOver14 * 2f, F(player, UpdateFields.UnitFieldMindamage), Tol);
        Assert.Equal(2f + ApOver14 * 2f, F(player, UpdateFields.UnitFieldMaxdamage), Tol);
    }

    [Fact]
    public void AgilityFromAnItemRaisesArmorCritAndDodge_AndRemovingItRestoresThem()
    {
        (Player player, _, _) = Create();
        Item ring = Equip(player, AgilityRing);

        Assert.Equal(90u, player.GetUInt32(UpdateFields.UnitFieldStat0 + 1));
        Assert.Equal(180u, player.GetUInt32(UpdateFields.UnitFieldResistances));
        Assert.Equal(2.25f, F(player, UpdateFields.PlayerCritPercentage), Tol);
        Assert.Equal(4.5f, F(player, UpdateFields.PlayerDodgePercentage), Tol);
        Assert.Equal(130, player.GetInt32(UpdateFields.UnitFieldRangedAttackPower) - 10);   // 60 + 90 - 10

        player.Inventory.DestroyItem(ring.BagSlot, ring.Slot);

        Assert.Equal(160u, player.GetUInt32(UpdateFields.UnitFieldResistances));
        Assert.Equal(2.0f, F(player, UpdateFields.PlayerCritPercentage), Tol);
        Assert.Equal(4.0f, F(player, UpdateFields.PlayerDodgePercentage), Tol);
    }

    [Fact]
    public void ItemArmorAndDynamicArmorAddUp()
    {
        (Player player, _, _) = Create();

        Equip(player, Shield);

        Assert.Equal(165u, player.GetUInt32(UpdateFields.UnitFieldResistances));
    }

    [Fact]
    public void BlockAbilityShieldAndBlockValue()
    {
        (Player player, PlayerStatSystem system, _) = Create();
        player.SetByte(UpdateFields.UnitFieldBytes2, 0, 1);

        Assert.False(system.PlayerCanBlock(player));
        Equip(player, Shield);
        Assert.False(system.PlayerCanBlock(player));                       // no Block ability yet
        Assert.Equal(25u, system.ShieldBlockValue(player));                // 20 + 120 / 20 - 1

        player.StatState.SetCanBlock(true);

        Assert.True(system.PlayerCanBlock(player));
        Assert.Equal(5f, F(player, UpdateFields.PlayerBlockPercentage), Tol);

        player.Inventory.SwapItem(InventorySlots.Bag0, InventorySlots.OffHand, InventorySlots.Bag0, InventorySlots.ItemStart + 7);
        Assert.False(system.PlayerCanBlock(player));
        Assert.Equal(5u, system.ShieldBlockValue(player));                 // 120 / 20 - 1 without the shield
    }

    [Fact]
    public void ParryAbilityNeedsAWeapon()
    {
        (Player player, PlayerStatSystem system, _) = Create();

        player.StatState.SetCanParry(true);
        Assert.Equal(5f, F(player, UpdateFields.PlayerParryPercentage), Tol);
        Assert.False(system.PlayerCanParry(player));                       // nothing to parry with

        Equip(player, Sword);

        Assert.True(system.PlayerCanParry(player));
        Assert.Equal(5f, F(player, UpdateFields.PlayerParryPercentage), Tol);
    }

    [Fact]
    public void AClassWithoutARateTableUsesRateOneLikeTheReference()
    {
        (Player player, _, _) = Create(rates: new AgilityRates(new Dictionary<Class, AgilityRateTable>(), new Dictionary<Class, AgilityRateTable>()));

        // ObjectMgr::GetPlayerCritPerAgility returns 1 for a class without a table (ObjectMgr.cpp:5334-5338).
        Assert.Equal(80f, F(player, UpdateFields.PlayerCritPercentage), Tol);
        Assert.Equal(80f, F(player, UpdateFields.PlayerDodgePercentage), Tol);
    }

    [Fact]
    public void WithoutAnyRateDataTheAgilityTermsAreLeftOutRatherThanGuessed()
    {
        (Player player, _, _) = Create(noRates: true);

        Assert.Equal(0f, F(player, UpdateFields.PlayerCritPercentage), Tol);   // class base 0 + no agility term + skill term 0
        Assert.Equal(0f, F(player, UpdateFields.PlayerDodgePercentage), Tol);
        Assert.Equal(160u, player.GetUInt32(UpdateFields.UnitFieldResistances));   // armor does not need rates
    }
    [Fact]
    public void UpdatingTwiceChangesNothing()
    {
        (Player player, PlayerStatSystem system, _) = Create();
        Equip(player, Sword);
        Equip(player, AgilityRing);
        player.StatState.SetCanParry(true);
        int[] fields =
        [
            UpdateFields.UnitFieldAttackPower, UpdateFields.UnitFieldRangedAttackPower, UpdateFields.UnitFieldResistances,
            UpdateFields.UnitFieldMindamage, UpdateFields.UnitFieldMaxdamage, UpdateFields.UnitFieldMinrangeddamage,
            UpdateFields.UnitFieldBaseattacktime, UpdateFields.PlayerCritPercentage, UpdateFields.PlayerDodgePercentage,
            UpdateFields.PlayerParryPercentage, UpdateFields.PlayerBlockPercentage,
        ];
        uint[] before = [.. fields.Select(player.GetUInt32)];

        system.UpdateAll(player);
        system.UpdateAll(player);

        Assert.Equal(before, fields.Select(player.GetUInt32));
    }

    [Fact]
    public void AttachingAfterTheItemsWereLoadedRebuildsTheWeaponState()
    {
        (Player player, PlayerStatSystem system, _) = Create(attach: false);
        Equip(player, Greataxe);

        system.Attach(player);

        Assert.Equal(2, player.StatState.WeaponDamageCount(WeaponAttackType.BaseAttack));
        Assert.Equal(3300u, player.GetUInt32(UpdateFields.UnitFieldBaseattacktime));
        Assert.Equal(50f + (400f / 14f * 3.3f), F(player, UpdateFields.UnitFieldMindamage), Tol);
    }

    [Fact]
    public void TheMeleeSwingRollsTheEquippedWeaponNotTheZeroToFiveFallback()
    {
        (WorldRuntime world, Map map, ScriptedRandom random, _) = CombatTestKit.CreateWorld();
        random.DefaultFraction = 0.5f;
        Player player = CombatTestKit.AddPlayer(world, 20, 0, 0, new FakeSession(20));
        player.Inventory.Templates = s_store;
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        for (int i = 0; i < 5; i++)
        {
            player.SetUInt32(UpdateFields.UnitFieldStat0 + i, new uint[] { 120, 80, 100, 20, 30 }[i]);
        }

        var system = new PlayerStatSystem(Rates());
        system.Attach(player);
        map.Combat.Stats = system;
        Equip(player, Sword);

        float roll = map.Combat.CalculateDamage(player, WeaponAttackType.BaseAttack);

        Assert.Equal(3f + (ApOver14 * 2f), roll, Tol);                    // mid of 59.14 - 61.14
    }

    [Fact]
    public void TheHitTableSeesTheParryBlockAndDualWieldAnswersOfTheStatSource()
    {
        (WorldRuntime world, Map map, _, _) = CombatTestKit.CreateWorld();
        Player player = CombatTestKit.AddPlayer(world, 21, 0, 0, new FakeSession(21));
        player.Inventory.Templates = s_store;
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        player.SetByte(UpdateFields.UnitFieldBytes2, 0, 1);
        var system = new PlayerStatSystem(Rates());
        system.Attach(player);
        map.Combat.Stats = system;
        player.StatState.SetCanParry(true);
        player.StatState.SetCanBlock(true);
        player.StatState.SetCanDualWield(true);
        Equip(player, Sword);
        Equip(player, Shield);
        var mob = new CombatTestUnit(level: 60);
        mob.Spawn(map, 1, 0);

        MeleeRollInput asVictim = map.Combat.BuildRollInput(mob, player, WeaponAttackType.BaseAttack);
        Assert.Equal(5f, asVictim.ParryChance, Tol);
        Assert.Equal(5f, asVictim.BlockChance, Tol);
        Assert.False(asVictim.DualWield);

        Item off = ItemTestData.Give(player.Inventory, OffhandSword);
        player.Inventory.DestroyItem(InventorySlots.Bag0, InventorySlots.OffHand);
        player.Inventory.SwapItem(off.BagSlot, off.Slot, InventorySlots.Bag0, InventorySlots.OffHand);
        MeleeRollInput asAttacker = map.Combat.BuildRollInput(player, mob, WeaponAttackType.OffAttack);
        Assert.True(asAttacker.DualWield);
        Assert.Equal(player.Level * 5, asAttacker.AttackerWeaponSkill);        // an off-hand weapon is not skill 0
    }
}

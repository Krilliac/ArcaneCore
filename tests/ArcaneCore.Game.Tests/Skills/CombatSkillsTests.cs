using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Skills;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Skills;
using Xunit;
using static ArcaneCore.Game.Tests.ItemTestData;

namespace ArcaneCore.Game.Tests.Skills;

/// <summary>
/// The weapon and defense skills the hit table rolls with, the off-hand/parry/block predicates and the skill-ups
/// a resolved melee swing earns (vmangos SpellCaster::GetWeaponSkillValue, Unit::ProcSkillsAndReactives and
/// Player::UpdateCombatSkills).
/// </summary>
public sealed class CombatSkillsTests
{
    private sealed class OffhandStatSource : ICombatStatSource
    {
        public bool? HasOffhandWeapon(Unit unit) => true;
        public bool? PlayerCanParry(Player player) => null;
        public bool? PlayerCanBlock(Player player) => null;
        public uint? ShieldBlockValue(Unit unit) => null;
    }

    private const uint OneHandSword = 91100;   // weapon 2/7, inventory type 13 (one hand: main or off hand)
    private const uint Greatshield = 91101;    // armor 4/6 with a block value

    private static readonly ItemTemplateStore Store = new(
        [
            .. ItemTestData.Templates,
            new ItemTemplate { Entry = OneHandSword, Class = 2, SubClass = 7, Name = "Test Sword", DisplayId = 1, InventoryType = 13, Delay = 2000, MaxDurability = 30, Damages = [new ItemDamage(2, 4, 0)] },
            new ItemTemplate { Entry = Greatshield, Class = 4, SubClass = 6, Name = "Test Shield", DisplayId = 2, InventoryType = 14, Armor = 20, Block = 30, MaxDurability = 40 },
        ],
        StartingItems);

    private sealed class Rig
    {
        public Rig(byte level = 10)
        {
            (World, Map, Random, Hooks) = CombatTestKit.CreateWorld();
            Session = new FakeSession(1);
            Player = CombatTestKit.AddPlayer(World, 1, 0, 0, Session, level: level);
            Player.Inventory.Templates = Store;
            Player.Inventory.GuidAllocator = new ItemGuidAllocator();
            Player.Inventory.Load([]);
            SkillRandom = new ScriptedSkillRandom();
            Skills = new PlayerSkills(Player, SkillTestKit.Catalog(), new SkillOptions(), new FakeSkillSpellHost(), SkillRandom);
            Player.AttachSkills(Skills);
            Player.Inventory.Requirements = new PlayerItemRequirements(DefaultItemRequirements.Instance);
            Creature = new CombatTestUnit(level: 10);
            Creature.Spawn(Map, 1, 0);
        }

        public WorldRuntime World { get; }

        public Map Map { get; }

        public ScriptedRandom Random { get; }

        public TestCombatHooks Hooks { get; }

        public FakeSession Session { get; }

        public Player Player { get; }

        public PlayerSkills Skills { get; }

        public ScriptedSkillRandom SkillRandom { get; }

        public CombatTestUnit Creature { get; }

        public Item Equip(uint entry, byte slot)
        {
            Item item = Give(Player.Inventory, entry);
            Player.Inventory.SwapItem(item.BagSlot, item.Slot, InventorySlots.Bag0, slot);
            Assert.Same(item, Player.Inventory.GetItem(InventorySlots.Bag0, slot));
            return item;
        }
    }

    [Fact]
    public void WeaponSkill_FollowsTheEquippedWeaponsProficiency_UnarmedForAnEmptyMainHand_ZeroForAnEmptyOffHand()
    {
        var rig = new Rig();
        rig.Skills.Set(SkillIds.Swords, 20, 50);
        rig.Skills.Set(SkillIds.Unarmed, 35, 50);
        rig.Skills.ModifyBonus(SkillIds.Swords, 3, permanent: true);

        Assert.Equal(35, rig.Hooks.GetWeaponSkill(rig.Player, WeaponAttackType.BaseAttack, rig.Creature));   // empty main hand: unarmed
        Assert.Equal(0, rig.Hooks.GetWeaponSkill(rig.Player, WeaponAttackType.OffAttack, rig.Creature));     // empty off hand
        Assert.Equal(0, rig.Hooks.GetWeaponSkill(rig.Player, WeaponAttackType.RangedAttack, rig.Creature));

        rig.Equip(OneHandSword, InventorySlots.MainHand);
        Assert.Equal(23, rig.Hooks.GetWeaponSkill(rig.Player, WeaponAttackType.BaseAttack, rig.Creature));   // 20 + 3 permanent bonus
        rig.Skills.ModifyBonus(SkillIds.Swords, 4);                                                           // a temporary bonus counts too
        Assert.Equal(27, rig.Hooks.GetWeaponSkill(rig.Player, WeaponAttackType.BaseAttack, rig.Creature));
    }

    [Fact]
    public void WeaponSkill_IsZeroForAWeaponTheClassCannotUse_AndDisarmedHandsUseUnarmed()
    {
        var rig = new Rig();
        rig.Skills.Set(SkillIds.Unarmed, 35, 50);
        rig.Skills.Set(SkillIds.Swords, 20, 50);
        rig.Equip(OneHandSword, InventorySlots.MainHand);
        rig.Skills.Set(SkillIds.Swords, 0, 0);                     // the skill is gone (unlearned): the item still counts as a weapon, skill 0
        Assert.Equal(0, rig.Hooks.GetWeaponSkill(rig.Player, WeaponAttackType.BaseAttack, rig.Creature));

        rig.Player.UnitFlags |= UnitFlags.Disarmed;                // a disarmed main hand is not usable: unarmed
        Assert.Equal(35, rig.Hooks.GetWeaponSkill(rig.Player, WeaponAttackType.BaseAttack, rig.Creature));
    }

    [Fact]
    public void WeaponSkill_ABrokenWeaponIsNotUsed()
    {
        var rig = new Rig();
        rig.Skills.Set(SkillIds.Swords, 20, 50);
        rig.Skills.Set(SkillIds.Unarmed, 35, 50);
        Item sword = rig.Equip(OneHandSword, InventorySlots.MainHand);
        Assert.Equal(20, rig.Hooks.GetWeaponSkill(rig.Player, WeaponAttackType.BaseAttack, rig.Creature));
        sword.Durability = 0;
        Assert.Equal(35, rig.Hooks.GetWeaponSkill(rig.Player, WeaponAttackType.BaseAttack, rig.Creature));
    }

    [Fact]
    public void DefenseSkill_UsesTheCurrentValueAgainstCreatures_AndTheMaximumAgainstPlayers()
    {
        var rig = new Rig();
        rig.Skills.Set(SkillIds.Defense, 12, 50);
        rig.Skills.ModifyBonus(SkillIds.Defense, 2);

        Assert.Equal(14, rig.Hooks.GetDefenseSkill(rig.Player, rig.Creature));
        var other = TestWorld.CreatePlayer(2, 5, 5, new FakeSession(2));
        Assert.Equal(52, rig.Hooks.GetDefenseSkill(rig.Player, other));    // max 50 + the temporary bonus 2

        // A creature (no skills) keeps level x 5.
        Assert.Equal(50, rig.Hooks.GetDefenseSkill(rig.Creature, rig.Player));
    }

    [Fact]
    public void WithoutAttachedSkills_TheLevelBasedDefaultsApply()
    {
        (WorldRuntime world, Map map, _, TestCombatHooks hooks) = CombatTestKit.CreateWorld();
        Player player = CombatTestKit.AddPlayer(world, 3, 0, 0, new FakeSession(3), level: 10);
        var creature = new CombatTestUnit(level: 10);
        creature.Spawn(map, 1, 0);
        Assert.Equal(50, hooks.GetWeaponSkill(player, WeaponAttackType.BaseAttack, creature));
        Assert.Equal(50, hooks.GetDefenseSkill(player, creature));
        Assert.False(hooks.PlayerCanParry(player));
        Assert.False(hooks.PlayerCanBlock(player));
    }

    [Fact]
    public void OffhandParryAndBlock_NeedTheAbilityAndTheEquipment()
    {
        var rig = new Rig();
        var plain = new CombatHooks();
        rig.Skills.Set(SkillIds.Swords, 20, 50);                     // the equip rules need the weapon skill
        Assert.False(plain.HasOffhandWeapon(rig.Player));
        Assert.False(plain.PlayerCanParry(rig.Player));
        Assert.False(plain.PlayerCanBlock(rig.Player));

        // Parry: the ability plus a weapon in hand.
        rig.Skills.CanParry = true;
        Assert.False(plain.PlayerCanParry(rig.Player));
        Item main = rig.Equip(OneHandSword, InventorySlots.MainHand);
        Assert.True(plain.PlayerCanParry(rig.Player));
        main.Durability = 0;                                         // a broken weapon cannot parry
        Assert.False(plain.PlayerCanParry(rig.Player));

        // Dual wield: a weapon in the off hand (the equip rules need the Dual Wield ability).
        rig.Skills.CanDualWield = true;
        Item off = rig.Equip(OneHandSword, InventorySlots.OffHand);
        Assert.True(plain.HasOffhandWeapon(rig.Player));
        Assert.True(plain.PlayerCanParry(rig.Player));               // the off-hand weapon parries when the main hand is broken
        off.Durability = 0;
        Assert.False(plain.HasOffhandWeapon(rig.Player));

        // Block: the ability plus an unbroken shield with a block value.
        var shieldRig = new Rig();
        shieldRig.Skills.Set(SkillIds.Shield, 1, 1);
        Assert.False(plain.PlayerCanBlock(shieldRig.Player));
        Item shield = shieldRig.Equip(Greatshield, InventorySlots.OffHand);
        Assert.False(plain.PlayerCanBlock(shieldRig.Player));        // no Block ability yet
        shieldRig.Skills.CanBlock = true;
        Assert.True(plain.PlayerCanBlock(shieldRig.Player));
        shield.Durability = 0;
        Assert.False(plain.PlayerCanBlock(shieldRig.Player));
    }

    [Fact]
    public void TheHitTableRollsWithTheRealWeaponAndDefenseSkill()
    {
        var rig = new Rig();
        rig.Skills.Set(SkillIds.Swords, 20, 50);
        rig.Skills.Set(SkillIds.Defense, 15, 50);
        rig.Equip(OneHandSword, InventorySlots.MainHand);

        MeleeRollInput outgoing = rig.Map.Combat.BuildRollInput(rig.Player, rig.Creature, WeaponAttackType.BaseAttack);
        Assert.Equal((20, 50), (outgoing.AttackerWeaponSkill, outgoing.AttackerMaxSkill));
        Assert.Equal(50, outgoing.VictimDefenseSkill);               // the creature's defense: level x 5

        MeleeRollInput incoming = rig.Map.Combat.BuildRollInput(rig.Creature, rig.Player, WeaponAttackType.BaseAttack);
        Assert.Equal(15, incoming.VictimDefenseSkill);               // the player's defense skill, not level x 5
    }

    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    [InlineData(50)]
    public void OffhandHitTableUsesLearnedSkillEvenWithTheStatsSource(int weaponSkill)
    {
        var rig = new Rig();
        rig.Skills.Set(SkillIds.Swords, (ushort)weaponSkill, 50);
        rig.Skills.CanDualWield = true;
        rig.Equip(OneHandSword, InventorySlots.OffHand);
        rig.Map.Combat.Stats = new OffhandStatSource();

        MeleeRollInput input = rig.Map.Combat.BuildRollInput(rig.Player, rig.Creature, WeaponAttackType.OffAttack);
        Assert.Equal(weaponSkill, input.AttackerWeaponSkill);
        Assert.Equal(50, input.AttackerMaxSkill);
        Assert.True(input.DualWield);
    }

    [Fact]
    public void ASwing_RaisesTheWeaponSkillOfTheAttacker_AndTheDefenseSkillOfTheVictim()
    {
        var rig = new Rig();
        rig.Skills.Set(SkillIds.Swords, 20, 50);
        rig.Skills.Set(SkillIds.Defense, 20, 50);
        rig.Equip(OneHandSword, InventorySlots.MainHand);

        rig.SkillRandom.Floats.Enqueue(0f);                                   // the weapon roll wins
        Assert.NotNull(rig.Map.Combat.AttackerStateUpdate(rig.Player, rig.Creature, WeaponAttackType.BaseAttack));
        Assert.Equal((ushort)21, rig.Skills.GetValuePure(SkillIds.Swords));
        Assert.Equal((ushort)20, rig.Skills.GetValuePure(SkillIds.Defense));

        rig.SkillRandom.Floats.Enqueue(0f);                                   // the defense roll wins
        Assert.NotNull(rig.Map.Combat.AttackerStateUpdate(rig.Creature, rig.Player, WeaponAttackType.BaseAttack));
        Assert.Equal((ushort)21, rig.Skills.GetValuePure(SkillIds.Defense));
        Assert.Equal((ushort)21, rig.Skills.GetValuePure(SkillIds.Swords));
    }

    [Fact]
    public void ALostRoll_RaisesNothing_AndTheChanceFollowsTheGoldenCurve()
    {
        var rig = new Rig();
        rig.Skills.Set(SkillIds.Swords, 48, 50);       // within 3 of the maximum: about 4.9 percent
        rig.Equip(OneHandSword, InventorySlots.MainHand);

        rig.SkillRandom.Floats.Enqueue(5.0f);
        rig.Map.Combat.AttackerStateUpdate(rig.Player, rig.Creature, WeaponAttackType.BaseAttack);
        Assert.Equal((ushort)48, rig.Skills.GetValuePure(SkillIds.Swords));

        rig.SkillRandom.Floats.Enqueue(4.8f);
        rig.Map.Combat.AttackerStateUpdate(rig.Player, rig.Creature, WeaponAttackType.BaseAttack);
        Assert.Equal((ushort)49, rig.Skills.GetValuePure(SkillIds.Swords));
    }

    [Fact]
    public void AnEvadedSwing_EarnsNoSkill()
    {
        var rig = new Rig();
        rig.Skills.Set(SkillIds.Swords, 20, 50);
        rig.Equip(OneHandSword, InventorySlots.MainHand);
        rig.Creature.IsInEvadeMode = true;

        rig.SkillRandom.Floats.Enqueue(0f);
        rig.Map.Combat.AttackerStateUpdate(rig.Player, rig.Creature, WeaponAttackType.BaseAttack);
        Assert.Equal((ushort)20, rig.Skills.GetValuePure(SkillIds.Swords));
        Assert.Single(rig.SkillRandom.Floats);                                // never rolled
    }

    [Fact]
    public void NoSkillFromPlayersOrWhileShapeshifted_AndADeadVictimDoesNotRollDefense()
    {
        var rig = new Rig();
        rig.Skills.Set(SkillIds.Swords, 20, 50);
        rig.Skills.Set(SkillIds.Defense, 20, 50);
        rig.Equip(OneHandSword, InventorySlots.MainHand);

        // Another player as the target: no weapon skill (PvP).
        var other = CombatTestKit.AddPlayer(rig.World, 2, 1, 0, new FakeSession(2), level: 10);
        rig.SkillRandom.Floats.Enqueue(0f);
        rig.Map.Combat.AttackerStateUpdate(rig.Player, other, WeaponAttackType.BaseAttack);
        Assert.Equal((ushort)20, rig.Skills.GetValuePure(SkillIds.Swords));

        // A shapeshifted attacker earns no weapon skill (UNIT_FIELD_BYTES_1 byte 2 is the form).
        rig.Player.SetByte(UpdateFields.UnitFieldBytes1, 2, 1);
        rig.Map.Combat.AttackerStateUpdate(rig.Player, rig.Creature, WeaponAttackType.BaseAttack);
        Assert.Equal((ushort)20, rig.Skills.GetValuePure(SkillIds.Swords));
        rig.Player.SetByte(UpdateFields.UnitFieldBytes1, 2, 0);
        Assert.Equal(2, rig.SkillRandom.Floats.Count + 1);                    // the PvP and form swings left the roll unconsumed

        // The swing that kills the player does not roll defense.
        rig.Player.Health = 1;
        rig.Creature.SetFloat(UpdateFields.UnitFieldMindamage, 500);
        rig.Creature.SetFloat(UpdateFields.UnitFieldMaxdamage, 500);
        rig.Map.Combat.AttackerStateUpdate(rig.Creature, rig.Player, WeaponAttackType.BaseAttack);
        Assert.False(rig.Player.IsAlive);
        Assert.Equal((ushort)20, rig.Skills.GetValuePure(SkillIds.Defense));
    }
}

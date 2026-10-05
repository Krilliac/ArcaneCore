using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Items;
using Xunit;

namespace ArcaneCore.Game.Tests.CombatMechanics;

public sealed class HitDurabilityTests
{
    [Fact]
    public void GuaranteedHitWear_UsesSelectedEquipmentSlotThroughInventoryPath()
    {
        (WorldRuntime world, Map map, ScriptedRandom random, _, Player attacker, _, Player victim, _) = CreatePair();
        using (world)
        {
            Item item = Equip(victim, InventorySlots.MainHand, 10);
            victim.Inventory.Options.DurabilityLossChanceDamage = 100;
            random.Ints.Enqueue(InventorySlots.MainHand);
            map.Combat.ApplyHitDurability(attacker, victim);
            Assert.Equal(9u, item.Durability);
        }
    }

    [Fact]
    public void PvpHitWear_RollsIndependentlyForVictimAndAttacker_AndZeroChanceDisablesBoth()
    {
        (WorldRuntime world, Map map, ScriptedRandom random, _, Player attacker, _, Player victim, _) = CreatePair();
        using (world)
        {
            Item a = Equip(attacker, InventorySlots.MainHand, 10);
            Item v = Equip(victim, InventorySlots.OffHand, 10);
            attacker.Inventory.Options.DurabilityLossChanceDamage = 100;
            victim.Inventory.Options.DurabilityLossChanceDamage = 100;
            random.Ints.Enqueue(InventorySlots.OffHand); // victim roll occurs first
            random.Ints.Enqueue(InventorySlots.MainHand); // attacker roll occurs second
            map.Combat.ApplyHitDurability(attacker, victim);
            Assert.Equal((9u, 9u), (a.Durability, v.Durability));
            attacker.Inventory.Options.DurabilityLossChanceDamage = 0;
            victim.Inventory.Options.DurabilityLossChanceDamage = 0;
            map.Combat.ApplyHitDurability(attacker, victim);
            Assert.Equal((9u, 9u), (a.Durability, v.Durability));
        }
    }

    [Fact]
    public void DealDamage_NonLethalProducerAppliesWear_EvenWhenDeathWearFlagIsFalse()
    {
        (WorldRuntime world, Map map, ScriptedRandom random, _, Player attacker, _, Player victim, _) = CreatePair();
        using (world)
        {
            Item item = Equip(victim, InventorySlots.MainHand, 10);
            victim.Inventory.Options.DurabilityLossChanceDamage = 100;
            random.Ints.Enqueue(InventorySlots.MainHand);
            map.Combat.DealDamage(attacker, victim, 1, direct: false, meleeDamage: false, durabilityLoss: false);
            Assert.Equal(9u, item.Durability);
        }
    }

    [Fact]
    public void DealDamage_LethalPathDoesNotAddHitWearBeyondDeathRules()
    {
        (WorldRuntime world, Map map, ScriptedRandom random, _, Player attacker, _, Player victim, _) = CreatePair();
        using (world)
        {
            Item item = Equip(victim, InventorySlots.MainHand, 100);
            victim.Inventory.Options.DurabilityLossChanceDamage = 100;
            victim.Health = 1;
            random.Ints.Enqueue(InventorySlots.MainHand);
            map.Combat.DealDamage(attacker, victim, 1);
            Assert.Equal(100u, item.Durability); // lethal path has no hit wear; PvP death has no 10% wear
        }
    }

    [Fact]
    public void DealDamage_ZeroDeliveryDoesNotRollHitWear()
    {
        (WorldRuntime world, Map map, ScriptedRandom random, _, Player attacker, _, Player victim, _) = CreatePair();
        using (world)
        {
            Item item = Equip(victim, InventorySlots.MainHand, 10);
            victim.Inventory.Options.DurabilityLossChanceDamage = 100;
            random.Ints.Enqueue(InventorySlots.MainHand);
            map.Combat.DealDamage(attacker, victim, 0);
            Assert.Equal(10u, item.Durability);
        }
    }

    [Fact]
    public void FractionalChance_UsesPercentBoundary()
    {
        (WorldRuntime world, Map map, ScriptedRandom random, _, Player attacker, _, Player victim, _) = CreatePair();
        using (world)
        {
            Item item = Equip(victim, InventorySlots.MainHand, 10);
            victim.Inventory.Options.DurabilityLossChanceDamage = 0.5;
            random.Floats.Enqueue(0.49f);
            random.Ints.Enqueue(InventorySlots.MainHand);
            map.Combat.DealDamage(attacker, victim, 1);
            Assert.Equal(9u, item.Durability);
            item.Durability = 10;
            random.Floats.Enqueue(0.5f);
            random.Ints.Enqueue(InventorySlots.MainHand);
            map.Combat.DealDamage(attacker, victim, 1);
            Assert.Equal(10u, item.Durability);
        }
    }

    private static Item Equip(Player player, byte slot, uint durability)
    {
        var template = new ItemTemplate { Entry = 980001, Name = "Hit durability test", Class = 2, SubClass = 7,
            InventoryType = slot == InventorySlots.OffHand ? 22u : 21u, MaxDurability = Math.Max(10u, durability) };
        player.Inventory.Templates = new ItemTemplateStore([template]);
        player.Inventory.Load([new InventoryItemData(0, slot, new ItemInstanceData { Guid = (uint)(player.Guid.Low + slot + 100), Entry = template.Entry, Durability = durability })]);
        return player.Inventory.GetItem(InventorySlots.Bag0, slot)!;
    }

    private static (WorldRuntime, Map, ScriptedRandom, TestCombatHooks, Player, FakeSession, Player, FakeSession) CreatePair()
    {
        var (world, map, random, hooks) = CombatTestKit.CreateWorld();
        var sa = new FakeSession(1); var sv = new FakeSession(2);
        Player attacker = CombatTestKit.AddPlayer(world, 1, 0, 0, sa);
        Player victim = CombatTestKit.AddPlayer(world, 2, 1, 0, sv);
        return (world, map, random, hooks, attacker, sa, victim, sv);
    }
}

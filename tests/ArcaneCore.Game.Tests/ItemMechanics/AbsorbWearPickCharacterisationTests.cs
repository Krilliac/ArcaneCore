using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Tests.CombatCore;
using ArcaneCore.Game.Tests.SpellRules;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Items;
using Xunit;
using ScriptedRandom = ArcaneCore.Game.Tests.CombatCore.ScriptedRandom;
using static ArcaneCore.Game.Tests.ItemTestData;

namespace ArcaneCore.Game.Tests.ItemMechanics;

/// <summary>
/// Characterises which worn armor piece the absorb wear (<c>Items:DurabilityLossChanceAbsorb</c>) takes and how many random draws it spends:
/// the pool is the worn armor with durability in ascending slot order, the pick is exactly one <c>Next(0, count - 1)</c> after the chance
/// roll, and nothing is drawn when no armor is worn. DefenseDurabilityTests only asserts that one of the two pieces lost a point; this pins the
/// index, so a shared pick helper in MapCombat.Durability.cs cannot drift in pool order or draw order.
/// </summary>
public sealed class AbsorbWearPickCharacterisationTests
{
    private const uint Durable = 1000;
    private const uint SwordEntry = 93_420;
    private const uint ShieldEntry = 93_421;
    private const uint ChestEntry = 93_422;
    private const uint AbsorbAura = 93_430;

    private static readonly ItemTemplateStore Store = new(
        [
            .. Templates,
            new ItemTemplate { Entry = SwordEntry, Class = 2, SubClass = 7, Name = "Pick Sword", DisplayId = 1, InventoryType = 13, Delay = 2000, MaxDurability = Durable, Damages = [new ItemDamage(1, 3, 0)] },
            new ItemTemplate { Entry = ShieldEntry, Class = 4, SubClass = 6, Name = "Pick Shield", DisplayId = 1, InventoryType = 14, Armor = 5, Block = 1, MaxDurability = Durable },
            new ItemTemplate { Entry = ChestEntry, Class = 4, SubClass = 1, Name = "Pick Chest", DisplayId = 1, InventoryType = 5, Armor = 5, MaxDurability = Durable },
        ], []);

    private sealed class NoDefenseStats : ICombatStatSource
    {
        public bool? HasOffhandWeapon(Unit unit) => null;
        public bool? PlayerCanParry(Player player) => false;
        public bool? PlayerCanBlock(Player player) => false;
        public uint? ShieldBlockValue(Unit unit) => 10;
    }

    private sealed record Scene(SpellTestKit Kit, MapCombat Combat, Player Attacker, Player Victim, ScriptedRandom Random) : IDisposable
    {
        public void Dispose() => Kit.Dispose();

        public uint Lost(byte slot) => Durable - Victim.Inventory.GetItem(InventorySlots.Bag0, slot)!.Durability;
    }

    private static Scene Create(params (byte Slot, uint Entry)[] worn)
    {
        var kit = new SpellTestKit(RuleTestKit.Grant(AbsorbAura, AuraType.SchoolAbsorb, 1000, (int)SpellSchoolMasks.Of(SpellSchool.Normal)));
        (Player attacker, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2);
        MapCombat combat = attacker.Map!.Combat;
        combat.SpellMitigation = kit.System;
        combat.Stats = new NoDefenseStats();
        attacker.SetFloat(UpdateFields.UnitFieldMindamage, 50);
        attacker.SetFloat(UpdateFields.UnitFieldMaxdamage, 50);
        victim.Health = victim.MaxHealth = 100_000;
        victim.SetFloat(UpdateFields.PlayerParryPercentage, 0f);
        victim.SetFloat(UpdateFields.PlayerBlockPercentage, 0f);
        victim.SetFloat(UpdateFields.PlayerDodgePercentage, 0f);
        victim.SetByte(UpdateFields.UnitFieldBytes2, 0, 1);
        victim.Relocate(2, 0, victim.Z, MathF.PI, 0);
        Wire(victim.Inventory);
        victim.Inventory.Templates = Store;
        victim.Inventory.Load([.. worn.Select(w => Row(w.Slot, w.Entry))]);
        ItemMechanicsOptions options = victim.Inventory.Options;
        options.DurabilityLossChanceDamage = 0;
        options.DurabilityLossChanceParry = 0;
        options.DurabilityLossChanceBlock = 0;
        options.DurabilityLossChanceAbsorb = 100;
        var random = new ScriptedRandom();
        combat.Random = random;
        return new Scene(kit, combat, attacker, victim, random);
    }

    private static InventoryItemData Row(byte slot, uint entry)
        => new(0, slot, new ItemInstanceData { Guid = 7000u + slot, Entry = entry, Durability = Durable, Charges = [0, 0, 0, 0, 0], Enchantments = new uint[21] });

    private static MeleeDamageInfo AbsorbedSwing(Scene s, params int[] draws)
    {
        RuleTestKit.Apply(s.Kit, s.Victim, AbsorbAura);
        foreach (int draw in draws)
        {
            s.Random.Ints.Enqueue(draw);
        }

        MeleeDamageInfo info = s.Combat.AttackerStateUpdate(s.Attacker, s.Victim, WeaponAttackType.BaseAttack)!;
        Assert.Equal(MeleeHitOutcome.Normal, info.Outcome);
        Assert.True(info.Absorbed > 0);
        return info;
    }

    [Fact]
    public void Pick0_WearsTheLowestSlotArmor_TheChest_NotTheShieldOrTheSword()
    {
        using Scene s = Create((InventorySlots.MainHand, SwordEntry), (InventorySlots.OffHand, ShieldEntry), (InventorySlots.Chest, ChestEntry));
        AbsorbedSwing(s, 9999, 0); // the hit-table roll (a normal hit), then the slot pick

        Assert.Equal(1u, s.Lost(InventorySlots.Chest));
        Assert.Equal(0u, s.Lost(InventorySlots.OffHand));
        Assert.Equal(0u, s.Lost(InventorySlots.MainHand));
    }

    [Fact]
    public void Pick1_WearsTheNextArmorInSlotOrder_TheShield()
    {
        using Scene s = Create((InventorySlots.MainHand, SwordEntry), (InventorySlots.OffHand, ShieldEntry), (InventorySlots.Chest, ChestEntry));
        AbsorbedSwing(s, 9999, 1);

        Assert.Equal(0u, s.Lost(InventorySlots.Chest));
        Assert.Equal(1u, s.Lost(InventorySlots.OffHand));
        Assert.Equal(0u, s.Lost(InventorySlots.MainHand));
    }

    [Fact]
    public void APickAboveTheCount_IsBoundedToTheLastArmorPiece()
    {
        using Scene s = Create((InventorySlots.OffHand, ShieldEntry), (InventorySlots.Chest, ChestEntry));
        AbsorbedSwing(s, 9999, 7);

        Assert.Equal(0u, s.Lost(InventorySlots.Chest));
        Assert.Equal(1u, s.Lost(InventorySlots.OffHand));
    }

    [Fact]
    public void OnePieceWorn_TakesTheWholeRoll_AndSpendsExactlyOneSlotDraw()
    {
        using Scene s = Create((InventorySlots.Chest, ChestEntry));
        AbsorbedSwing(s, 9999, 0, 4242);

        Assert.Equal(1u, s.Lost(InventorySlots.Chest));
        Assert.Equal([4242], s.Random.Ints.ToArray()); // one hit-table draw and one slot draw were consumed; the third stays queued
    }

    [Fact]
    public void NoArmorWorn_WearsNothing_AndSpendsNoSlotDraw()
    {
        using Scene s = Create((InventorySlots.MainHand, SwordEntry));
        AbsorbedSwing(s, 9999, 4242);

        Assert.Equal(0u, s.Lost(InventorySlots.MainHand));
        Assert.Equal([4242], s.Random.Ints.ToArray()); // only the hit-table draw was consumed
    }
}

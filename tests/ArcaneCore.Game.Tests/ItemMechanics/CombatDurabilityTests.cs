using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Stats;
using ArcaneCore.Kernel.Items;
using Xunit;
using static ArcaneCore.Game.Tests.ItemTestData;

namespace ArcaneCore.Game.Tests.ItemMechanics;

/// <summary>
/// Durability loss from combat damage (vmangos Unit::DealDamage, Unit.cpp:1093-1108, with Player::DurabilityPointLossForEquipSlot,
/// Player.cpp:4896): a surviving player victim wears one uniformly chosen equipment slot, a connecting swing wears the weapon of
/// its hand, both at <c>Items:DurabilityLossChanceDamage</c> percent; a broken item stops contributing; the death penalty is the
/// existing one (<see cref="Death.PlayerDeathDurabilityTests"/>).
/// </summary>
public sealed class CombatDurabilityTests
{
    private const uint Durable = 10_000;
    private const uint SwordEntry = 93_100;
    private const uint ArmorEntryBase = 93_200; // + slot
    private const uint RingEntry = 93_300;      // no durability at all
    private const uint BigHealth = 1_000_000_000;

    /// <summary>The equipment slots that hold an item with durability on the victim of the armor tests (the other seven hold none).</summary>
    private static readonly byte[] DurableSlots =
        [InventorySlots.Head, InventorySlots.Shoulders, InventorySlots.Chest, InventorySlots.Waist, InventorySlots.Legs, InventorySlots.Feet,
            InventorySlots.Wrists, InventorySlots.Hands, InventorySlots.Back, InventorySlots.MainHand, InventorySlots.OffHand, InventorySlots.Ranged];

    private static readonly ItemTemplateStore Store = new(
        [
            .. Templates,
            new ItemTemplate { Entry = SwordEntry, Class = 2, SubClass = 7, Name = "Durable Sword", DisplayId = 1, InventoryType = 13, Delay = 2000, MaxDurability = Durable, Damages = [new ItemDamage(1, 3, 0)] },
            .. DurableSlots.Select(slot => new ItemTemplate { Entry = ArmorEntryBase + slot, Class = slot == InventorySlots.Ranged ? 2u : 4u, SubClass = slot switch { InventorySlots.Ranged => 2u, InventorySlots.OffHand => 6u, _ => 1u }, Name = $"Durable {slot}", DisplayId = 1, InventoryType = InventoryTypeFor(slot), MaxDurability = Durable }),
            new ItemTemplate { Entry = RingEntry, Class = 4, SubClass = 0, Name = "Plain Ring", DisplayId = 1, InventoryType = 11 },
        ], []);

    /// <summary>The INVTYPE that fits <paramref name="slot"/>, so the load accepts the item there (head, shoulders, chest, waist, legs, feet, wrists, hands, back, shield, bow).</summary>
    private static uint InventoryTypeFor(byte slot) => slot switch
    {
        InventorySlots.Head => 1,
        InventorySlots.Shoulders => 3,
        InventorySlots.Chest => 5,
        InventorySlots.Waist => 6,
        InventorySlots.Legs => 7,
        InventorySlots.Feet => 8,
        InventorySlots.Wrists => 9,
        InventorySlots.Hands => 10,
        InventorySlots.Back => 16,
        InventorySlots.OffHand => 14,
        InventorySlots.Ranged => 15,
        _ => 21,
    };

    /// <summary>
    /// A seeded source for the statistical tests: the hit-table roll (range 0..9999) is always a plain hit, everything else
    /// (damage, the percent roll, the slot pick) comes from one <see cref="Random"/> with a fixed seed.
    /// </summary>
    private sealed class SeededRandom(int seed) : ICombatRandom
    {
        private readonly Random _random = new(seed);

        public int Next(int minInclusive, int maxInclusive)
            => minInclusive == 0 && maxInclusive == CombatConstants.RollRange - 1 ? maxInclusive : _random.Next(minInclusive, maxInclusive + 1);

        public float NextFloat(float min, float max) => min + ((float)_random.NextDouble() * (max - min));
    }

    private sealed record Scene(WorldRuntime World, Map Map, ScriptedRandom Scripted, Player Attacker, FakeSession AttackerSession, Player Victim, FakeSession VictimSession);

    private static Scene Create(bool seeded = false, int seed = 1)
    {
        (WorldRuntime world, Map map, ScriptedRandom scripted, _) = CombatTestKit.CreateWorld();
        if (seeded)
        {
            map.Combat.Random = new SeededRandom(seed);
        }

        var attackerSession = new FakeSession(1);
        var victimSession = new FakeSession(2);
        Player attacker = CombatTestKit.AddPlayer(world, 1, 0, 0, attackerSession);
        Player victim = CombatTestKit.AddPlayer(world, 2, 2, 0, victimSession, Race.Orc);
        foreach (Player p in new[] { attacker, victim })
        {
            Wire(p.Inventory);
            p.Inventory.Templates = Store;
            p.Inventory.Load([]);
        }

        attacker.SetFloat(UpdateFields.UnitFieldMindamage, 1);
        attacker.SetFloat(UpdateFields.UnitFieldMaxdamage, 1);
        attacker.SetFloat(UpdateFields.UnitFieldMinoffhanddamage, 1);
        attacker.SetFloat(UpdateFields.UnitFieldMaxoffhanddamage, 1);
        victim.MaxHealth = BigHealth;
        victim.Health = BigHealth;
        return new Scene(world, map, scripted, attacker, attackerSession, victim, victimSession);
    }

    private static InventoryItemData Row(byte slot, uint entry, uint durability = Durable)
        => new(0, slot, new ItemInstanceData { Guid = 5000u + slot, Entry = entry, Durability = durability, Charges = [0, 0, 0, 0, 0], Enchantments = new uint[21] });

    /// <summary>A durable item in every durable slot and a ring without durability in the finger slot.</summary>
    private static void EquipArmorEverywhere(Player player)
        => player.Inventory.Load([.. DurableSlots.Select(slot => Row(slot, slot == InventorySlots.MainHand ? SwordEntry : ArmorEntryBase + slot)), Row(InventorySlots.Finger1, RingEntry, 0)]);

    private static Item At(Player player, byte slot) => player.Inventory.GetItem(InventorySlots.Bag0, slot)!;

    private static void SetChance(Player player, double percent) => player.Inventory.Options.DurabilityLossChanceDamage = percent;

    private static void Swing(Scene s, WeaponAttackType type = WeaponAttackType.BaseAttack, int times = 1)
    {
        for (int i = 0; i < times; i++)
        {
            Assert.NotNull(s.Map.Combat.AttackerStateUpdate(s.Attacker, s.Victim, type));
            if (i % 500 == 499)
            {
                s.AttackerSession.Clear();
                s.VictimSession.Clear();
            }
        }
    }

    private static uint Lost(Player player, byte slot) => Durable - At(player, slot).Durability;

    // --- hit done: the weapon of the swinging hand --------------------------------------------------

    [Fact]
    public void HitsDone_WearTheMainHandWeapon_AtTheConfiguredChance()
    {
        Scene s = Create(seeded: true);
        using (s.World)
        {
            s.Attacker.Inventory.Load([Row(InventorySlots.MainHand, SwordEntry), Row(InventorySlots.OffHand, SwordEntry)]);
            SetChance(s.Attacker, 10);

            Swing(s, WeaponAttackType.BaseAttack, 5000);

            Assert.Equal(BigHealth - 5000, s.Victim.Health); // every swing landed for 1
            Assert.InRange(Lost(s.Attacker, InventorySlots.MainHand), 400u, 600u); // 10% of 5000 = 500, sd 21
            Assert.Equal(0u, Lost(s.Attacker, InventorySlots.OffHand));
        }
    }

    [Fact]
    public void HitsDone_AtTheDefaultHalfPercent_WearTheWeaponAboutOnceInTwoHundredHits()
    {
        Scene s = Create(seeded: true, seed: 7);
        using (s.World)
        {
            s.Attacker.Inventory.Load([Row(InventorySlots.MainHand, SwordEntry)]);
            Assert.Equal(0.5, s.Attacker.Inventory.Options.DurabilityLossChanceDamage);

            Swing(s, WeaponAttackType.BaseAttack, 20_000);

            Assert.InRange(Lost(s.Attacker, InventorySlots.MainHand), 60u, 140u); // 0.5% of 20000 = 100, sd 10
        }
    }

    [Fact]
    public void OffHandSwings_WearTheOffHandWeaponOnly()
    {
        Scene s = Create(seeded: true, seed: 3);
        using (s.World)
        {
            s.Attacker.Inventory.Load([Row(InventorySlots.MainHand, SwordEntry), Row(InventorySlots.OffHand, SwordEntry)]);
            SetChance(s.Attacker, 10);

            Swing(s, WeaponAttackType.OffAttack, 2000);

            Assert.InRange(Lost(s.Attacker, InventorySlots.OffHand), 140u, 260u); // 200, sd 13
            Assert.Equal(0u, Lost(s.Attacker, InventorySlots.MainHand));
        }
    }

    [Fact]
    public void ASwingThatDealsNothing_WearsNothing()
    {
        Scene s = Create();
        using (s.World)
        {
            s.Attacker.Inventory.Load([Row(InventorySlots.MainHand, SwordEntry)]);
            EquipArmorEverywhere(s.Victim);
            SetChance(s.Attacker, 100);
            SetChance(s.Victim, 100);
            s.Scripted.Ints.Enqueue(0); // roll 0 of the hit table is a miss
            s.Scripted.DefaultFraction = 0f; // every percent roll would succeed

            MeleeDamageInfo info = s.Map.Combat.AttackerStateUpdate(s.Attacker, s.Victim, WeaponAttackType.BaseAttack)!;

            Assert.Equal(0u, info.TotalDamage);
            Assert.Equal(0u, Lost(s.Attacker, InventorySlots.MainHand));
            Assert.All(DurableSlots, slot => Assert.Equal(0u, Lost(s.Victim, slot)));
            Assert.Equal(BigHealth, s.Victim.Health);
        }
    }

    [Fact]
    public void ASpellHit_IsNotAWeaponSwing_SoTheCastersWeaponWearsNothing()
    {
        Scene s = Create();
        using (s.World)
        {
            s.Attacker.Inventory.Load([Row(InventorySlots.MainHand, SwordEntry)]);
            SetChance(s.Attacker, 100);
            s.Scripted.DefaultFraction = 0f;

            s.Map.Combat.DealDamage(s.Attacker, s.Victim, 10, direct: true, meleeDamage: false);

            Assert.Equal(BigHealth - 10, s.Victim.Health);
            Assert.Equal(0u, Lost(s.Attacker, InventorySlots.MainHand));
        }
    }

    // --- hit taken: one of the 19 equipment slots, uniformly ------------------------------------------

    [Fact]
    public void HitsTaken_WearUniformlyChosenSlots_AtTheConfiguredChance_AndSkipItemsWithoutDurability()
    {
        Scene s = Create(seeded: true, seed: 11);
        using (s.World)
        {
            EquipArmorEverywhere(s.Victim);
            SetChance(s.Victim, 10);
            SetChance(s.Attacker, 0); // only the victim rolls

            Swing(s, WeaponAttackType.BaseAttack, 5000);

            uint total = (uint)DurableSlots.Sum(slot => Lost(s.Victim, slot));
            // 10% of 5000 = 500 events, 12 of the 19 slots hold a durable item: ~316 points (sd 11).
            Assert.InRange(total, 250u, 380u);
            foreach (byte slot in DurableSlots)
            {
                Assert.InRange(Lost(s.Victim, slot), 10u, 45u); // ~26 each (sd 5): the slots weigh the same
            }

            Assert.Equal(0u, At(s.Victim, InventorySlots.Finger1).MaxDurability);
            Assert.Equal(0u, At(s.Victim, InventorySlots.Finger1).Durability);
        }
    }

    [Fact]
    public void EachRolledSlot_WearsExactlyThatSlot_ByOnePoint()
    {
        Scene s = Create();
        using (s.World)
        {
            EquipArmorEverywhere(s.Victim);
            SetChance(s.Attacker, 0);
            s.Scripted.DefaultFraction = 0.004f; // the percent roll lands on 0.4: below the default 0.5

            for (int slot = 0; slot < InventorySlots.EquipmentEnd; slot++)
            {
                uint[] before = DurableSlots.Select(d => At(s.Victim, d).Durability).ToArray();
                s.Scripted.Ints.Enqueue(CombatConstants.RollRange - 1); // the hit table: a plain hit
                s.Scripted.Ints.Enqueue(slot);                           // then the slot pick

                Swing(s);

                for (int i = 0; i < DurableSlots.Length; i++)
                {
                    uint expected = DurableSlots[i] == slot ? before[i] - 1 : before[i];
                    Assert.Equal(expected, At(s.Victim, DurableSlots[i]).Durability);
                }

                Assert.Empty(s.Scripted.Ints);
            }
        }
    }

    [Fact]
    public void ThePercentRoll_IsStrict_AtTheBoundary()
    {
        Scene s = Create();
        using (s.World)
        {
            EquipArmorEverywhere(s.Victim);
            SetChance(s.Attacker, 0);
            s.Scripted.DefaultFraction = 0.006f; // 0.6 is not below 0.5: no wear

            s.Scripted.Ints.Enqueue(CombatConstants.RollRange - 1);
            s.Scripted.Ints.Enqueue(InventorySlots.Chest);
            Swing(s);

            Assert.Equal(0u, Lost(s.Victim, InventorySlots.Chest));
            Assert.Single(s.Scripted.Ints); // the slot was never picked
        }
    }

    [Fact]
    public void ACreatureHit_WearsAPlayersArmor_Too()
    {
        Scene s = Create();
        using (s.World)
        {
            EquipArmorEverywhere(s.Victim);
            var creature = new CombatTestUnit();
            creature.Spawn(s.Map, 2, 1);
            s.Scripted.DefaultFraction = 0.004f;
            s.Scripted.Ints.Enqueue(InventorySlots.Legs);

            uint dealt = s.Map.Combat.DealDamage(creature, s.Victim, 10);

            Assert.Equal(10u, dealt);
            Assert.Equal(1u, Lost(s.Victim, InventorySlots.Legs));
            Assert.Equal(BigHealth - 10, s.Victim.Health);
        }
    }

    // --- configuration ------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, 100d)]  // Items:DurabilityLossEnable off overrides the chance
    [InlineData(true, 0d)]
    [InlineData(true, -5d)]
    public void DisabledOrNonPositiveChance_WearsNothing_AndRollsNothing(bool enable, double chance)
    {
        Scene s = Create();
        using (s.World)
        {
            EquipArmorEverywhere(s.Victim);
            s.Attacker.Inventory.Load([Row(InventorySlots.MainHand, SwordEntry)]);
            foreach (Player p in new[] { s.Attacker, s.Victim })
            {
                p.Inventory.Options.DurabilityLossEnable = enable;
                SetChance(p, chance);
            }

            s.Scripted.DefaultFraction = 0f;
            s.Scripted.Ints.Enqueue(CombatConstants.RollRange - 1);
            s.Scripted.Ints.Enqueue(InventorySlots.Chest);
            Swing(s);

            Assert.All(DurableSlots, slot => Assert.Equal(0u, Lost(s.Victim, slot)));
            Assert.Equal(0u, Lost(s.Attacker, InventorySlots.MainHand));
            Assert.Single(s.Scripted.Ints); // no slot was drawn
        }
    }

    // --- broken items ---------------------------------------------------------------------------------

    [Fact]
    public void ABrokenWeapon_StaysAtZero_AndTheSwingsKeepLanding()
    {
        Scene s = Create();
        using (s.World)
        {
            s.Attacker.Inventory.Load([Row(InventorySlots.MainHand, SwordEntry, durability: 1)]);
            SetChance(s.Attacker, 100);
            s.Scripted.DefaultFraction = 0f;

            Swing(s, times: 3);

            Assert.Equal(0u, At(s.Attacker, InventorySlots.MainHand).Durability);
            Assert.Equal(BigHealth - 3, s.Victim.Health);
        }
    }

    [Fact]
    public void ABrokenItem_TakesItsStatsOff_StaysOff_AndComesBackWhenRepaired()
    {
        (Player player, _) = CreatePlayer(level: 60);
        player.Inventory.Templates = Store;
        new PlayerStatSystem(null).Attach(player);
        player.Inventory.Load(
        [
            new InventoryItemData(0, InventorySlots.MainHand, new ItemInstanceData { Guid = 4000, Entry = WornShortsword, Durability = 2, Charges = [0, 0, 0, 0, 0], Enchantments = new uint[21] }),
            new InventoryItemData(0, InventorySlots.OffHand, new ItemInstanceData { Guid = 4001, Entry = WornWoodenShield, Durability = 2, Charges = [0, 0, 0, 0, 0], Enchantments = new uint[21] }),
        ]);
        Item sword = At(player, InventorySlots.MainHand);
        Item shield = At(player, InventorySlots.OffHand);
        PlayerStatState state = player.StatState;
        Assert.Equal(3f, state.WeaponDamage(WeaponAttackType.BaseAttack, 0).Max);
        Assert.Equal(1f, state.ShieldBlockFlat);
        uint armor = player.GetUInt32(UpdateFields.UnitFieldResistances);
        Assert.True(armor >= 5); // the shield's armor

        player.Inventory.DurabilityPointLossForEquipSlot(InventorySlots.MainHand);
        player.Inventory.DurabilityPointLossForEquipSlot(InventorySlots.OffHand);
        Assert.Equal(3f, state.WeaponDamage(WeaponAttackType.BaseAttack, 0).Max); // one point left: still contributing
        Assert.Equal(1f, state.ShieldBlockFlat);

        player.Inventory.DurabilityPointLossForEquipSlot(InventorySlots.MainHand);
        player.Inventory.DurabilityPointLossForEquipSlot(InventorySlots.OffHand);
        Assert.Equal((0u, 0u), (sword.Durability, shield.Durability));
        Assert.Equal(0f, state.WeaponDamage(WeaponAttackType.BaseAttack, 0).Max);
        Assert.Equal(0f, state.ShieldBlockFlat);
        Assert.Equal(armor - 5, player.GetUInt32(UpdateFields.UnitFieldResistances));

        player.Inventory.DurabilityPointLossForEquipSlot(InventorySlots.MainHand); // already broken: nothing is taken off twice
        Assert.Equal(0f, state.WeaponDamage(WeaponAttackType.BaseAttack, 0).Max);
        Assert.Equal(armor - 5, player.GetUInt32(UpdateFields.UnitFieldResistances));

        player.Inventory.RepairDurability(shield); // repairing puts the shield back in play
        Assert.Equal(1f, state.ShieldBlockFlat);
        Assert.Equal(armor, player.GetUInt32(UpdateFields.UnitFieldResistances));
    }

    // --- the death penalty is unchanged ----------------------------------------------------------------

    [Fact]
    public void ALethalHit_WearsNothingExtra_AndTheDeathPenaltyIsStillTenPercent()
    {
        Scene s = Create();
        using (s.World)
        {
            EquipArmorEverywhere(s.Victim);
            var creature = new CombatTestUnit();
            creature.Spawn(s.Map, 2, 1);
            s.Victim.Health = 10;
            s.Scripted.DefaultFraction = 0.004f; // every percent roll would succeed
            s.Scripted.Ints.Enqueue(InventorySlots.Legs);

            s.Map.Combat.DealDamage(creature, s.Victim, 10);

            Assert.NotEqual(DeathState.Alive, s.Victim.Combat.DeathState);
            Assert.All(DurableSlots, slot => Assert.Equal(Durable / 10, Lost(s.Victim, slot))); // max(1, int(10000 x 0.10)): no extra point
            Assert.Single(s.Scripted.Ints); // the killing blow never drew a slot
        }
    }
}

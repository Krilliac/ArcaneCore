using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Risk;

/// <summary>
/// A hurt warrior with a bow kites on the real world (<see cref="RiskTestWorld"/>, manual clock): it hamstrings the creature on it, walks
/// out, and then holds its range and shoots instead of walking back into melee after every step (the brain positions with the kite's
/// range while the kite is under way). The spells are synthetic (an instant Hamstring with a 50% snare, an instant Shoot Bow); the
/// weapon and the arrows are real items from an <see cref="ItemTestContent"/>. Also pins <see cref="PlayerbotKite.HasUsableRangedWeapon"/>.
/// </summary>
public sealed class PlayerbotKiteRangedWorldTests
{
    private const uint HamstringId = 992_510;
    private const uint ShootBowId = 992_511;
    private const uint MobEntry = 992_610;
    private const uint BowEntry = 992_700;
    private const uint ThrownEntry = 992_701;
    private const uint ArrowEntry = 992_702;

    private const uint SubClassBow = 2;
    private const uint SubClassThrown = 16;
    private const uint SubClassArrow = 2;

    private static SpellInfo Hamstring() => new()
    {
        Id = HamstringId, Name = "Hamstring", Rank = "Rank 1", CastTime = new SpellCastTime(0, 0, 0), RangeIndex = SpellConstants.RangeIndexCombat,
        Range = new SpellRange(0f, 5f), StartRecoveryCategory = 133, StartRecoveryTime = 1500, DamageClass = SpellDamageClass.Magic,
        Duration = new SpellDuration(8_000, 0, 8_000),
        Effects =
        [
            new SpellEffectInfo { Effect = SpellEffectName.SchoolDamage, TargetA = SpellImplicitTarget.UnitEnemy, BasePoints = 0, BaseDice = 1, DieSides = 1 },
            new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.ModDecreaseSpeed, TargetA = SpellImplicitTarget.UnitEnemy, BasePoints = -51, BaseDice = 1, DieSides = 1 },
            new SpellEffectInfo(),
        ],
    };

    private static SpellInfo ShootBow() => new()
    {
        Id = ShootBowId, Name = "Shoot Bow", Rank = "Rank 1", CastTime = new SpellCastTime(0, 0, 0), RangeIndex = 5, Range = new SpellRange(0f, 35f),
        StartRecoveryCategory = 133, StartRecoveryTime = 1500, DamageClass = SpellDamageClass.Magic,
        Effects =
        [
            new SpellEffectInfo { Effect = SpellEffectName.SchoolDamage, TargetA = SpellImplicitTarget.UnitEnemy, BasePoints = 0, BaseDice = 1, DieSides = 1 },
            new SpellEffectInfo(),
            new SpellEffectInfo(),
        ],
    };

    private static ItemTestContent Content(uint maxDurability = 0)
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate
        {
            Entry = BowEntry, Name = "Test bow", Class = (uint)ItemClass.Weapon, SubClass = SubClassBow, InventoryType = (uint)InventoryType.Ranged,
            Delay = 2500, Damages = [new ItemDamage(3, 5, 0)], Stackable = 1, MaxDurability = maxDurability,
        });
        items.Templates.Templates.Add(new ItemTemplate
        {
            Entry = ThrownEntry, Name = "Test knife", Class = (uint)ItemClass.Weapon, SubClass = SubClassThrown, InventoryType = (uint)InventoryType.Thrown,
            Delay = 2000, Damages = [new ItemDamage(3, 5, 0)], Stackable = 200,
        });
        items.Templates.Templates.Add(new ItemTemplate
        {
            Entry = ArrowEntry, Name = "Test arrow", Class = (uint)ItemClass.Projectile, SubClass = SubClassArrow, InventoryType = (uint)InventoryType.Ammo,
            Damages = [new ItemDamage(1, 2, 0)], Stackable = 200,
        });
        return items;
    }

    /// <summary>World thread: wear <paramref name="entry"/> in the ranged slot and carry <paramref name="arrows"/> arrows, selected as the ammunition when any.</summary>
    private static Item EquipRanged(Player player, uint entry, uint arrows)
    {
        Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(entry, 1, out Item? weapon));
        Assert.NotNull(weapon);
        player.Inventory.RemoveItem(weapon.BagSlot, weapon.Slot);
        player.Inventory.EquipItem(InventorySlots.Ranged, weapon);
        Item worn = player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Ranged) ?? throw new InvalidOperationException("the ranged weapon was not equipped");
        if (arrows > 0)
        {
            Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(ArrowEntry, arrows, out _));
            player.Inventory.RestoreAmmo(ArrowEntry);
        }

        return worn;
    }

    private static float Gap(RiskTestWorld world, Creature mob)
        => Vector3.Distance(new Vector3(world.Player.X, world.Player.Y, world.Player.Z), new Vector3(mob.X, mob.Y, mob.Z));

    private static string Describe(RiskTestWorld world, Creature mob)
        => $"gap {Gap(world, mob):F1} mob speed {mob.RunSpeed:F1} bot speed {world.Player.RunSpeed:F1} steps {world.Brain.Kite.Steps} over {world.Brain.Kite.Over} "
            + $"retreat {world.Brain.Risk.Retreat.Active}";

    /// <summary>
    /// W1: a warrior at 45% health with a bow and arrows and Hamstring, a melee creature on it. Over the first eight seconds it is outside
    /// melee reach for most of the sampled ticks (it used to step out, fire at most once and walk back in: an out-and-back every step),
    /// and the creature takes damage while the warrior is out of reach, which only a shot can deal.
    /// </summary>
    [Fact]
    public async Task AHurtWarriorWithABow_StaysAtRange_AndShoots_InsteadOfOscillatingToMelee()
    {
        ItemTestContent items = Content();
        using IDisposable scope = items.Use();
        RiskTestWorld world = await RiskTestWorld.StartAsync();
        await using (world)
        {
            Creature mob = await world.OnWorldAsync(() => world.Spawn(RiskTestWorld.Template(MobEntry, health: 400, minDamage: 1, maxDamage: 2, runSpeed: 1.0f), 2, 0));
            await world.SeeAsync(mob);
            await world.OnWorldAsync(() =>
            {
                Player player = world.Player;
                player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Warrior);
                SpellFeature feature = world.Session.Services.GetRequiredService<SpellFeature>();
                SpellInfo[] added = [Hamstring(), ShootBow()];
                feature.System.Store = new SpellStore([.. feature.System.Store.All, .. added], [], []);
                foreach (SpellInfo spell in added) Assert.True(feature.Spellbook.LearnSpell(player, spell.Id));
                EquipRanged(player, BowEntry, 100);
                Assert.True(PlayerbotKite.HasUsableRangedWeapon(player), "the bow is not usable, so the test proves nothing");
                world.Engage(mob);
                player.Health = player.MaxHealth * 45 / 100;
                return true;
            });

            int ticks = 0, outside = 0, shots = 0;
            uint lastHealth = await world.OnWorldAsync(() => mob.Health);
            float lastGap = await world.OnWorldAsync(() => Gap(world, mob));
            await world.ThinkUntilAsync(8_000, () => false, each: () =>
            {
                float gap = Gap(world, mob);
                ticks++;
                if (gap > 6f) outside++;
                if (gap > 6f && lastGap > 6f && mob.Health < lastHealth) shots++;
                lastHealth = mob.Health;
                lastGap = gap;
            });

            string why = await world.OnWorldAsync(() => Describe(world, mob));
            Assert.True(ticks >= 70, $"only {ticks} ticks were sampled");
            Assert.True(outside * 2 > ticks, $"outside melee reach for {outside} of {ticks} ticks: {why}");
            Assert.True(shots >= 1, $"the warrior never shot from range ({shots} damaging ticks out of reach): {why}");
            Assert.True(await world.OnWorldAsync(() => world.Brain.Kite.Steps) >= 1, "the warrior never stepped: " + why);
            Assert.False(await world.OnWorldAsync(() => world.Brain.Risk.Retreat.Active), "retreating: " + why);
        }
    }

    /// <summary>A bow with no arrows selected shoots nothing, so the warrior does not count as a ranged fighter.</summary>
    [Fact]
    public async Task ABowWithNoAmmunition_IsNotUsable()
    {
        ItemTestContent items = Content();
        using IDisposable scope = items.Use();
        RiskTestWorld world = await RiskTestWorld.StartAsync();
        await using (world)
        {
            Assert.False(await world.OnWorldAsync(() =>
            {
                EquipRanged(world.Player, BowEntry, 0);
                Assert.Equal(0u, world.Player.Inventory.AmmoId);
                return PlayerbotKite.HasUsableRangedWeapon(world.Player);
            }));
        }
    }

    /// <summary>The control for the two cases around it: the same bow with arrows is usable.</summary>
    [Fact]
    public async Task ABowWithArrows_IsUsable()
    {
        ItemTestContent items = Content();
        using IDisposable scope = items.Use();
        RiskTestWorld world = await RiskTestWorld.StartAsync();
        await using (world)
            Assert.True(await world.OnWorldAsync(() => { EquipRanged(world.Player, BowEntry, 20); return PlayerbotKite.HasUsableRangedWeapon(world.Player); }));
    }

    /// <summary>A bow worn down to zero durability does not shoot, arrows or not.</summary>
    [Fact]
    public async Task ABowAtZeroDurability_IsNotUsable()
    {
        ItemTestContent items = Content(maxDurability: 40);
        using IDisposable scope = items.Use();
        RiskTestWorld world = await RiskTestWorld.StartAsync();
        await using (world)
        {
            Assert.False(await world.OnWorldAsync(() =>
            {
                Item bow = EquipRanged(world.Player, BowEntry, 20);
                Assert.Equal(40u, bow.MaxDurability);
                Assert.True(PlayerbotKite.HasUsableRangedWeapon(world.Player), "the bow is not usable even before it wears out, so the test proves nothing");
                bow.Durability = 0;
                return PlayerbotKite.HasUsableRangedWeapon(world.Player);
            }));
        }
    }

    /// <summary>A thrown weapon is its own ammunition: usable with no arrows or bullets selected.</summary>
    [Fact]
    public async Task AThrownWeapon_IsUsableWithNoAmmunition()
    {
        ItemTestContent items = Content();
        using IDisposable scope = items.Use();
        RiskTestWorld world = await RiskTestWorld.StartAsync();
        await using (world)
        {
            Assert.True(await world.OnWorldAsync(() =>
            {
                EquipRanged(world.Player, ThrownEntry, 0);
                Assert.Equal(0u, world.Player.Inventory.AmmoId);
                return PlayerbotKite.HasUsableRangedWeapon(world.Player);
            }));
        }
    }
}

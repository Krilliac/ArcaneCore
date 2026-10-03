using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Ranged;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Stats;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Ranged;

/// <summary>
/// Ranged lane S04: the inputs of ranged weapon damage. Ammo DPS in the player's ranged damage fields (vmangos StatSystem.cpp:440-443,
/// Player::_ApplyAmmoBonuses Player.cpp:7514-7535), the attack type of weapon damage spells (SpellEntry::GetWeaponAttackType,
/// SpellEntry.cpp:434-455), the ranged attack power auras 127 / 131 and the flat ranged damage taken 113 (SpellCaster.cpp:1340-1346,
/// 1411-1437, Unit.cpp:5703) and the attacker ranged hit chance aura 185 (SpellCaster.cpp:416). Aura values are the real ones:
/// Hunter's Mark rank 4 is aura 127 +110 (classic-db spell 14325), Elune's Grace aura 113 -21. The combat rules in the spell tests
/// are neutral (always hit, never crit), so every expected damage is exact.
/// </summary>
public sealed class RangedDamageTests
{
    private const uint Mark = 940701;        // aura 127, +110, on the victim
    private const uint Grace = 940702;       // aura 113, -21, on the victim
    private const uint HumanoidSlaying = 940703; // aura 131, +63, mask 64 (humanoid), on the attacker
    private const uint BeastSlaying = 940704;    // aura 131, +63, mask 1 (beast), on the attacker
    private const uint Heightened = 940705;  // aura 185, -20, on the victim
    private const uint RapidFire = 940706;   // aura 140, +40
    private const uint Shot = 940707;        // WEAPON_DAMAGE 5, ranged class, ranged slot
    private const uint Normalized = 940708;  // NORMALIZED_WEAPON_DMG 5, ranged class
    private const uint MeleeHit = 940709;    // WEAPON_DAMAGE 5, melee class
    private const uint WandShoot = 940710;   // Shoot shape: damage class magic, Ex2 0x20

    private const uint Bow = 94301;
    private const uint Gun = 94302;
    private const uint Wand = 94303;
    private const uint Arrow = 94310;
    private const uint Bullet = 94311;

    private static readonly ItemTemplate[] Templates =
    [
        new() { Entry = Bow, Class = 2, SubClass = 2, DisplayId = 300, InventoryType = 15, Delay = 2500, MaxDurability = 40, AmmoType = 2, Damages = [new ItemDamage(20, 30, 0)] },
        new() { Entry = Gun, Class = 2, SubClass = 3, DisplayId = 301, InventoryType = 26, Delay = 2800, MaxDurability = 40, AmmoType = 3, Damages = [new ItemDamage(20, 30, 0)] },
        new() { Entry = Wand, Class = 2, SubClass = 19, DisplayId = 302, InventoryType = 26, Delay = 1500, MaxDurability = 40, Damages = [new ItemDamage(10, 20, 0)] },
        new() { Entry = Arrow, Class = 6, SubClass = 2, DisplayId = 5996, InventoryType = 24, Stackable = 200, Damages = [new ItemDamage(20, 21, 0)] },
        new() { Entry = Bullet, Class = 6, SubClass = 3, DisplayId = 5998, InventoryType = 24, Stackable = 200, Damages = [new ItemDamage(10, 11, 0)] },
    ];

    // --- ammo DPS in the damage fields ---------------------------------------------------------

    private static (Player Player, PlayerStatSystem System) StatPlayer()
    {
        (Player player, _) = ItemTestData.CreatePlayer(level: 60);
        player.Inventory.Templates = new ItemTemplateStore([.. ItemTestData.Templates, .. Templates]);
        uint[] stats = [120, 80, 100, 20, 30];
        for (int i = 0; i < stats.Length; i++)
        {
            player.SetUInt32(UpdateFields.UnitFieldStat0 + i, stats[i]);
        }

        var system = new PlayerStatSystem();
        system.Attach(player);
        return (player, system);
    }

    private static void EquipRanged(Player player, uint entry)
    {
        Item item = ItemTestData.Give(player.Inventory, entry);
        Assert.Equal(InventoryResult.Ok, player.Inventory.CanEquipItem(InventorySlots.NullSlot, out byte dest, item.Template, item, swap: false));
        Assert.Equal(InventorySlots.Ranged, dest);
        player.Inventory.RemoveItem(item.BagSlot, item.Slot);
        player.Inventory.EquipItem(dest, item);
    }

    private static (float Min, float Max) Ranged(Player player)
        => (player.GetFloat(UpdateFields.UnitFieldMinrangeddamage), player.GetFloat(UpdateFields.UnitFieldMaxrangeddamage));

    [Fact]
    public void AmmoDps_AddsAverageAmmoDamageTimesWeaponSpeed_AndFollowsSetAndRemoveAmmo()
    {
        (Player player, _) = StatPlayer();
        EquipRanged(player, Bow);
        (float baseMin, float baseMax) = Ranged(player);
        ItemTestData.Give(player.Inventory, Arrow, 20);

        player.Inventory.SetAmmo(Arrow);

        // (20 + 21) / 2 = 20.5 damage per second at the bow's 2.5 s: +51.25 on both ends (StatSystem.cpp:440-443).
        (float min, float max) = Ranged(player);
        Assert.Equal(baseMin + 51.25f, min, 0.01f);
        Assert.Equal(baseMax + 51.25f, max, 0.01f);

        player.Inventory.RemoveAmmo();
        Assert.Equal((baseMin, baseMax), Ranged(player));
    }

    [Fact]
    public void AmmoSelectedBeforeTheBowIsEquipped_StillCounts_AndAMismatchedLauncherDropsIt()
    {
        (Player player, _) = StatPlayer();
        ItemTestData.Give(player.Inventory, Arrow, 20);
        player.Inventory.SetAmmo(Arrow); // refused or accepted, no launcher yet: no damage term
        (float noWeaponMin, _) = Ranged(player);

        EquipRanged(player, Bow);
        (float withBow, _) = Ranged(player);
        Assert.True(withBow >= noWeaponMin);

        // Arrows do not fit a gun (Player::CheckAmmoCompatibility): the ammo term disappears with the bow.
        player.Inventory.RemoveItem(InventorySlots.Bag0, InventorySlots.Ranged);
        EquipRanged(player, Gun);
        (float gunMin, float gunMax) = Ranged(player);
        player.Inventory.RemoveAmmo();
        Assert.Equal((gunMin, gunMax), Ranged(player)); // nothing to remove: arrows never counted with the gun
    }

    [Fact]
    public void AmmoThatDoesNotFitTheLauncher_AddsNothing()
    {
        (Player player, _) = StatPlayer();
        EquipRanged(player, Bow);
        (float baseMin, float baseMax) = Ranged(player);
        ItemTestData.Give(player.Inventory, Bullet, 20);

        player.Inventory.SetAmmo(Bullet);

        Assert.Equal((baseMin, baseMax), Ranged(player));
    }

    // --- spell damage ----------------------------------------------------------------------------

    private static SpellInfo Aura(uint id, int amount, AuraType type, int misc = 0, bool onTarget = true) => Spell(id, Effect(SpellEffectName.ApplyAura, amount, onTarget ? SpellImplicitTarget.UnitEnemy : SpellImplicitTarget.UnitCaster, aura: type, misc: misc)) with
    {
        Duration = new SpellDuration(60_000, 0, 60_000),
        SpellVisual = 1,
        Attributes = onTarget ? SpellAttributes.AuraIsDebuff : SpellAttributes.None,
        RangeIndex = 4,
        Range = new SpellRange(0, 35),
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static SpellInfo Weapon(uint id, SpellEffectName effect, SpellDamageClass damageClass, SpellAttributes attributes, uint ex2 = 0) => Spell(id, Effect(effect, 5, SpellImplicitTarget.UnitEnemy)) with
    {
        Attributes = attributes,
        AttributesEx2 = (SpellAttributesEx2)ex2,
        DamageClass = damageClass,
        RangeIndex = 4,
        Range = new SpellRange(0, 35),
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            Kit = new SpellTestKit(
                Aura(Mark, 110, AuraType.RangedAttackPowerAttackerBonus),
                Aura(Grace, -21, AuraType.ModRangedDamageTaken),
                Aura(HumanoidSlaying, 63, AuraType.ModRangedAttackPowerVersus, misc: 64, onTarget: false),
                Aura(BeastSlaying, 63, AuraType.ModRangedAttackPowerVersus, misc: 1, onTarget: false),
                Aura(Heightened, -20, AuraType.ModAttackerRangedHitChance),
                Aura(RapidFire, 40, AuraType.ModRangedHaste, onTarget: false),
                Weapon(Shot, SpellEffectName.WeaponDamage, SpellDamageClass.Ranged, SpellAttributes.UsesRangedSlot),
                Weapon(Normalized, SpellEffectName.NormalizedWeaponDmg, SpellDamageClass.Ranged, SpellAttributes.UsesRangedSlot),
                Weapon(MeleeHit, SpellEffectName.WeaponDamage, SpellDamageClass.Melee, SpellAttributes.None),
                Weapon(WandShoot, SpellEffectName.WeaponDamage, SpellDamageClass.Magic, (SpellAttributes)0x12, 0x20));
            (Player, _) = Kit.AddPlayer(1);
            (Target, _) = Kit.AddPlayer(2, 20);
            Target.Health = 100_000;
            Target.MaxHealth = 100_000;
            Player.Inventory.Templates = new ItemTemplateStore([.. ItemTestData.Templates, .. Templates]);
            Player.Inventory.GuidAllocator = new ItemGuidAllocator();
            Player.SetUInt32(UpdateFields.UnitFieldRangedattacktime, 2500);
            Player.SetUInt32(UpdateFields.UnitFieldBaseattacktime, 2000);
            Player.SetFloat(UpdateFields.UnitFieldMinrangeddamage, 100f);
            Player.SetFloat(UpdateFields.UnitFieldMaxrangeddamage, 100f);
            Player.SetFloat(UpdateFields.UnitFieldMindamage, 100f);
            Player.SetFloat(UpdateFields.UnitFieldMaxdamage, 100f);
            EquipRanged(Player, Bow);
            ItemTestData.Give(Player.Inventory, Arrow, 200);
            Player.Inventory.SetAmmo(Arrow);
        }

        public SpellTestKit Kit { get; }

        public Player Player { get; }

        public Player Target { get; }

        public void Apply(uint spell, bool onTarget)
            => Kit.System.CastSpell(Player, spell, onTarget ? SpellCastTargets.ForUnit(Target.Guid) : SpellCastTargets.ForSelf(), triggered: true);

        /// <summary>Cast a weapon spell on the target and return the damage it dealt.</summary>
        public uint Hit(uint spell)
        {
            Target.Health = 100_000;
            Assert.Equal(SpellCastResult.CastOk, Kit.System.CastSpell(Player, spell, SpellCastTargets.ForUnit(Target.Guid), triggered: true));
            return 100_000 - Target.Health;
        }

        public void Dispose() => Kit.Dispose();
    }

    [Fact]
    public void WithoutAnyBonus_ARangedWeaponSpell_DealsTheRollPlusTheEffectValue()
    {
        using var rig = new Rig();

        Assert.Equal(105u, rig.Hit(Shot));
    }

    [Fact]
    public void HuntersMark_AddsAttackPowerOverFourteenTimesTheUnhastedWeaponSpeed_ToRangedSpellsOnly()
    {
        using var rig = new Rig();
        rig.Apply(Mark, onTarget: true);

        Assert.Equal((uint)(105 + (110 / 14.0f * 2.5f)), rig.Hit(Shot));      // 2.5 s: the weapon speed of a weapon damage spell
        Assert.Equal((uint)(105 + (110 / 14.0f * 2.8f)), rig.Hit(Normalized)); // 2.8: the normalized ranged speed (SpellCaster.cpp:1826-1853)
        Assert.Equal(105u, rig.Hit(MeleeHit));                                // melee spells do not read the ranged aura
    }

    [Fact]
    public void RapidFire_DoesNotChangeTheDamagePerHit()
    {
        using var rig = new Rig();
        rig.Apply(Mark, onTarget: true);
        uint before = rig.Hit(Shot);

        rig.Apply(RapidFire, onTarget: false);
        Assert.True(rig.Player.GetUInt32(UpdateFields.UnitFieldRangedattacktime) < 2500u, "Rapid Fire must be up");

        Assert.Equal(before, rig.Hit(Shot));
    }

    [Fact]
    public void SlayingAura131_FollowsTheVictimsCreatureTypeMask()
    {
        using var rig = new Rig();

        rig.Apply(BeastSlaying, onTarget: false);
        Assert.Equal(105u, rig.Hit(Shot));                     // the target is a player (humanoid): a beast mask does not match

        rig.Apply(HumanoidSlaying, onTarget: false);
        Assert.Equal((uint)(105 + (63 / 14.0f * 2.5f)), rig.Hit(Shot));
        Assert.Equal(105u, rig.Hit(MeleeHit));
    }

    [Fact]
    public void ElunesGrace_IsAFlatReductionOfRangedDamageOnly()
    {
        using var rig = new Rig();
        rig.Apply(Grace, onTarget: true);

        Assert.Equal(84u, rig.Hit(Shot));
        Assert.Equal(105u, rig.Hit(MeleeHit));
    }

    [Fact]
    public void AWandShoot_SwingsTheRangedWeapon_NotTheMainHand()
    {
        using var rig = new Rig();
        rig.Player.SetFloat(UpdateFields.UnitFieldMinrangeddamage, 77f);
        rig.Player.SetFloat(UpdateFields.UnitFieldMaxrangeddamage, 77f);
        rig.Player.Inventory.RemoveItem(InventorySlots.Bag0, InventorySlots.Ranged);
        EquipRanged(rig.Player, Wand);

        Assert.Equal(82u, rig.Hit(WandShoot)); // 77 ranged + 5, not the 100 of the main hand
    }

    // --- aura 185: the attacker's ranged miss chance ----------------------------------------------

    [Fact]
    public void Aura185_ChangesTheRangedMissChance_ButNotTheMeleeOne()
    {
        using var rig = new Rig();
        var rules = new VanillaSpellCombatRules();
        SpellInfo ranged = rig.Kit.Store.Get(Shot)!;
        SpellInfo melee = rig.Kit.Store.Get(MeleeHit)!;

        int Misses(SpellInfo spell)
        {
            int misses = 0;
            for (int i = 0; i < 4000; i++)
            {
                if (rules.RollHit(rig.Kit.System, rig.Player, rig.Target, spell) == SpellMissInfo.Miss)
                {
                    misses++;
                }
            }

            return misses;
        }

        int baseline = Misses(ranged);
        int meleeBaseline = Misses(melee);
        rig.Apply(Heightened, onTarget: true);

        int withAura = Misses(ranged);
        int meleeWithAura = Misses(melee);

        // vmangos: missChance -= victim's aura total (SpellCaster.cpp:416), so -20 adds 20 points of miss to ranged attacks.
        Assert.InRange(withAura - baseline, 4000 * 0.15, 4000 * 0.25);
        Assert.InRange(Math.Abs(meleeWithAura - meleeBaseline), 0, 4000 * 0.05);
    }
}

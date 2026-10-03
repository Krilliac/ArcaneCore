using ArcaneCore.Game.Ranged;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using Xunit;

namespace ArcaneCore.Game.Tests.Ranged;

/// <summary>Pure ranged rules: item classification, ammo matching and the Spell.dbc attribute facts.</summary>
public sealed class RangedRulesTests
{
    private static ItemTemplate Weapon(uint subClass) => new() { Entry = 1, Class = 2, SubClass = subClass, InventoryType = 15 };

    private static ItemTemplate Projectile(uint subClass, float min = 4, float max = 5, uint flags = 0)
        => new() { Entry = 2, Class = 6, SubClass = subClass, InventoryType = 24, Damages = [new ItemDamage(min, max, 0)], Flags = flags };

    [Theory]
    [InlineData(2u, RangedWeaponKind.Bow)]
    [InlineData(3u, RangedWeaponKind.Gun)]
    [InlineData(16u, RangedWeaponKind.Thrown)]
    [InlineData(18u, RangedWeaponKind.Crossbow)]
    [InlineData(19u, RangedWeaponKind.Wand)]
    [InlineData(7u, RangedWeaponKind.None)]
    public void Classify_UsesTheVmangosWeaponSubclasses(uint subClass, RangedWeaponKind expected)
        => Assert.Equal(expected, AmmoRules.Classify(Weapon(subClass)));

    [Fact]
    public void Classify_IgnoresNonWeaponsAndMissingItems()
    {
        Assert.Equal(RangedWeaponKind.None, AmmoRules.Classify(null));
        Assert.Equal(RangedWeaponKind.None, AmmoRules.Classify(new ItemTemplate { Class = 4, SubClass = 2 }));
    }

    [Theory]
    [InlineData(RangedWeaponKind.Bow, 2u, true)]
    [InlineData(RangedWeaponKind.Crossbow, 2u, true)]
    [InlineData(RangedWeaponKind.Gun, 3u, true)]
    [InlineData(RangedWeaponKind.Bow, 3u, false)]
    [InlineData(RangedWeaponKind.Crossbow, 3u, false)]
    [InlineData(RangedWeaponKind.Gun, 2u, false)]
    [InlineData(RangedWeaponKind.Thrown, 2u, false)]
    [InlineData(RangedWeaponKind.Wand, 3u, false)]
    [InlineData(RangedWeaponKind.None, 2u, false)]
    public void AmmoMatchesWeapon_FollowsTheLauncherMatrix(RangedWeaponKind weapon, uint ammoSubClass, bool expected)
        => Assert.Equal(expected, AmmoRules.AmmoMatchesWeapon(weapon, Projectile(ammoSubClass)));

    [Fact]
    public void AmmoMatchesWeapon_RequiresAProjectileClassItem()
    {
        var notProjectile = new ItemTemplate { Class = 0, SubClass = 2, InventoryType = 24 };
        Assert.False(AmmoRules.AmmoMatchesWeapon(RangedWeaponKind.Bow, notProjectile));
        Assert.False(AmmoRules.AmmoMatchesWeapon(RangedWeaponKind.Bow, null));
    }

    [Fact]
    public void IsAmmoItem_NeedsInventoryTypeAmmo()
    {
        Assert.True(AmmoRules.IsAmmoItem(Projectile(2)));
        Assert.False(AmmoRules.IsAmmoItem(new ItemTemplate { Class = 6, SubClass = 2, InventoryType = 21 }));
        Assert.False(AmmoRules.IsAmmoItem(null));
    }

    [Theory]
    [InlineData(4f, 5f, 4.5f)]
    [InlineData(20f, 21f, 20.5f)]
    [InlineData(0f, 1f, 0.5f)]
    public void AmmoDps_IsTheMeanOfTheFirstDamageEntry(float min, float max, float expected)
        => Assert.Equal(expected, AmmoRules.AmmoDps(Projectile(2, min, max)));

    [Fact]
    public void AmmoDps_IsZeroWithoutDamageEntries()
    {
        Assert.Equal(0f, AmmoRules.AmmoDps(null));
        Assert.Equal(0f, AmmoRules.AmmoDps(new ItemTemplate { Class = 6, SubClass = 2, InventoryType = 24 }));
    }

    [Fact]
    public void ExoticFlag_IsItemFlagExotic()
    {
        Assert.True(AmmoRules.IsExotic(Projectile(2, flags: 0x8)));
        Assert.False(AmmoRules.IsExotic(Projectile(2, flags: 0x4)));
        Assert.Equal(0x8u, AmmoRules.ExoticFlag);
    }

    [Fact]
    public void NoAmmoSpells_AreBlindNetOMaticAndExposeWeakness()
        => Assert.Equal(new uint[] { 2094, 13099, 13119, 23577 }, AmmoRules.NoAmmoSpellIds.OrderBy(i => i));

    [Theory]
    [InlineData(RangedWeaponKind.Bow, true)]
    [InlineData(RangedWeaponKind.Crossbow, true)]
    [InlineData(RangedWeaponKind.Gun, true)]
    [InlineData(RangedWeaponKind.Thrown, false)]
    [InlineData(RangedWeaponKind.Wand, false)]
    public void UsesAmmoSlot_OnlyLaunchers(RangedWeaponKind kind, bool expected)
        => Assert.Equal(expected, AmmoRules.UsesAmmoSlot(kind));

    [Fact]
    public void SpellFacts_ReadTheRawAttributeBits()
    {
        var plain = new SpellInfo();
        Assert.False(RangedSpellFacts.IsRanged(plain));

        var autoShot = new SpellInfo { Attributes = SpellAttributes.UsesRangedSlot, AttributesEx2 = (SpellAttributesEx2)0x20 };
        Assert.True(RangedSpellFacts.IsRanged(autoShot));
        Assert.True(RangedSpellFacts.IsAutoRepeatRanged(autoShot));

        // IsAutoRepeatRangedSpell needs both the ranged slot and the auto-repeat attribute.
        Assert.False(RangedSpellFacts.IsAutoRepeatRanged(new SpellInfo { AttributesEx2 = (SpellAttributesEx2)0x20 }));
        Assert.False(RangedSpellFacts.IsAutoRepeatRanged(new SpellInfo { Attributes = SpellAttributes.UsesRangedSlot }));

        Assert.True(RangedSpellFacts.NeedsExoticAmmo(new SpellInfo { Attributes = (SpellAttributes)0x8 }));
        Assert.False(RangedSpellFacts.NeedsExoticAmmo(new SpellInfo { Attributes = (SpellAttributes)0x10 }));

        Assert.True(RangedSpellFacts.DoesNotResetCombatTimers(new SpellInfo { AttributesEx2 = (SpellAttributesEx2)0x20000 }));
        Assert.False(RangedSpellFacts.DoesNotResetCombatTimers(new SpellInfo { AttributesEx2 = (SpellAttributesEx2)0x10000 }));
    }

    [Theory]
    [InlineData(SpellDamageClass.Ranged, 0u, true)]
    [InlineData(SpellDamageClass.Melee, 0x20u, false)]
    [InlineData(SpellDamageClass.Magic, 0x20u, true)]
    [InlineData(SpellDamageClass.Magic, 0u, false)]
    [InlineData(SpellDamageClass.None, 0x20u, true)]
    public void UsesRangedWeapon_FollowsGetWeaponAttackType(SpellDamageClass cls, uint ex2, bool expected)
        => Assert.Equal(expected, RangedSpellFacts.UsesRangedWeapon(new SpellInfo { DamageClass = cls, AttributesEx2 = (SpellAttributesEx2)ex2 }));

    [Fact]
    public void HasWeaponDamageEffect_FindsBothWeaponDamageEffects()
    {
        Assert.True(RangedSpellFacts.HasWeaponDamageEffect(new SpellInfo { Effects = [new SpellEffectInfo { Effect = SpellEffectName.WeaponDamage }] }));
        Assert.True(RangedSpellFacts.HasWeaponDamageEffect(new SpellInfo { Effects = [new SpellEffectInfo { Effect = SpellEffectName.WeaponDamageNoschool }] }));
        Assert.False(RangedSpellFacts.HasWeaponDamageEffect(new SpellInfo { Effects = [new SpellEffectInfo { Effect = SpellEffectName.SchoolDamage }] }));
    }

    [Fact]
    public void Options_DefaultToRetail()
    {
        var options = new RangedOptions();
        Assert.Equal(AmmoMode.Retail, options.Ammo.Mode);
        Assert.Equal(RangeLeewayMode.Retail, options.Range.Leeway);
        Assert.Equal("Ranged", RangedOptions.SectionName);
    }
}

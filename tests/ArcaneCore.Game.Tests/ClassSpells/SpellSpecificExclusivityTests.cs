using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.ClassSpells;

/// <summary>
/// The spell specific stacking rules of every class (vmangos Unit::RemoveNoStackAurasDueToAuraHolder, Unit.cpp:3355-3573, with
/// Spells::GetSpellSpecific, SpellEntry.cpp:14-175, and the predicates of SpellEntry.h:107-180): one aspect, one sting per caster, one armor, one
/// curse per caster, one elemental shield. The spells carry the family, flags, dispel type and icons the classification reads; the amounts keep the rank
/// arithmetic visible.
/// </summary>
public sealed class SpellSpecificExclusivityTests : IDisposable
{
    private const uint AspectOfTheHawk = 13165;
    private const uint AspectOfTheMonkey = 13163;
    private const uint SerpentSting = 1978;
    private const uint ViperSting = 3034;
    private const uint FrostArmor = 7301;
    private const uint MageArmor = 6117;
    private const uint CurseOfAgony = 980;
    private const uint CurseOfTheElements = 1490;
    private const uint LightningShield1 = 324;
    private const uint LightningShield2 = 325;
    private const uint ElementalShieldProc = 23552;
    private const uint AutoShot = 75;

    private const uint FamilyMage = 3;
    private const uint FamilyWarlock = 5;
    private const uint FamilyHunter = 9;
    private const uint FamilyShaman = 11;
    private const uint DispelCurse = 2;
    private const uint DispelPoison = 4;
    private const uint HunterAspectIcon = 122;

    private static SpellInfo Instant(SpellInfo spell) => spell with { StartRecoveryCategory = 0, StartRecoveryTime = 0, SpellVisual = 1 };

    private static SpellInfo Aspect(uint id, string name) => Instant(Spell(id, Effect(SpellEffectName.ApplyAura, 10, aura: AuraType.Dummy)) with
    {
        Name = name,
        SpellFamilyName = FamilyHunter,
        ActiveIconId = HunterAspectIcon,
        Duration = new SpellDuration(1_800_000, 0, 1_800_000),
    });

    private static SpellInfo Sting(uint id, string name) => Instant(Spell(id, Effect(SpellEffectName.ApplyAura, 10, SpellImplicitTarget.UnitEnemy, AuraType.Dummy)) with
    {
        Name = name,
        SpellFamilyName = FamilyHunter,
        Dispel = DispelPoison,
        Attributes = SpellAttributes.AuraIsDebuff,
        RangeIndex = 4,
        Range = new SpellRange(0, 30),
        Duration = new SpellDuration(15_000, 0, 15_000),
    });

    private static SpellInfo Armor(uint id, string name, ulong familyFlags) => Instant(Spell(id, Effect(SpellEffectName.ApplyAura, 10, aura: AuraType.Dummy)) with
    {
        Name = name,
        SpellFamilyName = FamilyMage,
        SpellFamilyFlags = familyFlags,
        Duration = new SpellDuration(1_800_000, 0, 1_800_000),
    });

    private static SpellInfo Curse(uint id, string name) => Instant(Spell(id, Effect(SpellEffectName.ApplyAura, 10, SpellImplicitTarget.UnitEnemy, AuraType.Dummy)) with
    {
        Name = name,
        SpellFamilyName = FamilyWarlock,
        Dispel = DispelCurse,
        Attributes = SpellAttributes.AuraIsDebuff,
        RangeIndex = 4,
        Range = new SpellRange(0, 30),
        Duration = new SpellDuration(24_000, 0, 24_000),
    });

    private static SpellInfo Shield(uint id, string name, ulong familyFlags, int amount) => Instant(Spell(id, Effect(SpellEffectName.ApplyAura, amount, aura: AuraType.Dummy)) with
    {
        Name = name,
        SpellFamilyName = FamilyShaman,
        SpellFamilyFlags = familyFlags,
        Duration = new SpellDuration(600_000, 0, 600_000),
    });

    private readonly SpellTestKit _kit;
    private readonly Player _caster;
    private readonly Player _otherCaster;
    private readonly Player _target;

    public SpellSpecificExclusivityTests()
    {
        _kit = new SpellTestKit(
            Aspect(AspectOfTheHawk, "Aspect of the Hawk"),
            Aspect(AspectOfTheMonkey, "Aspect of the Monkey"),
            Sting(SerpentSting, "Serpent Sting"),
            Sting(ViperSting, "Viper Sting"),
            Armor(FrostArmor, "Frost Armor", 1UL << 25),
            Armor(MageArmor, "Mage Armor", 1UL << 28),
            Curse(CurseOfAgony, "Curse of Agony"),
            Curse(CurseOfTheElements, "Curse of the Elements"),
            Shield(LightningShield1, "Lightning Shield", 1UL << 10, 10),
            Shield(LightningShield2, "Lightning Shield", 1UL << 10, 20),
            Shield(ElementalShieldProc, "Elemental Shield", 0, 5));
        (_caster, _) = _kit.AddPlayer(1, 0, 0);
        (_target, _) = _kit.AddPlayer(2, 3, 0);
        (_otherCaster, _) = _kit.AddPlayer(3, -2, 0);
        _kit.System.Relations = new FakeRelations { Hostile = { _target.Guid } };
    }

    public void Dispose() => _kit.Dispose();

    private SpellAuraHolder? Holder(Unit unit, uint spell) => _kit.System.GetAuras(unit).FirstOrDefault(h => !h.IsRemoved && h.Spell.Id == spell);

    private void Cast(Unit caster, uint spell, Unit? target = null)
    {
        Assert.Equal(SpellCastResult.CastOk, _kit.System.CastSpell(caster, spell,
            target is null ? SpellCastTargets.ForSelf() : SpellCastTargets.ForUnit(target.Guid), triggered: true));
        _kit.Now++;
    }

    [Fact]
    public void AspectOfTheMonkey_ReplacesAspectOfTheHawk()
    {
        Cast(_caster, AspectOfTheHawk);
        Assert.NotNull(Holder(_caster, AspectOfTheHawk));

        Cast(_caster, AspectOfTheMonkey);

        Assert.Null(Holder(_caster, AspectOfTheHawk));
        Assert.NotNull(Holder(_caster, AspectOfTheMonkey));
    }

    [Fact]
    public void ASecondSting_FromTheSameHunter_ReplacesTheFirst()
    {
        Cast(_caster, SerpentSting, _target);
        Cast(_caster, ViperSting, _target);

        Assert.Null(Holder(_target, SerpentSting));
        Assert.NotNull(Holder(_target, ViperSting));
    }

    [Fact]
    public void Stings_FromTwoHunters_BothStay()
    {
        Cast(_caster, SerpentSting, _target);
        Cast(_otherCaster, ViperSting, _target);

        Assert.NotNull(Holder(_target, SerpentSting));
        Assert.NotNull(Holder(_target, ViperSting));
    }

    [Fact]
    public void AMageArmor_ReplacesTheOtherArmor()
    {
        Cast(_caster, FrostArmor);
        Cast(_caster, MageArmor);

        Assert.Null(Holder(_caster, FrostArmor));
        Assert.NotNull(Holder(_caster, MageArmor));
    }

    [Fact]
    public void ASecondCurse_FromTheSameWarlock_ReplacesTheFirst_ButCursesOfTwoWarlocksStay()
    {
        Cast(_caster, CurseOfAgony, _target);
        Cast(_caster, CurseOfTheElements, _target);
        Assert.Null(Holder(_target, CurseOfAgony));
        Assert.NotNull(Holder(_target, CurseOfTheElements));

        Cast(_otherCaster, CurseOfAgony, _target);

        Assert.NotNull(Holder(_target, CurseOfTheElements));      // the first warlock's curse stays beside the second's
        Assert.NotNull(Holder(_target, CurseOfAgony));
    }

    [Fact]
    public void AnElementalShield_ReplacesTheOtherShield_AndALowerRankNeverReplacesAHigherOne()
    {
        Cast(_caster, LightningShield1);
        Cast(_caster, ElementalShieldProc);
        Assert.Null(Holder(_caster, LightningShield1));
        Assert.NotNull(Holder(_caster, ElementalShieldProc));

        Cast(_caster, LightningShield2);
        Assert.Null(Holder(_caster, ElementalShieldProc));
        Assert.NotNull(Holder(_caster, LightningShield2));

        Cast(_caster, LightningShield1);                           // "cannot remove higher rank": refused, nothing is removed
        Assert.Null(Holder(_caster, LightningShield1));
        Assert.NotNull(Holder(_caster, LightningShield2));
    }

    [Fact]
    public void RestoredAuras_GoThroughTheSameRule_SoOnlyTheLaterOfTwoAspectsIsKept()
    {
        // vmangos Player::_LoadAuras adds the saved holders through Unit::AddSpellAuraHolder; so does SpellSystem.RestoreAuras.
        PersistedAura Saved(uint spell) => new()
        {
            SpellId = spell,
            CasterGuid = _caster.Guid,
            CasterLevel = 60,
            MaxDurationMs = 1_800_000,
            RemainingMs = 1_000_000,
            EffectMask = 1,
            Amounts = [10, 0, 0],
            SavedAtUnixMs = 1_000,
        };

        IReadOnlyList<SpellAuraHolder> restored = _kit.System.RestoreAuras(_caster, [Saved(AspectOfTheHawk), Saved(AspectOfTheMonkey)], 2_000);

        Assert.Null(Holder(_caster, AspectOfTheHawk));
        Assert.NotNull(Holder(_caster, AspectOfTheMonkey));
        Assert.Single(restored, h => !h.IsRemoved);
    }

    [Fact]
    public void Classification_FollowsGetSpellSpecific()
    {
        Assert.Equal(SpellSpecific.Aspect, SpellSpecifics.Of(Aspect(AspectOfTheHawk, "Aspect of the Hawk")));
        Assert.Equal(SpellSpecific.Normal, SpellSpecifics.Of(Aspect(AutoShot, "Auto Shot")));             // SpellEntry.cpp:108 excludes Auto Shot
        Assert.Equal(SpellSpecific.Aspect, SpellSpecifics.Of(Spell(13161, Effect(SpellEffectName.ApplyAura, 1, aura: AuraType.Dummy)))); // Aspect of the Beast, generic family
        Assert.Equal(SpellSpecific.Sting, SpellSpecifics.Of(Sting(SerpentSting, "Serpent Sting")));
        Assert.Equal(SpellSpecific.MageArmor, SpellSpecifics.Of(Armor(MageArmor, "Mage Armor", 1UL << 28)));
        Assert.Equal(SpellSpecific.Curse, SpellSpecifics.Of(Curse(CurseOfAgony, "Curse of Agony")));
        Assert.Equal(SpellSpecific.ElementalShield, SpellSpecifics.Of(Shield(ElementalShieldProc, "Elemental Shield", 0, 5)));
        Assert.Equal(SpellSpecific.WarlockArmor, SpellSpecifics.Of(Spell(687, Effect(SpellEffectName.ApplyAura, 1, aura: AuraType.Dummy)) with { SpellVisual = 130, SpellIconId = 89 }));
        Assert.Equal(SpellSpecific.Normal, SpellSpecifics.Of(Spell(1, Effect(SpellEffectName.ApplyAura, 1, aura: AuraType.Dummy))));
    }

    [Fact]
    public void Predicates_FollowTheVmangosTables()
    {
        Assert.True(SpellSpecifics.IsSinglePerTargetPerCaster(SpellSpecific.Sting, SpellSpecific.Sting));
        Assert.False(SpellSpecifics.IsSinglePerTargetPerCaster(SpellSpecific.MageArmor, SpellSpecific.MageArmor));
        Assert.True(SpellSpecifics.IsSinglePerTarget(SpellSpecific.MageArmor, SpellSpecific.MageArmor));
        Assert.False(SpellSpecifics.IsSinglePerTarget(SpellSpecific.Sting, SpellSpecific.Sting));
        Assert.True(SpellSpecifics.IsSingleRanksPerTarget(SpellSpecific.Curse, SpellSpecific.Curse));
        Assert.False(SpellSpecifics.IsSingleRanksPerTarget(SpellSpecific.Sting, SpellSpecific.Sting));
        Assert.True(SpellSpecifics.IsSinglePerTarget(SpellSpecific.Food, SpellSpecific.FoodAndDrink));
        Assert.False(SpellSpecifics.IsSinglePerTarget(SpellSpecific.Food, SpellSpecific.Drink));
        Assert.False(SpellSpecifics.IsSinglePerTarget(SpellSpecific.WellFed, SpellSpecific.Food));
    }
}

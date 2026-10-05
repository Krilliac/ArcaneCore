using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Spells.Rules.Application;
using ArcaneCore.Game.Spells.Rules.Immunity;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Game.Totems;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.ClassSpells.Totems;

/// <summary>Totem.cpp:180-217 at vmangos 0e3ff01: intrinsic per-effect rules, including the exceptions preceding Creature's rules.</summary>
public sealed class TotemImmunityTests
{
    private const uint Incoming = 931000;

    [Theory]
    [InlineData(SpellEffectName.AttackMe)]
    [InlineData(SpellEffectName.Heal)]
    [InlineData(SpellEffectName.HealMaxHealth)]
    [InlineData(SpellEffectName.HealMechanical)]
    [InlineData(SpellEffectName.Energize)]
    public void ForeignDirectRegenerationAndTaunt_DropTheirEffect(SpellEffectName effect)
    {
        using var kit = new TotemKit();
        (Player owner, Creature totem) = Summon(kit);
        SpellInfo spell = IncomingSpell(Effect(effect, 10, SpellImplicitTarget.UnitFriend));

        Assert.Equal(0, ApplyRule(kit, owner, totem, spell, 1));
    }

    [Theory]
    [InlineData(AuraType.PeriodicHeal)]
    [InlineData(AuraType.PeriodicEnergize)]
    [InlineData(AuraType.PeriodicHealthFunnel)]
    public void ForeignPositivePeriodicRegeneration_DropsItsEffect(AuraType aura)
    {
        using var kit = new TotemKit();
        (Player owner, Creature totem) = Summon(kit);
        SpellInfo spell = IncomingSpell(Effect(SpellEffectName.ApplyAura, 10, SpellImplicitTarget.UnitFriend, aura));

        Assert.Equal(0, ApplyRule(kit, owner, totem, spell, 1));
    }

    [Theory]
    [InlineData(SpellEffectName.ApplyAura)]
    [InlineData(SpellEffectName.ApplyAreaAuraParty)]
    [InlineData(SpellEffectName.ApplyAreaAuraPet)]
    [InlineData(SpellEffectName.ApplyAreaAuraRaid)]
    [InlineData(SpellEffectName.ApplyAreaAuraFriend)]
    [InlineData(SpellEffectName.ApplyAreaAuraEnemy)]
    public void ForeignNegativeAura_DropsAllAuraApplicationShapes(SpellEffectName effect)
    {
        using var kit = new TotemKit();
        (Player owner, Creature totem) = Summon(kit);
        SpellInfo spell = IncomingSpell(Effect(effect, 1, SpellImplicitTarget.UnitFriend, AuraType.Dummy))
            with { Attributes = SpellAttributes.AuraIsDebuff };

        Assert.Equal(0, ApplyRule(kit, owner, totem, spell, 1));
    }

    [Fact]
    public void MixedDamageAndDebuff_LandsDamageAndNeverAddsTheAura()
    {
        using var kit = new TotemKit();
        (Player owner, Creature totem) = Summon(kit);
        totem.MaxHealth = totem.Health = 100;
        kit.Spells.ApplicationRules.Add(new ImmunityApplicationRule());
        kit.Spells.CombatRules = SpellCombatRules.Neutral;
        kit.Relations.Hostile.Add(totem.Guid);
        AddIncoming(kit, IncomingSpell(
            Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy),
            Effect(SpellEffectName.ApplyAura, 1, SpellImplicitTarget.UnitEnemy, AuraType.Dummy)));

        Assert.Equal(SpellCastResult.CastOk, kit.Spells.CastSpell(owner, Incoming, SpellCastTargets.ForUnit(totem.Guid), true));

        Assert.Equal(90u, totem.Health);
        Assert.False(kit.Spells.HasAura(totem, Incoming));
    }

    [Fact]
    public void ForeignHealIsBlocked_WhileSelfHealLands()
    {
        using var kit = new TotemKit();
        (Player owner, Creature totem) = Summon(kit);
        totem.MaxHealth = 100;
        totem.Health = 30;
        kit.Spells.ApplicationRules.Add(new ImmunityApplicationRule());
        kit.Spells.CombatRules = SpellCombatRules.Neutral;
        AddIncoming(kit, IncomingSpell(Effect(SpellEffectName.Heal, 10, SpellImplicitTarget.UnitFriend)));

        Assert.Equal(SpellCastResult.CastOk, kit.Spells.CastSpell(owner, Incoming, SpellCastTargets.ForUnit(totem.Guid), true));
        Assert.Equal(30u, totem.Health);
        Assert.Equal(SpellCastResult.CastOk, kit.Spells.CastSpell(totem, Incoming, SpellCastTargets.ForUnit(totem.Guid), true));
        Assert.Equal(40u, totem.Health);
    }

    [Theory]
    [InlineData(0x2000UL)]
    [InlineData(0x4000UL)]
    [InlineData(0x4000000UL)]
    public void ShamanStreamSpringAndTideFamilies_AllowForeignRegeneration(ulong flag)
    {
        using var kit = new TotemKit();
        (Player owner, Creature totem) = Summon(kit);
        totem.MaxHealth = 100;
        totem.Health = 30;
        kit.Spells.ApplicationRules.Add(new ImmunityApplicationRule());
        kit.Spells.CombatRules = SpellCombatRules.Neutral;
        AddIncoming(kit, IncomingSpell(Effect(SpellEffectName.Heal, 10, SpellImplicitTarget.UnitFriend))
            with { SpellFamilyName = 11, SpellFamilyFlags = flag });

        Assert.Equal(SpellCastResult.CastOk, kit.Spells.CastSpell(owner, Incoming, SpellCastTargets.ForUnit(totem.Guid), true));
        Assert.Equal(40u, totem.Health);
    }

    [Fact]
    public void ExceptionsBypassCreatureEffectImmunity_ButOtherFamiliesAndBitsDoNot()
    {
        using var kit = new TotemKit();
        (Player owner, Creature totem) = Summon(kit);
        kit.Spells.CreatureImmunities = new MechanicProvider();
        SpellInfo spell = IncomingSpell(Effect(SpellEffectName.Dummy, 1) with { Mechanic = 1 });

        Assert.Equal(0, ApplyRule(kit, owner, totem, spell, 1));
        Assert.Equal(1, ApplyRule(kit, totem, totem, spell, 1));
        Assert.Equal(1, ApplyRule(kit, owner, totem, spell with { SpellFamilyName = 11, SpellFamilyFlags = 0x4000 }, 1));
        Assert.Equal(0, ApplyRule(kit, owner, totem, spell with { SpellFamilyName = 10, SpellFamilyFlags = 0x4000 }, 1));
        Assert.Equal(0, ApplyRule(kit, owner, totem, spell with { SpellFamilyName = 11, SpellFamilyFlags = 0x8000 }, 1));
    }

    [Fact]
    public void IntrinsicTotemImmunityPrecedesIgnoreFlags_AndCanBeDisabledByDeveloperOption()
    {
        using var kit = new TotemKit();
        (Player owner, Creature totem) = Summon(kit);
        SpellInfo spell = IncomingSpell(Effect(SpellEffectName.Heal, 10, SpellImplicitTarget.UnitFriend)) with
        {
            Attributes = (SpellAttributes)SpellRuleFlags.NoImmunities,
            AttributesEx3 = SpellRuleFlags.Ex3IgnoreCasterAndTargetRestrictions,
        };

        Assert.Equal(0, ApplyRule(kit, owner, totem, spell, 1));
        kit.Spells.ImmunityEnforcement = false;
        Assert.Equal(1, ApplyRule(kit, owner, totem, spell, 1));
    }

    [Fact]
    public void OrdinaryCreaturesAndNonRegenerationBuffs_RetainExistingBehavior()
    {
        using var kit = new TotemKit();
        (Player owner, Creature totem) = Summon(kit);
        Creature ordinary = kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(TotemKit.EarthEntry)!, owner.X + 3, owner.Y, owner.Z, 0);
        SpellInfo heal = IncomingSpell(Effect(SpellEffectName.Heal, 10, SpellImplicitTarget.UnitFriend));
        SpellInfo buff = IncomingSpell(Effect(SpellEffectName.ApplyAura, 10, SpellImplicitTarget.UnitFriend, AuraType.ModStat));

        Assert.False(TotemQuery.IsTotem(ordinary));
        Assert.Equal(1, ApplyRule(kit, owner, ordinary, heal, 1));
        Assert.Equal(1, ApplyRule(kit, owner, totem, buff, 1));
        // The reference regeneration list is periodic heal/energize/health funnel, not MOD_REGEN.
        Assert.Equal(1, ApplyRule(kit, owner, totem,
            buff with { Effects = [buff.Effects[0] with { AuraType = AuraType.ModRegen }] }, 1));
        kit.Spells.CreatureImmunities = new MechanicProvider();
        SpellInfo ignoring = buff with
        {
            AttributesEx3 = SpellRuleFlags.Ex3IgnoreCasterAndTargetRestrictions,
            Effects = [buff.Effects[0] with { Mechanic = 1 }],
        };
        Assert.Equal(1, ApplyRule(kit, owner, ordinary, ignoring, 1));
    }

    private static (Player Owner, Creature Totem) Summon(TotemKit kit)
    {
        kit.Spells.Units = new MapUnitResolver();
        (Player owner, _) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(owner, TotemKit.EarthTotemSummon));
        return (owner, Assert.IsType<Creature>(kit.Totems.GetTotem(owner, TotemSlot.Earth)));
    }

    private static SpellInfo IncomingSpell(params SpellEffectInfo[] effects) => Spell(Incoming, effects) with
    {
        RangeIndex = 4, Range = new SpellRange(0, 30), Duration = new SpellDuration(10_000, 0, 10_000),
        StartRecoveryCategory = 0, StartRecoveryTime = 0,
    };

    private static void AddIncoming(TotemKit kit, SpellInfo spell)
        => kit.Spells.Store = new SpellStore([.. kit.Store.All, spell], [], []);

    private static int ApplyRule(TotemKit kit, Unit caster, Unit target, SpellInfo spell, int mask)
    {
        var cast = new SpellCast(spell, caster, SpellCastTargets.ForUnit(target.Guid), true, 0, 0, 0);
        var application = new SpellApplication(kit.Spells, cast, target, mask);
        new ImmunityApplicationRule().Begin(application);
        return application.EffectMask;
    }

    private sealed class MechanicProvider : ICreatureImmunityProvider
    {
        public uint MechanicImmuneMask(Unit unit) => 1;
        public uint SchoolImmuneMask(Unit unit) => 0;
    }

    private sealed class MapUnitResolver : ISpellUnitResolver
    {
        public Unit? Find(Unit reference, ObjectGuid guid)
            => reference.Guid == guid ? reference : reference.Map?.FindObject(guid) as Unit;
    }
}

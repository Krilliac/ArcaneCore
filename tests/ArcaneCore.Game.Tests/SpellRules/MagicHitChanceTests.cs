using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Tests.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.SpellRules;

/// <summary>
/// Magic spell hit chance, re-derived from vmangos SpellCaster::MagicSpellHitResult / MagicSpellHitChance
/// (SpellCaster.cpp:772-880). Each test names the modifier it pins.
/// </summary>
public sealed class MagicHitChanceTests
{
    private const uint Bolt = 900_001;
    private const uint StunBolt = 900_002;
    private const uint SnareDebuff = 900_003;
    private const uint Aoe = 900_004;
    private const uint AlwaysHit = 900_005;
    private const uint NoResist = 900_006;
    private const uint Binary = 900_007;
    private const uint MechanicResist = 900_010;
    private const uint DebuffResist = 900_011;
    private const uint AttackerHit = 900_012;
    private const uint AoeAvoid = 900_013;
    private const uint CasterHit = 900_014;

    private static SpellTestKit Kit() => new(
        RuleTestSupport.Magic(Bolt),
        RuleTestSupport.Magic(StunBolt) with { Mechanic = (uint)SpellMechanic.Stun },
        RuleTestSupport.Magic(SnareDebuff) with { Dispel = (uint)DispelType.Magic },
        RuleTestSupport.Magic(Aoe) with
        {
            Effects = [SpellTestKit.Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy, targetB: SpellImplicitTarget.EnumUnitsEnemyAoeAtDestLoc)],
        },
        RuleTestSupport.Magic(AlwaysHit) with { AttributesEx3 = SpellRuleFlags.Ex3AlwaysHit },
        RuleTestSupport.Magic(NoResist) with { AttributesEx4 = SpellRuleFlags.Ex4IgnoreResistances },
        RuleTestSupport.Magic(Binary) with
        {
            Effects = [SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.ModStun)],
        },
        RuleTestSupport.Grant(MechanicResist, AuraType.ModMechanicResistance, 25, (int)SpellMechanic.Stun),
        RuleTestSupport.Grant(DebuffResist, AuraType.ModDebuffResistance, 15, (int)DispelType.Magic),
        RuleTestSupport.Grant(AttackerHit, AuraType.ModAttackerSpellHitChance, -10, 1 << (int)SpellSchool.Frost),
        RuleTestSupport.Grant(AoeAvoid, AuraType.ModAoeAvoidance, 30),
        RuleTestSupport.Grant(CasterHit, AuraType.ModSpellHitChance, 3));

    private static (Player Caster, Player Target) Players(SpellTestKit kit, byte casterLevel = 60, byte targetLevel = 60)
    {
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        caster.Level = casterLevel;
        target.Level = targetLevel;
        return (caster, target);
    }

    [Theory]
    [InlineData(60, 60, 96f)]
    [InlineData(60, 62, 94f)]
    [InlineData(60, 63, 83f)]  // creature target: 94 - 11
    [InlineData(60, 50, 106f)] // clamped to 99 only after every modifier
    [InlineData(1, 60, 22f)]   // vmangos floor (SpellCaster.cpp:829-834)
    [InlineData(10, 60, 22f)]
    public void BaseChance_FollowsTheLevelDifference_WithTheRetailFloor_AgainstCreatures(int casterLevel, int targetLevel, float expected)
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        caster.Level = (byte)casterLevel;
        Creature target = FoundationTests.MakeCreature(0, (byte)targetLevel);

        Assert.Equal(expected, VanillaSpellCombatRules.MagicHitChance(caster, target));
    }

    [Fact]
    public void BaseChance_UsesSevenPerLevelAgainstPlayers()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player target) = Players(kit, 60, 63);

        Assert.Equal(87f, VanillaSpellCombatRules.MagicHitChance(caster, target));
    }

    [Fact]
    public void Floor_CanBeLoweredToTheCmangosValue()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        caster.Level = 1;
        Creature target = FoundationTests.MakeCreature(0, 60);
        var options = new SpellRuleOptions { MagicHitFloorPercent = 1.0f };
        var rules = new VanillaSpellCombatRules { Options = options };

        Assert.Equal(1.0f, rules.MagicHitPercent(kit.System, caster, target, kit.Store.Get(Bolt)!));
    }

    [Fact]
    public void WorldBossVictim_CountsAsCasterLevelPlusThree()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        caster.Level = 60;
        Creature boss = FoundationTests.MakeCreature(3, 60);
        Creature elite = FoundationTests.MakeCreature(1, 60);

        Assert.Equal(83f, VanillaSpellCombatRules.MagicHitChance(caster, boss)); // 94 - (3 - 2) * 11
        Assert.Equal(96f, VanillaSpellCombatRules.MagicHitChance(caster, elite));
    }

    [Fact]
    public void WorldBossCaster_SeesItsVictimThreeLevelsBelowItself()
    {
        using SpellTestKit kit = Kit();
        (Player victim, _) = kit.AddPlayer(1);
        victim.Level = 60;
        Creature boss = FoundationTests.MakeCreature(3, 60);

        Assert.Equal(99f, VanillaSpellCombatRules.MagicHitChance(boss, victim)); // 96 - (-3)
    }

    [Fact]
    public void FinalChance_IsClampedToOneToNinetyNine()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player target) = Players(kit, 60, 50);
        var rules = new VanillaSpellCombatRules();

        Assert.Equal(99f, rules.MagicHitPercent(kit.System, caster, target, kit.Store.Get(Bolt)!));
    }

    [Fact]
    public void VictimMechanicResistance_LowersOnlySpellsWithThatMechanic()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player target) = Players(kit);
        RuleTestSupport.Apply(kit, target, MechanicResist);
        var rules = new VanillaSpellCombatRules();

        Assert.Equal(71f, rules.MagicHitPercent(kit.System, caster, target, kit.Store.Get(StunBolt)!)); // 96 - 25
        Assert.Equal(96f, rules.MagicHitPercent(kit.System, caster, target, kit.Store.Get(Bolt)!));
    }

    [Fact]
    public void VictimDebuffResistance_LowersOnlySpellsOfThatDispelType()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player target) = Players(kit);
        RuleTestSupport.Apply(kit, target, DebuffResist);
        var rules = new VanillaSpellCombatRules();

        Assert.Equal(81f, rules.MagicHitPercent(kit.System, caster, target, kit.Store.Get(SnareDebuff)!));
        Assert.Equal(96f, rules.MagicHitPercent(kit.System, caster, target, kit.Store.Get(Bolt)!));
    }

    [Fact]
    public void VictimAttackerSpellHitChance_AppliesBySchoolMask()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player target) = Players(kit);
        RuleTestSupport.Apply(kit, target, AttackerHit);
        var rules = new VanillaSpellCombatRules();

        Assert.Equal(86f, rules.MagicHitPercent(kit.System, caster, target, kit.Store.Get(Bolt)!)); // frost bolt
        Assert.Equal(96f, rules.MagicHitPercent(kit.System, caster, target, kit.Store.Get(Bolt)! with { School = SpellSchool.Fire }));
    }

    [Fact]
    public void AoeAvoidance_LowersOnlyAreaSpells()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player target) = Players(kit);
        RuleTestSupport.Apply(kit, target, AoeAvoid);
        var rules = new VanillaSpellCombatRules();

        Assert.Equal(66f, rules.MagicHitPercent(kit.System, caster, target, kit.Store.Get(Aoe)!));
        Assert.Equal(96f, rules.MagicHitPercent(kit.System, caster, target, kit.Store.Get(Bolt)!));
    }

    [Fact]
    public void CasterSpellHitChance_IsAddedAfterTheVictimsModifiers()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player target) = Players(kit, 60, 62);
        RuleTestSupport.Apply(kit, caster, CasterHit);
        var rules = new VanillaSpellCombatRules();

        Assert.Equal(97f, rules.MagicHitPercent(kit.System, caster, target, kit.Store.Get(Bolt)!)); // 94 + 3
    }

    [Fact]
    public void AlwaysHit_IsAHundredPercent_EvenFromLevelOneAgainstABoss()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        caster.Level = 1;
        Creature boss = FoundationTests.MakeCreature(3, 63);
        var rules = new VanillaSpellCombatRules();
        kit.System.Random = new ScriptedRandom(9999);

        Assert.Equal(100f, rules.MagicHitPercent(kit.System, caster, boss, kit.Store.Get(AlwaysHit)!));
        Assert.Equal(SpellMissInfo.None, rules.RollHit(kit.System, caster, boss, kit.Store.Get(AlwaysHit)!));
    }

    [Fact]
    public void IgnoreResistances_NeverMisses()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        caster.Level = 1;
        Creature boss = FoundationTests.MakeCreature(3, 63);
        var rules = new VanillaSpellCombatRules();
        kit.System.Random = new ScriptedRandom(0, 0);

        Assert.Equal(SpellMissInfo.None, rules.RollHit(kit.System, caster, boss, kit.Store.Get(NoResist)!));
        Assert.Equal(SpellMissInfo.Resist, rules.RollHit(kit.System, caster, boss, kit.Store.Get(Bolt)!)); // roll 0 < miss bound
    }

    [Fact]
    public void BinarySpells_HitChanceIsScaledByTheVictimsResistChance()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player target) = Players(kit);
        target.SetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Frost, 150); // 150 * 0.15 / 60 = 0.375
        var rules = new VanillaSpellCombatRules();

        Assert.Equal(60f, rules.MagicHitPercent(kit.System, caster, target, kit.Store.Get(Binary)!), 3); // 96 * (1 - 0.375)
        Assert.Equal(96f, rules.MagicHitPercent(kit.System, caster, target, kit.Store.Get(Bolt)!));      // non-binary: unchanged
    }

    [Fact]
    public void ResistMissChanceSpellMod_IsAppliedAfterTheFloor()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player target) = Players(kit);
        var rules = new VanillaSpellCombatRules { Modifiers = new AddModifier(SpellModOp.ResistMissChance, -20f) };

        Assert.Equal(76f, rules.MagicHitPercent(kit.System, caster, target, kit.Store.Get(Bolt)!));
    }

    [Fact]
    public void RollHit_MissesAboutAsOftenAsTheChanceSays()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        caster.Level = 60;
        Creature target = FoundationTests.MakeCreature(0, 63); // 83% hit
        var rules = new VanillaSpellCombatRules();

        int resisted = Enumerable.Range(0, 4000).Count(_ => rules.RollHit(kit.System, caster, target, kit.Store.Get(Bolt)!) == SpellMissInfo.Resist);

        Assert.InRange(resisted, 560, 800); // 17% of 4000 = 680
    }

    internal sealed class AddModifier(SpellModOp op, float delta) : ISpellModifiers
    {
        public float Apply(Unit caster, SpellInfo spell, SpellModOp operation, float value) => operation == op ? value + delta : value;
    }
}

using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.CombatMechanics;

/// <summary>
/// S03 power economy against vmangos: Player::RewardRage (Player.cpp:2243-2267), Player::Regenerate and
/// RegenerateAll (Player.cpp:2269-2331), the 82% refund (Spell.cpp:1267-1285), the rate defaults
/// (mangosd.conf.dist.in:2793-2799) and World::setConfigPos (World.cpp:2959-2967).
/// </summary>
public sealed class PowerRulesTests
{
    private const int RageIndex = 1;
    private const int EnergyIndex = 3;

    private sealed class FakeAuras : IPowerAuraSource
    {
        public HashSet<(uint Spell, int Effect)> Auras { get; } = [];

        public HashSet<AuraType> Types { get; } = [];

        public float RegenFactor { get; set; } = 1.0f;

        public bool HasAura(Unit unit, uint spellId, int effectIndex) => Auras.Contains((spellId, effectIndex));

        public bool HasAuraType(Unit unit, AuraType type) => Types.Contains(type);

        public float GetPowerRegenFactor(Unit unit, PowerType power) => power == PowerType.Mana ? 1.0f : RegenFactor;
    }

    // --- rage from damage ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(10, 100u, true, false, 1.0f, 200u)]
    [InlineData(10, 100u, false, false, 1.0f, 66u)]
    [InlineData(10, 100u, false, true, 1.0f, 86u)]
    [InlineData(30, 200u, true, false, 1.0f, 137u)]
    [InlineData(30, 200u, false, false, 1.0f, 45u)]
    [InlineData(30, 200u, false, true, 1.0f, 59u)]
    [InlineData(60, 1000u, true, false, 1.0f, 325u)]
    [InlineData(60, 1000u, false, false, 1.0f, 108u)]
    [InlineData(60, 1000u, false, true, 1.0f, 140u)]
    [InlineData(60, 1000u, true, true, 1.0f, 325u)]      // Berserker Rage only scales rage TAKEN
    [InlineData(60, 1000u, true, false, 2.0f, 650u)]     // Rate.Rage.Income
    [InlineData(60, 1000u, false, true, 0.5f, 70u)]      // the rate multiplies after the 1.3
    public void RageFromDamage_FollowsKalgansFormula_X10(int level, uint damage, bool dealt, bool berserker, float rate, uint expectedRaw)
        => Assert.Equal(expectedRaw, PowerRules.RageFromDamage((byte)level, damage, dealt, berserker, rate));

    [Fact]
    public void BerserkerRage_AppliesOnlyWithAura18499OnEffectZero_WhenRageIsTaken()
    {
        (WorldRuntime world, _, _, _) = CombatTestKit.CreateWorld();
        using WorldRuntime w = world;
        Player player = CombatTestKit.AddPlayer(world, 1, 0, 0, new FakeSession(1), level: 60);
        SetMaxRage(player);

        var auras = new FakeAuras();
        var environment = new PowerEnvironment(new CombatOptions(), auras);

        MapCombat.RewardRage(player, 1000, attacker: false, environment);
        Assert.Equal(108u, Rage(player));   // no aura

        auras.Auras.Add((18499, 1));        // the aura on the wrong effect index does not count (HasAura(18499, EFFECT_INDEX_0))
        MapCombat.RewardRage(player, 1000, attacker: false, environment);
        Assert.Equal(216u, Rage(player));

        auras.Auras.Add((18499, 0));
        MapCombat.RewardRage(player, 1000, attacker: false, environment);
        Assert.Equal(356u, Rage(player));   // +140

        MapCombat.RewardRage(player, 1000, attacker: true, environment);
        Assert.Equal(681u, Rage(player));   // +325: no Berserker bonus on rage dealt
    }

    [Fact]
    public void RewardRage_WithoutAnEnvironment_UsesTheRetailDefaults()
    {
        (WorldRuntime world, _, _, _) = CombatTestKit.CreateWorld();
        using WorldRuntime w = world;
        Player player = CombatTestKit.AddPlayer(world, 1, 0, 0, new FakeSession(1), level: 60);
        SetMaxRage(player);

        MapCombat.RewardRage(player, 1000, attacker: true);

        Assert.Equal(325u, Rage(player));
    }

    [Fact]
    public void RewardRage_ScalesWithTheWorldsRageIncomeRate()
    {
        (WorldRuntime world, _, _, _) = CombatTestKit.CreateWorld();
        using WorldRuntime w = world;
        Player player = CombatTestKit.AddPlayer(world, 1, 0, 0, new FakeSession(1), level: 60);
        SetMaxRage(player);

        MapCombat.RewardRage(player, 1000, attacker: true, new PowerEnvironment(new CombatOptions { RateRageIncome = 2.0f }, null));

        Assert.Equal(650u, Rage(player));
    }

    // --- regeneration --------------------------------------------------------------------------------------

    [Fact]
    public void Defaults_AreTheRetailRates()
    {
        var options = new CombatOptions();
        Assert.Equal(1.0f, options.RateRageIncome);
        Assert.Equal(1.0f, options.RateRageLoss);
        Assert.Equal(1.0f, options.RateEnergy);
        Assert.Equal(1.0f, options.RateMana);
        Assert.Equal("Combat", CombatOptions.SectionName);
    }

    [Fact]
    public void Normalize_ReplacesNegativeManaAndRageLossRates_ButNotIncomeOrEnergy()
    {
        var options = new CombatOptions { RateMana = -1, RateRageLoss = -2, RateRageIncome = -3, RateEnergy = -4 };

        IReadOnlyList<string> replaced = options.Normalize();

        Assert.Equal(["Rate.Mana", "Rate.Rage.Loss"], replaced);
        Assert.Equal(1.0f, options.RateMana);
        Assert.Equal(1.0f, options.RateRageLoss);
        Assert.Equal(-3.0f, options.RateRageIncome);   // vmangos reads these two with plain setConfig
        Assert.Equal(-4.0f, options.RateEnergy);
    }

    [Fact]
    public void RageDecay_Is20RawPerTick_ScaledByTheLossRateAndRegenAuras()
    {
        Assert.Equal(20u, PowerRules.RageDecayPerTick(1.0f, 1.0f));
        Assert.Equal(40u, PowerRules.RageDecayPerTick(2.0f, 1.0f));
        Assert.Equal(30u, PowerRules.RageDecayPerTick(1.0f, 1.5f));
        Assert.Equal(10u, PowerRules.RageDecayPerTick(0.5f, 1.0f));
        Assert.Equal(40u, PowerRules.EnergyPerTick(2.0f, 1.0f));
        Assert.Equal(75u, PowerRules.ManaPerTick(18.75f, 2.0f));
    }

    [Fact]
    public void Regen_DecaysRageByTheLossRate_OutOfCombat()
    {
        (WorldRuntime world, _, _, _) = CombatTestKit.CreateWorld();
        using WorldRuntime w = world;
        PowerEnvironment.Register(world, new PowerEnvironment(new CombatOptions { RateRageLoss = 2.0f }, null));
        Player player = CombatTestKit.AddPlayer(world, 1, 0, 0, new FakeSession(1));
        player.SetUInt32(UpdateFields.UnitFieldPower1 + RageIndex, 100);

        world.RunTick(50);

        Assert.Equal(60u, Rage(player));
    }

    [Fact]
    public void Regen_RageDecay_IsSuppressedByInterruptRegen_ButHealthStillRegenerates()
    {
        (WorldRuntime world, _, _, _) = CombatTestKit.CreateWorld();
        using WorldRuntime w = world;
        var auras = new FakeAuras();
        auras.Types.Add(AuraType.InterruptRegen);   // Bloodrage
        PowerEnvironment.Register(world, new PowerEnvironment(new CombatOptions(), auras));
        Player player = CombatTestKit.AddPlayer(world, 1, 0, 0, new FakeSession(1));
        player.SetUInt32(UpdateFields.UnitFieldPower1 + RageIndex, 100);
        player.SetUInt32(UpdateFields.UnitFieldStat0 + 4, 30);
        player.Health = 500;

        world.RunTick(50);

        Assert.Equal(100u, Rage(player));
        Assert.Equal(515u, player.Health);
    }

    [Fact]
    public void Regen_RageDecay_FollowsModPowerRegenPercentAuras()
    {
        (WorldRuntime world, _, _, _) = CombatTestKit.CreateWorld();
        using WorldRuntime w = world;
        PowerEnvironment.Register(world, new PowerEnvironment(new CombatOptions(), new FakeAuras { RegenFactor = 1.5f }));
        Player player = CombatTestKit.AddPlayer(world, 1, 0, 0, new FakeSession(1));
        player.SetUInt32(UpdateFields.UnitFieldPower1 + RageIndex, 100);

        world.RunTick(50);

        Assert.Equal(70u, Rage(player));
    }

    [Fact]
    public void Regen_EnergyGains20RawPerTick_TimesTheEnergyRate()
    {
        (WorldRuntime world, _, _, _) = CombatTestKit.CreateWorld();
        using WorldRuntime w = world;
        PowerEnvironment.Register(world, new PowerEnvironment(new CombatOptions { RateEnergy = 2.0f }, null));
        Player player = CombatTestKit.AddPlayer(world, 1, 0, 0, new FakeSession(1));
        player.SetUInt32(UpdateFields.UnitFieldMaxpower1 + EnergyIndex, 100);
        player.SetUInt32(UpdateFields.UnitFieldPower1 + EnergyIndex, 0);

        world.RunTick(50);

        Assert.Equal(40u, player.GetUInt32(UpdateFields.UnitFieldPower1 + EnergyIndex));
    }

    [Fact]
    public void Regen_RageDoesNotDecay_InCombat()
    {
        (WorldRuntime world, Map map, _, _) = CombatTestKit.CreateWorld();
        using WorldRuntime w = world;
        Player player = CombatTestKit.AddPlayer(world, 1, 0, 0, new FakeSession(1));
        player.SetUInt32(UpdateFields.UnitFieldPower1 + RageIndex, 100);
        map.Combat.SetInCombatState(player, 60000);

        world.RunTick(50);

        Assert.Equal(100u, Rage(player));
    }

    // --- refund --------------------------------------------------------------------------------------------

    private static SpellInfo Ability(PowerType power, uint cost, bool discount) => new()
    {
        Id = 1,
        PowerType = (int)power,
        ManaCost = cost,
        AttributesEx = discount ? (SpellAttributesEx)(uint)SpellAttributesExCombat.DiscountPowerOnMiss : 0,
        Effects = [],
    };

    [Theory]
    [InlineData(PowerType.Rage, SpellMissInfo.Dodge, 123)]
    [InlineData(PowerType.Rage, SpellMissInfo.Parry, 123)]
    [InlineData(PowerType.Rage, SpellMissInfo.Miss, null)]
    [InlineData(PowerType.Rage, SpellMissInfo.Immune, null)]
    [InlineData(PowerType.Rage, SpellMissInfo.Block, null)]
    [InlineData(PowerType.Rage, SpellMissInfo.None, null)]
    [InlineData(PowerType.Energy, SpellMissInfo.Miss, 123)]
    [InlineData(PowerType.Energy, SpellMissInfo.Dodge, 123)]
    [InlineData(PowerType.Energy, SpellMissInfo.Parry, 123)]
    [InlineData(PowerType.Energy, SpellMissInfo.Immune, 123)]
    [InlineData(PowerType.Energy, SpellMissInfo.Immune2, 123)]
    [InlineData(PowerType.Energy, SpellMissInfo.Resist, null)]
    [InlineData(PowerType.Energy, SpellMissInfo.None, null)]
    [InlineData(PowerType.Mana, SpellMissInfo.Dodge, null)]
    public void Refund_Is82PercentOfTheCost_OnTheRetailOutcomes(PowerType power, SpellMissInfo miss, int? expected)
    {
        (PowerType Power, int Amount)? refund = PowerRules.Refund(Ability(power, 150, discount: true), miss, 150);

        if (expected is null)
        {
            Assert.Null(refund);
        }
        else
        {
            Assert.Equal((power, expected.Value), refund);
        }
    }

    [Fact]
    public void Refund_OnlyForDiscountPowerOnMissSpells()
        => Assert.Null(PowerRules.Refund(Ability(PowerType.Rage, 150, discount: false), SpellMissInfo.Dodge, 150));

    [Fact]
    public void Refund_RoundsHalfAwayFromZero_LikeLroundf()
    {
        // 25 * 0.82 = 20.5 -> 21 (lroundf); 5 * 0.82 = 4.1 -> 4.
        Assert.Equal(21, PowerRules.Refund(Ability(PowerType.Rage, 25, true), SpellMissInfo.Dodge, 25)!.Value.Amount);
        Assert.Equal(4, PowerRules.Refund(Ability(PowerType.Rage, 5, true), SpellMissInfo.Dodge, 5)!.Value.Amount);
    }

    private const uint RageAbility = 920001;
    private const uint EnergyAbility = 920002;
    private const uint PlainAbility = 920003;

    private static SpellInfo Strike(uint id, PowerType power, bool discount) => Spell(id, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy)) with
    {
        PowerType = (int)power,
        ManaCost = 150,
        RangeIndex = 4,
        Range = new SpellRange(0, 30),
        AttributesEx = discount ? (SpellAttributesEx)(uint)SpellAttributesExCombat.DiscountPowerOnMiss : 0,
    };

    private static (SpellTestKit Kit, Player Caster, Player Target, FixedRules Rules) RefundKit()
    {
        var kit = new SpellTestKit(
            Strike(RageAbility, PowerType.Rage, discount: true),
            Strike(EnergyAbility, PowerType.Energy, discount: true),
            Strike(PlainAbility, PowerType.Rage, discount: false));
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 3, 0);
        caster.SetUInt32(UpdateFields.UnitFieldMaxpower1 + RageIndex, 1000);
        caster.SetUInt32(UpdateFields.UnitFieldMaxpower1 + EnergyIndex, 1000);
        SpellSystem.SetPower(caster, PowerType.Rage, 400);
        SpellSystem.SetPower(caster, PowerType.Energy, 400);
        var rules = new FixedRules();
        kit.System.CombatRules = rules;
        kit.System.RegisterObserver(new PowerRefundObserver());
        return (kit, caster, target, rules);
    }

    [Fact]
    public void Observer_RefundsRage_OnADodge_AfterTheCostIsTaken()
    {
        (SpellTestKit kit, Player caster, Player target, FixedRules rules) = RefundKit();
        using (kit)
        {
            rules.Miss = SpellMissInfo.Dodge;

            kit.System.CastSpell(caster, RageAbility, SpellCastTargets.ForUnit(target.Guid), triggered: false);

            Assert.Equal(373u, SpellSystem.GetPower(caster, PowerType.Rage));   // 400 - 150 + round(150 * 0.82)
        }
    }

    [Fact]
    public void Observer_DoesNotRefundRage_OnAMiss_ButRefundsEnergy()
    {
        (SpellTestKit kit, Player caster, Player target, FixedRules rules) = RefundKit();
        using (kit)
        {
            rules.Miss = SpellMissInfo.Miss;

            kit.System.CastSpell(caster, RageAbility, SpellCastTargets.ForUnit(target.Guid), triggered: false);
            Assert.Equal(250u, SpellSystem.GetPower(caster, PowerType.Rage));

            kit.Advance(1500);
            kit.System.CastSpell(caster, EnergyAbility, SpellCastTargets.ForUnit(target.Guid), triggered: false);
            Assert.Equal(373u, SpellSystem.GetPower(caster, PowerType.Energy));
        }
    }

    [Fact]
    public void Observer_NoRefund_OnAHit_OrForSpellsWithoutTheFlag()
    {
        (SpellTestKit kit, Player caster, Player target, FixedRules rules) = RefundKit();
        using (kit)
        {
            kit.System.CastSpell(caster, RageAbility, SpellCastTargets.ForUnit(target.Guid), triggered: false);
            Assert.Equal(250u, SpellSystem.GetPower(caster, PowerType.Rage));

            kit.Advance(1500);
            rules.Miss = SpellMissInfo.Dodge;
            kit.System.CastSpell(caster, PlainAbility, SpellCastTargets.ForUnit(target.Guid), triggered: false);
            Assert.Equal(100u, SpellSystem.GetPower(caster, PowerType.Rage));
        }
    }

    private static void SetMaxRage(Player player) => player.SetUInt32(UpdateFields.UnitFieldMaxpower1 + RageIndex, 1000);

    private static uint Rage(Player player) => player.GetUInt32(UpdateFields.UnitFieldPower1 + RageIndex);
}

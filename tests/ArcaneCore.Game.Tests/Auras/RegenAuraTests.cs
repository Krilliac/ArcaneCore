using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Auras;

/// <summary>
/// Food (SPELL_AURA_MOD_REGEN) and drink (SPELL_AURA_MOD_POWER_REGEN) regenerate through the 2 s player tick, not as periodic
/// auras: vmangos Player::RegenerateHealth (Player.cpp:2340-2395), Player::Regenerate (Player.cpp:2292-2335) and
/// Player::UpdateManaRegen (StatSystem.cpp:642-660). Numbers come from Spell.dbc 1.12.1: Food (433) is ModRegen 17 with no
/// amplitude (61 health over 18 s), Drink (430) is ModPowerRegen 42 for mana (151 mana over 18 s, 8.4 mp5 per second tick).
/// </summary>
public sealed class RegenAuraTests
{
    private sealed class FakeAuras : IPowerAuraSource
    {
        public List<SpellAura> Live { get; } = [];

        public bool HasAura(Unit unit, uint spellId, int effectIndex) => false;

        public bool HasAuraType(Unit unit, AuraType type) => Live.Any(a => a.Type == type);

        public float GetPowerRegenFactor(Unit unit, PowerType power) => 1.0f;

        public IReadOnlyList<SpellAura> GetAuras(Unit unit, AuraType type) => [.. Live.Where(a => a.Type == type)];
    }

    private static (WorldRuntime World, Map Map, Player Player, FakeAuras Auras) Setup()
    {
        (WorldRuntime world, Map map, _, _) = CombatTestKit.CreateWorld();
        var auras = new FakeAuras();
        CombatEnvironment.Register(world, new CombatEnvironment(new CombatOptions(), auras));
        Player player = CombatTestKit.AddPlayer(world, 1, 0, 0, new FakeSession(1));
        player.SetUInt32(UpdateFields.UnitFieldStat0 + 4, 0); // spirit 0: warrior spirit regen is 0, so only the auras regenerate
        return (world, map, player, auras);
    }

    private static void Ticks(WorldRuntime world, int count)
    {
        world.RunTick(50); // the first regen tick comes at once, then every 2 s
        for (int i = 1; i < count; i++)
        {
            world.RunTick(2000);
        }
    }

    [Fact]
    public void EatingFood433_OutOfCombat_RestoresSixtyOneHealthIn18Seconds()
    {
        (WorldRuntime world, _, Player player, FakeAuras auras) = Setup();
        using WorldRuntime w = world;
        player.Health = 100;
        auras.Live.Add(new SpellAura(0, AuraType.ModRegen, 17, 0, 0));

        Ticks(world, 9); // 9 ticks of 17 * 2000 / 5000 = 6.8, fractions carried

        Assert.Equal(161u, player.Health);
    }

    [Fact]
    public void FoodWithAnAmplitude_UsesItAsThePeriod()
    {
        (WorldRuntime world, _, Player player, FakeAuras auras) = Setup();
        using WorldRuntime w = world;
        player.Health = 100;
        auras.Live.Add(new SpellAura(0, AuraType.ModRegen, 30, 4000, 0)); // 30 * 2000 / 4000 = 15 per tick

        Ticks(world, 2);

        Assert.Equal(130u, player.Health);
    }

    [Fact]
    public void Food_InCombat_RestoresNothing()
    {
        (WorldRuntime world, Map map, Player player, FakeAuras auras) = Setup();
        using WorldRuntime w = world;
        player.Health = 100;
        auras.Live.Add(new SpellAura(0, AuraType.ModRegen, 17, 0, 0));
        map.Combat.SetInCombatState(player, 600000);

        Ticks(world, 3);

        Assert.Equal(100u, player.Health);
    }

    [Fact]
    public void HealthRegenInCombatAura_RegeneratesInCombat_TwoSecondsOfAmountOverFive()
    {
        (WorldRuntime world, Map map, Player player, FakeAuras auras) = Setup();
        using WorldRuntime w = world;
        player.Health = 100;
        auras.Live.Add(new SpellAura(0, AuraType.ModHealthRegenInCombat, 10, 0, 0)); // 2 * 10 / 5 = 4 per tick
        map.Combat.SetInCombatState(player, 600000);

        Ticks(world, 3);

        Assert.Equal(112u, player.Health);
    }

    [Fact]
    public void RegenDuringCombatAura_ScalesTheSpiritRegenByItsPercentage()
    {
        (WorldRuntime world, Map map, Player player, FakeAuras auras) = Setup();
        using WorldRuntime w = world;
        player.SetUInt32(UpdateFields.UnitFieldStat0 + 4, 30); // warrior: 1.26 * 30 - 22.6 = 15.2 per tick
        player.Health = 100;
        auras.Live.Add(new SpellAura(0, AuraType.ModRegenDuringCombat, 50, 0, 0)); // 7.6 per tick in combat
        map.Combat.SetInCombatState(player, 600000);

        Ticks(world, 1);
        Assert.Equal(107u, player.Health);
    }

    [Fact]
    public void HealthRegenPercent_MultipliesTheSpiritRegen_OutOfCombatOnly()
    {
        (WorldRuntime world, _, Player player, FakeAuras auras) = Setup();
        using WorldRuntime w = world;
        player.SetUInt32(UpdateFields.UnitFieldStat0 + 4, 30);
        player.Health = 100;
        auras.Live.Add(new SpellAura(0, AuraType.ModHealthRegenPercent, 100, 0, 0)); // 15.2 * 2 = 30.4

        Ticks(world, 1);

        Assert.Equal(130u, player.Health);
    }

    [Fact]
    public void DrinkingDrink430_RestoresSixteenManaPerTick_FromTheAuraAlone()
    {
        (WorldRuntime world, _, Player player, FakeAuras auras) = Setup();
        using WorldRuntime w = world;
        player.SetUInt32(UpdateFields.UnitFieldMaxpower1, 1000);
        player.SetUInt32(UpdateFields.UnitFieldPower1, 0);
        auras.Live.Add(new SpellAura(0, AuraType.ModPowerRegen, 42, 0, (int)PowerType.Mana)); // 42 / 5 * 2 = 16.8 -> 16 (uint)

        Ticks(world, 9);

        Assert.Equal(144u, player.GetUInt32(UpdateFields.UnitFieldPower1));
    }

    [Fact]
    public void DrinkInsideTheFiveSecondRule_StillGivesTheMp5Part()
    {
        (WorldRuntime world, _, Player player, FakeAuras auras) = Setup();
        using WorldRuntime w = world;
        player.SetUInt32(UpdateFields.UnitFieldMaxpower1, 1000);
        player.SetUInt32(UpdateFields.UnitFieldPower1, 0);
        auras.Live.Add(new SpellAura(0, AuraType.ModPowerRegen, 42, 0, (int)PowerType.Mana));
        player.Combat.LastManaUseTimer = 5000;

        world.RunTick(50);

        Assert.Equal(16u, player.GetUInt32(UpdateFields.UnitFieldPower1));
    }

    [Fact]
    public void ManaPerSecond_ScalesTheSpiritPartByTheInterruptPercentageCappedAtOneHundred()
    {
        var auras = new FakeAuras();
        auras.Live.Add(new SpellAura(0, AuraType.ModManaRegenInterrupt, 30, 0, 0));
        auras.Live.Add(new SpellAura(1, AuraType.ModPowerRegen, 10, 0, (int)PowerType.Mana));
        auras.Live.Add(new SpellAura(2, AuraType.ModPowerRegen, 99, 0, (int)PowerType.Rage)); // another power: ignored
        var unit = new CombatTestUnit();

        Assert.Equal(2.0f + 3.0f, RegenAuraRules.ManaPerSecond(auras, unit, 10.0f, insideFiveSecondRule: true), 4);
        Assert.Equal(2.0f + 10.0f, RegenAuraRules.ManaPerSecond(auras, unit, 10.0f, insideFiveSecondRule: false), 4);

        auras.Live.Add(new SpellAura(3, AuraType.ModManaRegenInterrupt, 200, 0, 0));
        Assert.Equal(2.0f + 10.0f, RegenAuraRules.ManaPerSecond(auras, unit, 10.0f, insideFiveSecondRule: true), 4);
    }

    [Fact]
    public void WithoutAnyAura_TheFiveSecondRuleStillStopsSpiritRegen()
    {
        Assert.Equal(0.0f, RegenAuraRules.ManaPerSecond(null, new CombatTestUnit(), 10.0f, insideFiveSecondRule: true));
        Assert.Equal(10.0f, RegenAuraRules.ManaPerSecond(null, new CombatTestUnit(), 10.0f, insideFiveSecondRule: false));
    }

    // --- sit down on apply, stand up removes (Unit::SetStandState, _AddSpellAuraHolder) ----------------

    private const uint FoodSpell = 942001;

    private static SpellTestKit FoodKit() => new(
        Spell(FoodSpell, Effect(SpellEffectName.ApplyAura, 17, SpellImplicitTarget.UnitCaster, AuraType.ModRegen)) with
        {
            AuraInterruptFlags = SpellAuraInterruptFlags.StandingCancels | SpellAuraInterruptFlags.UnderWaterCancels,
            Duration = new SpellDuration(18000, 0, 18000),
            SpellVisual = 1,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        });

    [Fact]
    public void ApplyingFood_SitsThePlayerDown_AndStandingUpRemovesTheAura()
    {
        using SpellTestKit kit = FoodKit();
        (Player player, _) = kit.AddPlayer(1);
        Assert.Equal(StandState.Stand, player.StandState);

        kit.System.CastSpell(player, FoodSpell, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(StandState.Sit, player.StandState);
        kit.Advance(2000);
        Assert.True(kit.System.HasAura(player, FoodSpell));

        player.SetStandState(StandState.Stand);
        kit.Advance(100);

        Assert.False(kit.System.HasAura(player, FoodSpell));
    }

    [Fact]
    public void ApplyingFood_ToAPlayerAlreadyOnAChair_KeepsTheChair()
    {
        using SpellTestKit kit = FoodKit();
        (Player player, _) = kit.AddPlayer(1);
        player.SetStandState(StandState.SitMediumChair);

        kit.System.CastSpell(player, FoodSpell, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(StandState.SitMediumChair, player.StandState);
    }

    [Fact]
    public void SpellSystemPowerAuras_ListsTheLiveAurasOfAType()
    {
        using SpellTestKit kit = FoodKit();
        (Player player, _) = kit.AddPlayer(1);
        kit.System.CastSpell(player, FoodSpell, SpellCastTargets.ForSelf(), triggered: true);
        var source = new SpellSystemPowerAuras(kit.System);

        SpellAura aura = Assert.Single(source.GetAuras(player, AuraType.ModRegen));

        Assert.Equal(17, aura.Amount);
        Assert.Empty(source.GetAuras(player, AuraType.ModPowerRegen));
    }
}

using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Auras;

/// <summary>
/// Periodic timing against vmangos Aura::Update (SpellAuras.cpp:553-572), CalculatePeriodic (:8053-8110), UpdatePeriodicTimer
/// (:528-543) and HandleAuraModTotalManaPercentRegen (:4855-4866). All on the virtual clock: <c>Advance(ms, step)</c> with a
/// step equal to the span delivers one big update, which is how a lag spike looks to the aura.
/// </summary>
public sealed class PeriodicTimingTests
{
    private const AuraType Probe = SpellHandlerModuleTests.FixtureAura;
    private const uint Plain = 943001;
    private const uint ImmolationTrapEffect = 13797;
    private const uint Stoneclaw = 943002;
    private const uint ManaFood = 943003;

    private static SpellInfo Periodic(uint id, uint amplitude, uint visual = 1, uint icon = 0) => Spell(
        id, Effect(SpellEffectName.ApplyAura, 1, SpellImplicitTarget.UnitCaster, Probe, amplitude: amplitude)) with
    {
        Duration = new SpellDuration(60000, 0, 60000),
        SpellVisual = visual,
        SpellIconId = icon,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static (SpellTestKit Kit, Player Player, int[] Ticks) Setup(bool catchUp = false)
    {
        var kit = new SpellTestKit(
            Periodic(Plain, 3000),
            Periodic(ImmolationTrapEffect, 3000),
            Periodic(Stoneclaw, 3000, visual: 0, icon: 689),
            Spell(ManaFood, Effect(SpellEffectName.ApplyAura, 5, SpellImplicitTarget.UnitCaster, AuraType.ObsModMana)) with
            {
                Duration = new SpellDuration(18000, 0, 18000),
                SpellVisual = 1,
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            });
        kit.System.AuraOptions = new AuraOptions { PeriodicCatchUp = catchUp };
        (Player player, _) = kit.AddPlayer(1);
        var ticks = new int[1];
        kit.System.RegisterAura(Probe, new AuraHandler(null, (_, _, _) => ticks[0]++));
        return (kit, player, ticks);
    }

    private static void Cast(SpellTestKit kit, Player player, uint spell)
        => kit.System.CastSpell(player, spell, SpellCastTargets.ForSelf(), triggered: true);

    [Fact]
    public void FirstTick_ForANormalAura_IsOneAmplitudeAfterApplication()
    {
        (SpellTestKit kit, Player player, int[] ticks) = Setup();
        using SpellTestKit k = kit;
        Cast(kit, player, Plain);

        kit.Advance(2900);
        Assert.Equal(0, ticks[0]);
        kit.Advance(100);
        Assert.Equal(1, ticks[0]);
    }

    [Fact]
    public void ALagSpike_DeliversOneTick_NotAllTheMissedOnes()
    {
        (SpellTestKit kit, Player player, int[] ticks) = Setup();
        using SpellTestKit k = kit;
        Cast(kit, player, Plain);

        kit.Advance(7000, step: 7000); // 7 s in one update on a 3 s aura: two periods missed

        Assert.Equal(1, ticks[0]);
        Assert.Equal(0, kit.System.GetAuras(player).Single().Auras[0]!.PeriodicTimer); // drifted past a whole period: reset to 0
    }

    [Fact]
    public void ALagSpikeShorterThanAPeriod_KeepsTheRemainder()
    {
        (SpellTestKit kit, Player player, int[] ticks) = Setup();
        using SpellTestKit k = kit;
        Cast(kit, player, Plain);

        kit.Advance(4000, step: 4000); // timer 3000 - 4000 = -1000 -> tick, timer 2000

        Assert.Equal(1, ticks[0]);
        Assert.Equal(2000, kit.System.GetAuras(player).Single().Auras[0]!.PeriodicTimer);
    }

    [Fact]
    public void WithPeriodicCatchUp_EveryMissedTickIsDeliveredInOneUpdate()
    {
        (SpellTestKit kit, Player player, int[] ticks) = Setup(catchUp: true);
        using SpellTestKit k = kit;
        Cast(kit, player, Plain);

        kit.Advance(7000, step: 7000);

        Assert.Equal(2, ticks[0]);
        Assert.Equal(2000, kit.System.GetAuras(player).Single().Auras[0]!.PeriodicTimer);
    }

    [Fact]
    public void ImmolationTrapEffect_TicksAtTheFirstUpdate()
    {
        (SpellTestKit kit, Player player, int[] ticks) = Setup();
        using SpellTestKit k = kit;
        Cast(kit, player, ImmolationTrapEffect);

        kit.Advance(100);

        Assert.Equal(1, ticks[0]);
        kit.Advance(2800);
        Assert.Equal(1, ticks[0]);
        kit.Advance(200);
        Assert.Equal(2, ticks[0]);
    }

    [Fact]
    public void StoneclawRule_VisualZeroWithIcon689_TicksAtTheFirstUpdate()
    {
        (SpellTestKit kit, Player player, int[] ticks) = Setup();
        using SpellTestKit k = kit;
        Cast(kit, player, Stoneclaw);

        kit.Advance(100);

        Assert.Equal(1, ticks[0]);
    }

    [Fact]
    public void ARefresh_RestartsTheTimerWithTheSameInitialRule()
    {
        (SpellTestKit kit, Player player, int[] ticks) = Setup();
        using SpellTestKit k = kit;
        Cast(kit, player, Plain);
        Cast(kit, player, ImmolationTrapEffect);
        kit.Advance(1000);

        Cast(kit, player, Plain);
        Cast(kit, player, ImmolationTrapEffect);

        SpellAura normal = kit.System.GetAuras(player).Single(h => h.Spell.Id == Plain).Auras[0]!;
        SpellAura immediate = kit.System.GetAuras(player).Single(h => h.Spell.Id == ImmolationTrapEffect).Auras[0]!;
        Assert.Equal(3000, normal.PeriodicTimer);
        Assert.Equal(0, immediate.PeriodicTimer);
    }

    [Fact]
    public void ObsModMana_WithoutAnAmplitude_TicksEverySecond()
    {
        (SpellTestKit kit, Player player, _) = Setup();
        using SpellTestKit k = kit;
        player.SetUInt32(UpdateFields.UnitFieldMaxpower1, 1000);
        player.SetUInt32(UpdateFields.UnitFieldPower1, 0);
        Cast(kit, player, ManaFood);
        SpellAura aura = kit.System.GetAuras(player).Single().Auras[0]!;
        Assert.Equal(1000u, aura.Period);

        kit.Advance(3000);

        Assert.Equal(3, aura.TickCount);
        Assert.Equal(150u, player.GetUInt32(UpdateFields.UnitFieldPower1)); // 5 percent of 1000 per tick
    }

    [Fact]
    public void AnAuraTypeWithoutAnAmplitude_IsNotPeriodic_ExceptObsModMana()
    {
        Assert.Equal(0u, PeriodicTiming.PeriodFor(AuraType.PeriodicDamage, 0));
        Assert.Equal(0u, PeriodicTiming.PeriodFor(AuraType.ObsModHealth, 0));
        Assert.Equal(1000u, PeriodicTiming.PeriodFor(AuraType.ObsModMana, 0));
        Assert.Equal(2000u, PeriodicTiming.PeriodFor(AuraType.ObsModMana, 2000));
    }

    [Theory]
    [InlineData(8000, 3000, 2000)]
    [InlineData(6000, 3000, 3000)] // divisible: a whole period
    [InlineData(2500, 3000, 2500)] // shorter than a period: the remaining duration
    [InlineData(3000, 3000, 3000)]
    public void SyncToDuration_FollowsUpdatePeriodicTimer(int duration, uint period, int expected)
    {
        var aura = new SpellAura(0, AuraType.PeriodicDamage, 1, period, 0) { PeriodicTimer = 1 };

        PeriodicTiming.SyncToDuration(aura, duration);

        Assert.Equal(expected, aura.PeriodicTimer);
    }
}

using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Combat;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Combat;

/// <summary>
/// The kite decision (<see cref="PlayerbotKiteModel.Decide"/>) as a pure function of a snapshot, and the step geometry and slow
/// classification around it.
/// </summary>
public sealed class PlayerbotKiteModelTests
{
    private const float BotSpeed = 7f;

    /// <summary>A bot at 40% health (the line is 50%) with one ordinary pursuer at its own speed, no slow ready, nothing kited yet.</summary>
    private static KiteFacts Facts(float healthPct = 40f, int pursuers = 1, bool caster = false, bool immobile = false, float nearestSpeed = BotSpeed,
        bool slowReady = false, uint msSinceStep = uint.MaxValue, int steps = 0, int maxSteps = 8, uint kiteMs = 0, int failedSpots = 0)
        => new(healthPct, 50f, pursuers, caster, immobile, nearestSpeed, BotSpeed, slowReady, msSinceStep, steps, maxSteps, kiteMs, failedSpots);

    [Fact]
    public void SameSpeed_NoSlowReady_FightsOn() => Assert.Equal(KiteDecision.None, PlayerbotKiteModel.Decide(Facts()));

    [Fact]
    public void SameSpeed_SlowReady_Slows() => Assert.Equal(KiteDecision.Slow, PlayerbotKiteModel.Decide(Facts(slowReady: true)));

    [Fact]
    public void HalfSpeedPursuer_Steps() => Assert.Equal(KiteDecision.Step, PlayerbotKiteModel.Decide(Facts(nearestSpeed: BotSpeed * 0.5f)));

    [Fact]
    public void RootedOrStunnedPursuer_Steps() => Assert.Equal(KiteDecision.Step, PlayerbotKiteModel.Decide(Facts(immobile: true)));

    [Fact]
    public void ASlowedPursuer_StepsEvenWhenAnotherSlowIsReady()
        => Assert.Equal(KiteDecision.Step, PlayerbotKiteModel.Decide(Facts(nearestSpeed: BotSpeed * 0.5f, slowReady: true)));

    [Fact]
    public void AnEightyPercentPursuerIsOutWalked_ANinetyPercentOneIsNot()
    {
        Assert.Equal(KiteDecision.Step, PlayerbotKiteModel.Decide(Facts(nearestSpeed: BotSpeed * PlayerbotKiteModel.SlowSpeedRatio)));
        Assert.Equal(KiteDecision.None, PlayerbotKiteModel.Decide(Facts(nearestSpeed: BotSpeed * 0.9f)));
    }

    [Fact]
    public void HealthOverTheLine_FightsOn()
    {
        Assert.Equal(KiteDecision.None, PlayerbotKiteModel.Decide(Facts(healthPct: 50.5f, slowReady: true)));
        Assert.Equal(KiteDecision.Slow, PlayerbotKiteModel.Decide(Facts(healthPct: 50f, slowReady: true)));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(0)]
    public void ThreePursuersOrNone_FightsOn(int pursuers)
        => Assert.Equal(KiteDecision.None, PlayerbotKiteModel.Decide(Facts(pursuers: pursuers, slowReady: true, immobile: true)));

    [Fact]
    public void TwoPursuers_StillKite() => Assert.Equal(KiteDecision.Slow, PlayerbotKiteModel.Decide(Facts(pursuers: 2, slowReady: true)));

    [Fact]
    public void ACasterPursuer_FightsOn() => Assert.Equal(KiteDecision.None, PlayerbotKiteModel.Decide(Facts(caster: true, immobile: true, slowReady: true)));

    [Fact]
    public void InsideTheStepCooldown_FightsOn_AndAfterItSteps()
    {
        KiteFacts slowed = Facts(nearestSpeed: BotSpeed * 0.5f, msSinceStep: PlayerbotKiteModel.StepCooldownMs - 1);
        Assert.Equal(KiteDecision.None, PlayerbotKiteModel.Decide(slowed));
        Assert.Equal(KiteDecision.Step, PlayerbotKiteModel.Decide(slowed with { MsSinceStep = PlayerbotKiteModel.StepCooldownMs }));
    }

    [Fact]
    public void TheStepCap_GivesUp()
    {
        Assert.Equal(KiteDecision.Step, PlayerbotKiteModel.Decide(Facts(immobile: true, steps: 7)));
        Assert.Equal(KiteDecision.GiveUp, PlayerbotKiteModel.Decide(Facts(immobile: true, steps: 8)));
    }

    [Fact]
    public void TwoFailedSpots_GiveUp()
    {
        Assert.Equal(KiteDecision.Step, PlayerbotKiteModel.Decide(Facts(immobile: true, failedSpots: 1)));
        Assert.Equal(KiteDecision.GiveUp, PlayerbotKiteModel.Decide(Facts(immobile: true, failedSpots: 2)));
    }

    [Fact]
    public void TheTimeCap_GivesUp()
    {
        Assert.Equal(KiteDecision.Slow, PlayerbotKiteModel.Decide(Facts(slowReady: true, kiteMs: PlayerbotKiteModel.MaxKiteMs - 1)));
        Assert.Equal(KiteDecision.GiveUp, PlayerbotKiteModel.Decide(Facts(slowReady: true, kiteMs: PlayerbotKiteModel.MaxKiteMs)));
    }

    [Fact]
    public void AGiveUpNeedsSomethingToGiveUpOn()
    {
        // Healthy, or nothing close: the caps are not reported for a bot that is not kiting at all.
        Assert.Equal(KiteDecision.None, PlayerbotKiteModel.Decide(Facts(healthPct: 90f, steps: 8)));
        Assert.Equal(KiteDecision.None, PlayerbotKiteModel.Decide(Facts(pursuers: 0, steps: 8)));
    }

    [Fact]
    public void ABotWithNoRunSpeed_NeverStepsOnSpeedAlone()
        => Assert.Equal(KiteDecision.None, PlayerbotKiteModel.Decide(new KiteFacts(40f, 50f, 1, false, false, 1f, 0f, false, uint.MaxValue, 0, 8, 0, 0)));

    [Fact]
    public void StartDistance_IsEightYards_AHuntersDeadZonePlusTwo()
    {
        Assert.Equal(8f, PlayerbotKiteModel.StartYards(hunter: false));
        Assert.Equal(PlayerbotClassRotation.HunterDeadZone + 2f, PlayerbotKiteModel.StartYards(hunter: true));
    }

    [Theory]
    [InlineData(25f, false, 22f)]
    [InlineData(20f, false, 17f)]
    [InlineData(12f, false, 10f)]
    [InlineData(30f, true, 27f)]
    [InlineData(40f, true, 28f)]
    [InlineData(18f, true, 15f)]
    [InlineData(10f, true, 14f)]
    public void WantedDistance_FollowsThePreferredRange_WithinItsClamp(float preferred, bool hunter, float expected)
        => Assert.Equal(expected, PlayerbotKiteModel.WantedYards(preferred, hunter));

    [Theory]
    [InlineData(22f, 2f, 10f, 10f)]
    [InlineData(22f, 15f, 10f, 7f)]
    [InlineData(22f, 21f, 10f, 4f)]
    [InlineData(22f, 0f, 6f, 6f)]
    public void StepLength_IsWhatIsStillWanted_BetweenFourYardsAndTheOption(float wanted, float nearest, float option, float expected)
        => Assert.Equal(expected, PlayerbotKiteModel.StepLength(wanted, nearest, option));

    [Fact]
    public void TheFan_StartsStraightAwayAndNeverTurnsBackTowardThePursuers()
    {
        float[] fan = PlayerbotKiteModel.FanDegrees.ToArray();
        Assert.Equal(7, fan.Length);
        Assert.Equal(0f, fan[0]);
        Assert.All(fan, degrees => Assert.InRange(MathF.Abs(degrees), 0f, 105f));
        Assert.Equal(fan.Length, fan.Distinct().Count());
    }

    [Fact]
    public void Turned_RotatesAUnitVectorCounterClockwise()
    {
        Vector2 turned = PlayerbotKiteModel.Turned(new Vector2(1f, 0f), 90f);
        Assert.Equal(0f, turned.X, 4);
        Assert.Equal(1f, turned.Y, 4);
        Assert.Equal(1f, PlayerbotKiteModel.Turned(new Vector2(0.6f, 0.8f), 35f).Length(), 4);
    }

    [Theory]
    [InlineData(Class.Mage, "Frost Nova,Cone of Cold,Frostbolt")]
    [InlineData(Class.Hunter, "Concussive Shot,Wing Clip")]
    [InlineData(Class.Warlock, "Curse of Exhaustion,Death Coil")]
    [InlineData(Class.Shaman, "Frost Shock")]
    [InlineData(Class.Druid, "Entangling Roots")]
    [InlineData(Class.Warrior, "Hamstring")]
    [InlineData(Class.Rogue, "Gouge")]
    [InlineData(Class.Priest, "")]
    [InlineData(Class.Paladin, "")]
    public void EachClass_HasItsSlowsInOrder(Class playerClass, string expected)
        => Assert.Equal(expected, string.Join(',', PlayerbotKiteModel.SlowsOf(playerClass)));

    [Fact]
    public void EverySlowName_IsOneTheBotResolvesFromItsBook()
    {
        foreach (Class playerClass in Enum.GetValues<Class>())
            foreach (string name in PlayerbotKiteModel.SlowsOf(playerClass))
                Assert.Contains(name, PlayerbotEscapes.All);
    }

    private static SpellInfo Spell(string name, params SpellEffectInfo[] effects) => new()
    {
        Id = 1, Name = name, Duration = new SpellDuration(8000, 0, 8000), Effects = [.. effects],
    };

    [Fact]
    public void ANegativeSpeedAura_IsASlow_AndASpellWithoutOneIsNot()
    {
        SpellInfo snare = Spell("Frostbolt", new SpellEffectInfo
        {
            Effect = SpellEffectName.ApplyAura, AuraType = AuraType.ModDecreaseSpeed, TargetA = SpellImplicitTarget.UnitEnemy, BasePoints = -41,
        });
        Assert.True(PlayerbotKiteModel.IsSlow(PlayerbotEscapes.Frostbolt, snare));
        Assert.True(PlayerbotKiteModel.IsSlow(PlayerbotEscapes.ConeOfCold, snare));
        Assert.True(PlayerbotKiteModel.IsSlow(PlayerbotEscapes.CurseOfExhaustion, snare));

        SpellInfo noAura = Spell("Frostbolt", new SpellEffectInfo { Effect = SpellEffectName.SchoolDamage, TargetA = SpellImplicitTarget.UnitEnemy, BasePoints = 9 });
        Assert.False(PlayerbotKiteModel.IsSlow(PlayerbotEscapes.Frostbolt, noAura));
        Assert.False(PlayerbotKiteModel.IsSlow(PlayerbotEscapes.Frostbolt, null));
    }

    [Fact]
    public void ARootWithADuration_IsASlow_AndOneWithoutIsNot()
    {
        var root = new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.ModRoot, TargetA = SpellImplicitTarget.UnitEnemy };
        SpellInfo timed = Spell("Entangling Roots", root);
        Assert.True(PlayerbotKiteModel.IsSlow(PlayerbotEscapes.EntanglingRoots, timed));
        Assert.False(PlayerbotKiteModel.IsSlow(PlayerbotEscapes.EntanglingRoots, timed with { Duration = new SpellDuration(0, 0, 0) }));
    }
}

using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Stealth;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Rogue;

/// <summary>
/// Unit::CanDetectStealthOf (vmangos Unit.cpp:6543-6616). Each case is one of the sniffed retail measurements recorded in
/// that function's comment (the ModStealth amount of a rogue stands in as its stealth skill, level x 5 for the cases that
/// name equal levels). The tests prove the code equals the comment, not that retail behaves so.
/// </summary>
public sealed class StealthDetectionFormulaTests
{
    private static StealthDetectionInput Player(float distance, int detectorLevel, int targetLevel, int detectMod = 0, bool inArc = true, int stealthLevelMod = 0)
        => new(distance, false, true, true, detectorLevel, targetLevel, targetLevel * 5, stealthLevelMod, detectMod, inArc);

    private static StealthDetectionInput Creature(float distance, int creatureLevel, int rogueLevel, bool inArc = true)
        => new(distance, false, false, true, creatureLevel, rogueLevel, rogueLevel * 5, 0, 0, inArc);

    [Theory]
    [InlineData(1, 1, 0, 9.0f)]     // Level 1 rogue vs level 1 rogue: approaching 9
    [InlineData(1, 1, 50, 24.0f)]   // with Perception (+50 detect): approaching 24
    [InlineData(1, 2, 0, 7.5f)]     // vs level 2: 7.5
    [InlineData(1, 2, 50, 22.5f)]
    [InlineData(1, 3, 0, 6.0f)]     // vs level 3: 6
    [InlineData(1, 3, 50, 21.0f)]
    public void PlayerVsPlayer_VisibleDistance_FollowsTheSniffedMeasurements(int detectorLevel, int targetLevel, int perception, float expected)
    {
        StealthDetectionResult at = StealthDetection.Evaluate(Player(expected - 0.05f, detectorLevel, targetLevel, perception));
        Assert.True(at.Detected);
        Assert.Equal(expected, at.VisibleDistance, 3);
        Assert.False(StealthDetection.Evaluate(Player(expected + 0.05f, detectorLevel, targetLevel, perception)).Detected);
    }

    [Fact]
    public void Level32HunterVsLevel1Rogue_IsCappedAt30_AndBehindItIs21()
    {
        StealthDetectionResult front = StealthDetection.Evaluate(Player(29.9f, 32, 1));
        Assert.True(front.Detected);
        Assert.Equal(30.0f, front.VisibleDistance);
        Assert.False(StealthDetection.Evaluate(Player(30.1f, 32, 1)).Detected);

        StealthDetectionResult behind = StealthDetection.Evaluate(Player(20.9f, 32, 1, inArc: false));
        Assert.True(behind.Detected);
        Assert.Equal(21.0f, behind.VisibleDistance); // the 9 yard penalty is applied after the cap
        Assert.False(StealthDetection.Evaluate(Player(21.1f, 32, 1, inArc: false)).Detected);
    }

    [Theory]
    [InlineData(1, 5.0f / 6.0f + (15 * (5.0f / 6.0f) / 5.0f))] // 3.333: sniffed 3.237
    [InlineData(2, 5.0f / 6.0f + (10 * (5.0f / 6.0f) / 5.0f))] // 2.5: sniffed 2.389
    [InlineData(3, 5.0f / 6.0f + (5 * (5.0f / 6.0f) / 5.0f))]  // 1.667: sniffed 1.588
    public void Level4CreatureVsRogue_AggroDistanceGrowsFiveSixthsPerLevel_AndAlertIsFiveYardsBeyond(int rogueLevel, float expected)
    {
        StealthDetectionResult inside = StealthDetection.Evaluate(Creature(expected - 0.05f, 4, rogueLevel));
        Assert.True(inside.Detected);
        Assert.Equal(expected, inside.VisibleDistance, 3);

        StealthDetectionResult alert = StealthDetection.Evaluate(Creature(expected + 0.5f, 4, rogueLevel));
        Assert.False(alert.Detected);
        Assert.True(alert.Alert);

        Assert.True(StealthDetection.Evaluate(Creature(expected + 5.0f, 4, rogueLevel)).Alert); // alert at exactly visible + 5
        StealthDetectionResult far = StealthDetection.Evaluate(Creature(expected + 5.1f, 4, rogueLevel));
        Assert.False(far.Detected);
        Assert.False(far.Alert);
    }

    [Fact]
    public void Alert_IsFalseWhenDetected_AndWhenWithinThePlainBand()
    {
        StealthDetectionResult detected = StealthDetection.Evaluate(Creature(2.0f, 4, 1));
        Assert.True(detected.Detected);
        Assert.False(detected.Alert);
    }

    [Theory]
    [InlineData(1.49f, true)]
    [InlineData(1.5f, false)] // a distance of exactly 1.5 is not below the collision radius
    public void UnderOnePointFiveYards_IsAlwaysDetected_EvenWhenTheVisibleDistanceIsZero(float distance, bool detected)
    {
        // Level 1 creature vs a level 60 rogue with extra stealth level: the visible distance clamps to 0.
        var input = new StealthDetectionInput(distance, false, false, true, 1, 60, 300, 100, 0, true);
        Assert.Equal(detected, StealthDetection.Evaluate(input).Detected);
    }

    [Fact]
    public void StunnedDetector_NeverDetects_EvenInsideCollisionDistance()
    {
        StealthDetectionInput stunned = Player(0.5f, 60, 1) with { DetectorStunned = true };
        Assert.False(StealthDetection.Evaluate(stunned).Detected);
        Assert.True(StealthDetection.Evaluate(Player(0.5f, 60, 1)).Detected);
    }

    [Fact]
    public void BehindTheDetector_ReducesTheVisibleDistanceByNineYards_AndItNeverGoesNegativeAfterTheCap()
    {
        StealthDetectionResult front = StealthDetection.Evaluate(Player(2.0f, 1, 1));
        Assert.True(front.Detected);
        StealthDetectionResult behind = StealthDetection.Evaluate(Player(2.0f, 1, 1, inArc: false)); // 9 - 9 = 0
        Assert.False(behind.Detected);
        Assert.Equal(0f, behind.VisibleDistance, 3);
    }

    [Fact]
    public void ADoubledYardsPerLevel_AppliesOnlyAboveThreeLevelsOfDifference()
    {
        // 3 levels above: normal 1.5; 4 levels above: doubled to 3.0 (vmangos levelDiff > 3).
        Assert.Equal(9.0f + (15 * 1.5f / 5.0f), StealthDetection.Evaluate(Player(2f, 4, 1)).VisibleDistance, 3);
        Assert.Equal(9.0f + (20 * 3.0f / 5.0f), StealthDetection.Evaluate(Player(2f, 5, 1)).VisibleDistance, 3);
    }

    [Fact]
    public void StealthLevelAndTheDetectBonus_ShiftTheSkillDifference()
    {
        float baseline = StealthDetection.Evaluate(Player(2f, 10, 10)).VisibleDistance;
        Assert.Equal(baseline - (10 * 1.5f / 5.0f), StealthDetection.Evaluate(Player(2f, 10, 10, stealthLevelMod: 10)).VisibleDistance, 3);
        Assert.Equal(baseline + (10 * 1.5f / 5.0f), StealthDetection.Evaluate(Player(2f, 10, 10, detectMod: 10)).VisibleDistance, 3);
    }

    [Fact]
    public void PlayerDetectorVsCreatureTarget_Uses21YardsAndTheCreaturesLevelTimesFiveAsItsSkill()
    {
        // The aura total on a creature target is ignored (vmangos: target->IsPlayer() ? aura total : level * 5).
        var input = new StealthDetectionInput(2f, false, true, false, 10, 10, 9999, 0, 0, true);
        Assert.Equal(21.0f, StealthDetection.Evaluate(input).VisibleDistance, 3);
    }

    [Fact]
    public void MaxDetectRanges_AreConfigurable_PerDetectorKind_AndDefaultTo30()
    {
        Assert.Equal(30.0f, StealthOptions.Default.MaxPlayerDetectRange);
        Assert.Equal(30.0f, StealthOptions.Default.MaxCreatureDetectRange);

        var tight = new StealthOptions { MaxPlayerDetectRange = 20.0f };
        Assert.True(StealthDetection.Evaluate(Player(19.9f, 32, 1), tight).Detected);
        Assert.False(StealthDetection.Evaluate(Player(20.1f, 32, 1), tight).Detected);
        var creatureTight = new StealthOptions { MaxCreatureDetectRange = 2.0f };
        Assert.False(StealthDetection.Evaluate(Creature(2.5f, 60, 1), creatureTight).Detected);
        Assert.True(StealthDetection.Evaluate(Creature(25f, 60, 1), tight).Detected); // the player limit does not apply to a creature detector
    }

    [Fact]
    public void UnitAdapter_ReadsStealthAuras_FromTheSpellSystem()
    {
        var kit = new SpellTestKit(
            Spell(910201, Effect(SpellEffectName.ApplyAura, 5, aura: AuraType.ModStealth)) with { Duration = new SpellDuration(-1, 0, -1), SpellVisual = 1 },
            Spell(910202, Effect(SpellEffectName.ApplyAura, 50, aura: AuraType.ModStealthDetect, misc: 0)) with { Duration = new SpellDuration(-1, 0, -1), SpellVisual = 1 },
            Spell(910203, Effect(SpellEffectName.ApplyAura, 50, aura: AuraType.ModStealthDetect, misc: 3)) with { Duration = new SpellDuration(-1, 0, -1), SpellVisual = 1 });
        using (kit)
        {
            kit.System.RegisterAura(AuraType.ModStealth, new AuraHandler(null, null));
            kit.System.RegisterAura(AuraType.ModStealthDetect, new AuraHandler(null, null));
            (Player detector, _) = kit.AddPlayer(1);
            (Player rogue, _) = kit.AddPlayer(2, 2);
            detector.Level = 1;
            rogue.Level = 1;
            kit.System.CastSpell(rogue, 910201, SpellCastTargets.ForSelf(), triggered: true);

            // Facing: orientation 0 looks along +X; the rogue at +X is in front.
            detector.Relocate(0, 0, 83.5f, 0f, 0);
            rogue.Relocate(5, 0, 83.5f, 0f, 0);
            StealthDetectionResult plain = StealthDetection.CanDetectStealthOf(kit.System, detector, rogue, 5f);
            Assert.Equal(9.0f, plain.VisibleDistance, 3); // 9 + (5 - 5) * 1.5 / 5

            kit.System.CastSpell(detector, 910203, SpellCastTargets.ForSelf(), triggered: true); // misc 3 is another detection kind: ignored
            Assert.Equal(9.0f, StealthDetection.CanDetectStealthOf(kit.System, detector, rogue, 5f).VisibleDistance, 3);

            kit.System.CastSpell(detector, 910202, SpellCastTargets.ForSelf(), triggered: true); // Perception-like: misc 0
            Assert.Equal(24.0f, StealthDetection.CanDetectStealthOf(kit.System, detector, rogue, 5f).VisibleDistance, 3);

            rogue.Relocate(-5, 0, 83.5f, 0f, 0); // behind the detector
            StealthDetectionResult behind = StealthDetection.CanDetectStealthOf(kit.System, detector, rogue, 5f);
            Assert.Equal(15.0f, behind.VisibleDistance, 3);
        }
    }

    [Fact]
    public void LevelForTarget_CountsWorldBossesAsTargetLevelPlusThree_OtherUnitsTheirOwnLevel()
    {
        var boss = new CombatTestUnit(level: 63) { IsWorldBoss = true };
        var plain = new CombatTestUnit(level: 63);
        using var kit = new SpellTestKit();
        (Player rogue, _) = kit.AddPlayer(1);
        rogue.Level = 40;
        Assert.Equal(43, StealthDetection.LevelForTarget(boss, rogue));
        Assert.Equal(63, StealthDetection.LevelForTarget(boss, null));
        Assert.Equal(63, StealthDetection.LevelForTarget(plain, rogue));
        Assert.Equal(40, StealthDetection.LevelForTarget(rogue, boss));
    }
}

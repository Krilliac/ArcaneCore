using ArcaneCore.Game.Combat;
using Xunit;

namespace ArcaneCore.Game.Tests;

/// <summary>
/// The vanilla attack table (vmangos SpellCaster::RollMeleeOutcomeAgainst) with exact range
/// boundaries, plus armor and glancing math.
/// </summary>
public sealed class CombatHitTableTests
{
    /// <summary>A level-60 player hitting a level-60 mob from the front: skills 300 vs 300.</summary>
    private static MeleeRollInput PlayerVsMob(byte mobLevel = 60) => new()
    {
        AttackType = WeaponAttackType.BaseAttack,
        VictimIsPlayer = false,
        AttackerIsCreature = false,
        AttackerIsPlayerControlled = true,
        VictimIsPlayerControlled = false,
        VictimStanding = true,
        AttackerCanCrush = false,
        VictimCreatureCanParry = true,
        VictimCreatureCanBlock = true,
        VictimLevel = mobLevel,
        AttackerMaxSkill = 300,
        VictimMaxSkill = mobLevel * 5,
        AttackerWeaponSkill = 300,
        VictimDefenseSkill = mobLevel * 5,
        BaseCritChance = 0f,
        DodgeChance = 5f,
        ParryChance = 5f,
        BlockChance = 5f,
    };

    /// <summary>A level-<paramref name="mobLevel"/> mob hitting a level-60 player with no defenses.</summary>
    private static MeleeRollInput MobVsPlayer(byte mobLevel = 60) => new()
    {
        AttackType = WeaponAttackType.BaseAttack,
        VictimIsPlayer = true,
        AttackerIsCreature = true,
        AttackerIsPlayerControlled = false,
        VictimIsPlayerControlled = true,
        VictimStanding = true,
        AttackerCanCrush = true,
        VictimLevel = 60,
        AttackerMaxSkill = mobLevel * 5,
        VictimMaxSkill = 300,
        AttackerWeaponSkill = mobLevel * 5,
        VictimDefenseSkill = 300,
        BaseCritChance = 5f,
    };

    [Theory]
    [InlineData(0, MeleeHitOutcome.Miss)]
    [InlineData(499, MeleeHitOutcome.Miss)]
    [InlineData(500, MeleeHitOutcome.Dodge)]
    [InlineData(999, MeleeHitOutcome.Dodge)]
    [InlineData(1000, MeleeHitOutcome.Parry)]
    [InlineData(1499, MeleeHitOutcome.Parry)]
    [InlineData(1500, MeleeHitOutcome.Glancing)] // (10 + 0·2)·100 = 1000 wide
    [InlineData(2499, MeleeHitOutcome.Glancing)]
    [InlineData(2500, MeleeHitOutcome.Block)]
    [InlineData(2999, MeleeHitOutcome.Block)]
    [InlineData(3000, MeleeHitOutcome.Normal)] // player crit chance 0 → no crit range
    [InlineData(9999, MeleeHitOutcome.Normal)]
    public void EqualLevelPlayerVsMob_StacksTheRangesInVanillaOrder(int roll, MeleeHitOutcome expected)
    {
        Assert.Equal(expected, MeleeHitTable.Roll(PlayerVsMob(), roll).Outcome);
    }

    [Fact]
    public void RolledFlags_RecordEveryDefenseThatWasRolled()
    {
        MeleeRollResult normal = MeleeHitTable.Roll(PlayerVsMob(), 5000);
        Assert.Equal(HitInfo.RolledDodge | HitInfo.RolledParry | HitInfo.RolledBlock, normal.HitInfo);
        Assert.Equal(HitInfo.Miss, MeleeHitTable.Roll(PlayerVsMob(), 0).HitInfo);
        Assert.Equal(HitInfo.RolledDodge | HitInfo.RolledParry | HitInfo.RolledBlock | HitInfo.Block, MeleeHitTable.Roll(PlayerVsMob(), 2500).HitInfo);
        Assert.Equal(HitInfo.RolledDodge | HitInfo.RolledParry | HitInfo.Glancing, MeleeHitTable.Roll(PlayerVsMob(), 1500).HitInfo);
    }

    [Fact]
    public void EvadingVictim_AlwaysEvades()
    {
        MeleeRollResult r = MeleeHitTable.Roll(PlayerVsMob() with { VictimEvading = true }, 9999);
        Assert.Equal(MeleeHitOutcome.Evade, r.Outcome);
        Assert.Equal(HitInfo.Miss | HitInfo.SwingNoHitSound, r.HitInfo);
    }

    [Fact]
    public void FromBehind_NoParryOrBlock_ButMobsStillDodge()
    {
        MeleeRollInput input = PlayerVsMob() with { FromBehind = true };
        Assert.Equal(MeleeHitOutcome.Dodge, MeleeHitTable.Roll(input, 999).Outcome);
        Assert.Equal(MeleeHitOutcome.Glancing, MeleeHitTable.Roll(input, 1000).Outcome); // parry skipped
        Assert.Equal(MeleeHitOutcome.Normal, MeleeHitTable.Roll(input, 2000).Outcome);   // block skipped
    }

    [Fact]
    public void FromBehind_PlayersCannotDodge()
    {
        MeleeRollInput input = MobVsPlayer() with { DodgeChance = 5f, FromBehind = true };
        Assert.Equal(MeleeHitOutcome.Crit, MeleeHitTable.Roll(input, 500).Outcome); // miss 0..499, then crit 5%
    }

    [Fact]
    public void SittingPlayer_IsAlwaysCritByCreatures()
    {
        MeleeRollInput input = MobVsPlayer() with { VictimStanding = false, BaseCritChance = 0f };
        Assert.Equal(MeleeHitOutcome.Crit, MeleeHitTable.Roll(input, 9999).Outcome);
        Assert.Equal(0f, MeleeHitTable.MissChance(input, 0)); // nobody misses a sitting target
    }

    [Fact]
    public void Level63Boss_CrushesAndGlancesPerVanillaFormulas()
    {
        // Mob 63 vs player 60 (defense 300): miss 5 − 15·0.04 = 4.4% (~440), crit 5 + 15·0.04 =
        // 5.6% (560), crushing (315 − 300)·200 − 1500 = 1500 (boundaries float-rounded as in vmangos).
        MeleeRollInput input = MobVsPlayer(63);
        Assert.Equal(4.4f, MeleeHitTable.MissChance(input, 15), 3);
        Assert.Equal(5.6f, MeleeHitTable.CritChance(input), 3);
        Assert.Equal(MeleeHitOutcome.Miss, MeleeHitTable.Roll(input, 400).Outcome);
        Assert.Equal(MeleeHitOutcome.Crit, MeleeHitTable.Roll(input, 600).Outcome);
        Assert.Equal(MeleeHitOutcome.Crushing, MeleeHitTable.Roll(input, 1100).Outcome);
        Assert.Equal(MeleeHitOutcome.Crushing, MeleeHitTable.Roll(input, 2450).Outcome);
        Assert.Equal(MeleeHitOutcome.Normal, MeleeHitTable.Roll(input, 2550).Outcome);

        Assert.Equal(MeleeHitOutcome.Normal, MeleeHitTable.Roll(input with { AttackerCanCrush = false }, 1100).Outcome);
    }

    [Fact]
    public void PlayerVsLevel63Mob_MissIsEightPercent_AndGlancingCapsAtForty()
    {
        // skill diff 300 − 315 = −15 < −10 → miss 5 + 15·0.2 = 8%; dodge 5% + 15·10/100 = 6.5%.
        MeleeRollInput input = PlayerVsMob(63) with { ParryChance = 0f, BlockChance = 0f };
        Assert.Equal(8f, MeleeHitTable.MissChance(input, -15), 3);
        Assert.Equal(MeleeHitOutcome.Miss, MeleeHitTable.Roll(input, 799).Outcome);
        Assert.Equal(MeleeHitOutcome.Dodge, MeleeHitTable.Roll(input, 800).Outcome);
        Assert.Equal(MeleeHitOutcome.Dodge, MeleeHitTable.Roll(input, 1449).Outcome);
        // glancing (10 + 15·2)·100 = 4000 (the cap)
        Assert.Equal(MeleeHitOutcome.Glancing, MeleeHitTable.Roll(input, 1450).Outcome);
        Assert.Equal(MeleeHitOutcome.Glancing, MeleeHitTable.Roll(input, 5449).Outcome);
        Assert.Equal(MeleeHitOutcome.Normal, MeleeHitTable.Roll(input, 5450).Outcome);
    }

    [Fact]
    public void FirstPercentOfHit_IsIgnoredAgainstMobsMoreThanTenSkillAbove()
    {
        MeleeRollInput input = PlayerVsMob(63) with { HitBonus = 3f };
        Assert.Equal(6f, MeleeHitTable.MissChance(input, -15), 3); // 8 − (3 − 1)
        Assert.Equal(2f, MeleeHitTable.MissChance(PlayerVsMob() with { HitBonus = 3f }, 0), 3);
    }

    [Fact]
    public void DualWield_AddsNineteenPercentMiss_AndIsCappedAtSixty()
    {
        Assert.Equal(24f, MeleeHitTable.MissChance(PlayerVsMob() with { DualWield = true }, 0), 3);
        Assert.Equal(60f, MeleeHitTable.MissChance(PlayerVsMob() with { DualWield = true }, -300), 3);
    }

    [Fact]
    public void LowLevelMobs_ScaleMissAndDefenses()
    {
        // level 5 mob: miss 5·0.5 = 2.5% → 250; dodge (500 − 0)·0.5 = 250
        MeleeRollInput input = PlayerVsMob(5) with { AttackerMaxSkill = 25, AttackerWeaponSkill = 25 };
        Assert.Equal(MeleeHitOutcome.Miss, MeleeHitTable.Roll(input, 249).Outcome);
        Assert.Equal(MeleeHitOutcome.Dodge, MeleeHitTable.Roll(input, 250).Outcome);
        Assert.Equal(MeleeHitOutcome.Parry, MeleeHitTable.Roll(input, 500).Outcome);
    }

    [Fact]
    public void CreatureVictimFlags_DisableParryAndBlock()
    {
        MeleeRollInput input = PlayerVsMob() with { VictimCreatureCanParry = false, VictimCreatureCanBlock = false };
        Assert.Equal(MeleeHitOutcome.Glancing, MeleeHitTable.Roll(input, 1000).Outcome);
        Assert.Equal(MeleeHitOutcome.Normal, MeleeHitTable.Roll(input, 2000).Outcome);
    }

    [Fact]
    public void CritChance_UsesSkillDifferenceRules()
    {
        // vs player: 0.04 per point either way
        Assert.Equal(4.4f, MeleeHitTable.CritChance(MobVsPlayer() with { AttackerWeaponSkill = 285 }), 3);
        // vs mob with a lower weapon skill: 0.2 per capped point
        Assert.Equal(2f, MeleeHitTable.CritChance(PlayerVsMob() with { BaseCritChance = 5f, AttackerWeaponSkill = 285 }), 3);
        Assert.Equal(0f, MeleeHitTable.CritChance(PlayerVsMob() with { BaseCritChance = 1f, AttackerWeaponSkill = 285 }));
    }

    [Theory]
    [InlineData(100u, 0f, 60, 100u)]
    [InlineData(100u, 3000f, 60, 64u)]   // 300 / 550 = 0.545 → 0.353 reduction
    [InlineData(100u, 100000f, 60, 25u)] // capped at 75%
    [InlineData(0u, 0f, 1, 1u)]          // at least 1
    public void ArmorMitigation_FollowsTheVanillaFormula(uint damage, float armor, int level, uint expected)
    {
        Assert.Equal(expected, MeleeHitTable.ApplyArmor(damage, armor, level));
    }

    [Fact]
    public void GlancingRange_FollowsBaeza_WithCasterPenalty()
    {
        (float low, float high) = MeleeHitTable.GlancingRange(315, 300, Class.Warrior);
        Assert.Equal(0.55f, low, 3);
        Assert.Equal(0.75f, high, 3);

        (low, high) = MeleeHitTable.GlancingRange(315, 300, Class.Mage);
        Assert.Equal(0.01f, low, 3);
        Assert.Equal(0.45f, high, 3);

        (low, high) = MeleeHitTable.GlancingRange(300, 300, Class.Rogue);
        Assert.Equal(0.91f, low, 3);
        Assert.Equal(0.99f, high, 3);
    }
}

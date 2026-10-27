using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Hunter;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.ClassSpells;

/// <summary>
/// Improved Scorpid Sting (vmangos Aura::HandleAuraModStat, SpellAuras.cpp:4650-4677). Build 5875 shapes: Scorpid Sting 3043 is SPELLFAMILY_HUNTER
/// with CF_HUNTER_SCORPID_STING (0x8000) and two MOD_STAT effects of -20 (strength, agility); the talent ranks 19491/19493/19494 are a passive
/// OVERRIDE_CLASS_SCRIPTS aura of misc 2388/2389/2390; 19486 is the MOD_STAT stamina aura the caster casts with the computed amount.
/// </summary>
public sealed class ImprovedScorpidStingTests
{
    private const uint ScorpidSting = 3043;
    private const uint Stamina = ImprovedScorpidStingModule.StaminaSpell;
    private const int StaminaField = UpdateFields.UnitFieldStat0 + 2;

    private static SpellInfo Instant(SpellInfo spell) => spell with { StartRecoveryCategory = 0, StartRecoveryTime = 0, SpellVisual = 1 };

    private static SpellInfo AtEnemy(SpellInfo spell) => Instant(spell with { RangeIndex = 4, Range = new SpellRange(0, 30) });

    private static SpellTestKit Kit(uint talentSpell, int classScript, ulong stingFlags = 0x8000) => new(
        Instant(Spell(talentSpell, Effect(SpellEffectName.ApplyAura, 1, SpellImplicitTarget.UnitCaster, AuraType.OverrideClassScripts, misc: classScript)) with
        {
            SpellFamilyName = ImprovedScorpidStingModule.HunterFamily,
            Attributes = SpellAttributes.Passive,
        }),
        AtEnemy(Spell(
            ScorpidSting,
            Effect(SpellEffectName.ApplyAura, -20, SpellImplicitTarget.UnitEnemy, AuraType.ModStat, misc: 0),
            Effect(SpellEffectName.ApplyAura, -20, SpellImplicitTarget.UnitEnemy, AuraType.ModStat, misc: 1)) with
        {
            School = SpellSchool.Nature,
            SpellFamilyName = ImprovedScorpidStingModule.HunterFamily,
            SpellFamilyFlags = stingFlags,
            Attributes = SpellAttributes.AuraIsDebuff,
            Duration = new SpellDuration(20_000, 0, 20_000),
        }),
        AtEnemy(Spell(Stamina, Effect(SpellEffectName.ApplyAura, 1, SpellImplicitTarget.UnitEnemy, AuraType.ModStat, misc: 2)) with
        {
            Attributes = SpellAttributes.AuraIsDebuff,
            Duration = new SpellDuration(20_000, 0, 20_000),
        }));

    /// <summary>
    /// With spell batching on (<c>ARCANECORE_TEST_SPELL_BATCH_MS</c>) a cast that goes off is held until the next window boundary: advance
    /// the clock to it so the held go resolves. A no-op with the window at 0.
    /// </summary>
    private static void FlushSpellBatch(SpellTestKit kit)
    {
        uint window = kit.System.SpellBatchWindowMs;
        if (window != 0)
        {
            kit.Advance(window - (kit.Now % window));
        }
    }

    private static (SpellTestKit Kit, Player Hunter, Player Victim) Rig(uint talentSpell, int classScript, ulong stingFlags = 0x8000)
    {
        SpellTestKit kit = Kit(talentSpell, classScript, stingFlags);
        (Player hunter, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 3, 0);
        kit.System.Relations = new FakeRelations { Hostile = { victim.Guid } };
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(hunter, talentSpell, SpellCastTargets.ForSelf(), triggered: true));
        Assert.True(kit.System.HasAura(hunter, talentSpell));
        return (kit, hunter, victim);
    }

    [Theory]
    [InlineData(19491u, 2388, -2)]
    [InlineData(19493u, 2389, -4)]
    [InlineData(19494u, 2390, -6)]
    public void ScorpidSting_TakesTheTalentsShareOfTheStrengthAmountAsStamina_AndTheStaminaLeavesWithTheSting(uint talentSpell, int classScript, int expected)
    {
        (SpellTestKit kit, Player hunter, Player victim) = Rig(talentSpell, classScript);
        using (kit)
        {
            int before = victim.GetInt32(StaminaField);

            Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(hunter, ScorpidSting, SpellCastTargets.ForUnit(victim.Guid), triggered: false));
            FlushSpellBatch(kit);

            Assert.True(kit.System.HasAura(victim, ScorpidSting));
            Assert.True(kit.System.HasAura(victim, Stamina));
            Assert.Equal(before + expected, victim.GetInt32(StaminaField));

            kit.System.RemoveAuras(victim, ScorpidSting);

            Assert.False(kit.System.HasAura(victim, Stamina));
            Assert.Equal(before, victim.GetInt32(StaminaField));
        }
    }

    [Fact]
    public void WithoutTheTalentsClassScript_ScorpidStingTakesNoStamina()
    {
        (SpellTestKit kit, Player hunter, Player victim) = Rig(19491, 9999);
        using (kit)
        {
            int before = victim.GetInt32(StaminaField);

            Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(hunter, ScorpidSting, SpellCastTargets.ForUnit(victim.Guid), triggered: false));
            FlushSpellBatch(kit);

            Assert.True(kit.System.HasAura(victim, ScorpidSting));
            Assert.False(kit.System.HasAura(victim, Stamina));
            Assert.Equal(before, victim.GetInt32(StaminaField));
        }
    }

    [Fact]
    public void AStrengthDebuffOutsideTheScorpidStingFamilyMask_IsNotScripted()
    {
        (SpellTestKit kit, Player hunter, Player victim) = Rig(19494, 2390, stingFlags: 0x4000);
        using (kit)
        {
            Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(hunter, ScorpidSting, SpellCastTargets.ForUnit(victim.Guid), triggered: false));
            FlushSpellBatch(kit);

            Assert.True(kit.System.HasAura(victim, ScorpidSting));
            Assert.False(kit.System.HasAura(victim, Stamina));
        }
    }

    [Theory]
    [InlineData(2388, 10)]
    [InlineData(2389, 20)]
    [InlineData(2390, 30)]
    public void StaminaPercent_IsTheVmangosTable(int classScript, int percent)
        => Assert.Equal(percent, ImprovedScorpidStingModule.StaminaPercent(classScript));
}

using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Skills;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Skills;
using ArcaneCore.Kernel.Skills;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

public sealed class SkillAuraModuleTests
{
    private const uint Temporary = 931001;
    private const uint Permanent = 931002;
    private const uint Negative = 931003;
    private const uint Stackable = 931004;
    private const uint OtherSkill = 931005;
    private const uint InvalidSkill = 931006;

    [Fact]
    public void ModuleIsDiscoveredAndBothAuraTypesHaveHandlers()
    {
        using var kit = Kit();
        Assert.Contains(typeof(SkillAuras), kit.System.Modules);
        Assert.True(kit.System.HasAuraHandler(AuraType.ModSkill));
        Assert.True(kit.System.HasAuraHandler(AuraType.ModSkillTalent));
    }

    [Fact]
    public void BonusesUseSeparateSignedHalvesAndDoNotPersistOrRaisePureSkill()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        PlayerSkills skills = Attach(player);
        skills.Set(SkillIds.Swords, 20, 75);
        skills.TakeSnapshot();

        Cast(kit, player, Temporary, Permanent, Negative);

        Assert.Equal(7, skills.GetBonus(SkillIds.Swords));
        Assert.Equal(5, skills.GetBonus(SkillIds.Swords, permanent: true));
        Assert.Equal(20, skills.GetValuePure(SkillIds.Swords));
        Assert.Equal(25, skills.GetValueBase(SkillIds.Swords));
        Assert.Equal(32, skills.GetValue(SkillIds.Swords));
        Assert.Equal(87, skills.GetMax(SkillIds.Swords));
        Assert.False(skills.IsDirty);
        Assert.Equal(new CharacterSkillRow((ushort)SkillIds.Swords, 20, 75), Assert.Single(skills.TakeSnapshot().Skills));

        kit.System.RemoveAuras(player, Temporary);
        Assert.Equal(-3, skills.GetBonus(SkillIds.Swords));
        kit.System.RemoveAuras(player, Negative);
        kit.System.RemoveAuras(player, Permanent);
        Assert.Equal(0, skills.GetBonus(SkillIds.Swords));
        Assert.Equal(0, skills.GetBonus(SkillIds.Swords, permanent: true));
    }

    [Fact]
    public void StacksReplaceTheirPreviousContributionAndRemovalKeepsOtherBonuses()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        PlayerSkills skills = Attach(player);
        skills.Set(SkillIds.Swords, 20, 75);
        skills.ModifyBonus(SkillIds.Swords, 2); // An independent equipment-like source.
        Cast(kit, player, Temporary, Stackable, Stackable, Stackable, Stackable);

        Assert.Equal(24, skills.GetBonus(SkillIds.Swords)); // 2 + 10 + capped 3 * 4.
        kit.System.RemoveAuras(player, Stackable);
        Assert.Equal(12, skills.GetBonus(SkillIds.Swords));
        kit.System.RemoveAuras(player, Temporary);
        kit.System.RemoveAuras(player, Temporary); // Idempotent removal.
        Assert.Equal(2, skills.GetBonus(SkillIds.Swords));
    }

    [Fact]
    public void ForgetAndRelearnReapplyEveryContributionOnceToTheNewSlot()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        PlayerSkills skills = Attach(player);
        skills.Set(SkillIds.Swords, 20, 75);
        Cast(kit, player, Temporary, Permanent, Negative, Stackable, Stackable);
        Assert.Equal(15, skills.GetBonus(SkillIds.Swords));

        skills.Set(SkillIds.Swords, 0, 0);
        Assert.False(skills.Has(SkillIds.Swords));
        Assert.Equal(0u, player.GetUInt32(SkillTestKit.SlotIndex(0) + 2));
        skills.Set(SkillIds.Axes, 10, 75); // Reuse the cleared slot for another skill.
        skills.Set(SkillIds.Swords, 25, 75);

        Assert.Equal(0, skills.GetBonus(SkillIds.Axes));
        Assert.Equal(15, skills.GetBonus(SkillIds.Swords));
        Assert.Equal(5, skills.GetBonus(SkillIds.Swords, permanent: true));
        skills.Set(SkillIds.Swords, 30, 75); // Updating an existing skill must not reapply.
        Assert.Equal(50, skills.GetValue(SkillIds.Swords));
        kit.System.RemoveAuras(player, Stackable);
        Assert.Equal(7, skills.GetBonus(SkillIds.Swords));
    }

    [Fact]
    public void AuraOfUnknownSkillWaitsForLearningAndRemovedAuraNeverApplies()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        PlayerSkills skills = Attach(player);
        Cast(kit, player, Temporary, Permanent);
        Assert.False(skills.Has(SkillIds.Swords));
        kit.System.RemoveAuras(player, Temporary);

        skills.Set(SkillIds.Swords, 20, 75);

        Assert.Equal(0, skills.GetBonus(SkillIds.Swords));
        Assert.Equal(5, skills.GetBonus(SkillIds.Swords, permanent: true));
        kit.System.RemoveAuras(player, Permanent);
        Assert.Equal(20, skills.GetValue(SkillIds.Swords));
    }

    [Fact]
    public void RemovingAnAuraWhileItsSkillIsForgottenDoesNotCreateNegativeBonusOnRelearn()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        PlayerSkills skills = Attach(player);
        skills.Set(SkillIds.Swords, 20, 75);
        Cast(kit, player, Temporary, Permanent);
        skills.Set(SkillIds.Swords, 0, 0);
        kit.System.RemoveAuras(player, Temporary);
        skills.Set(SkillIds.Swords, 20, 75);
        Assert.Equal(0, skills.GetBonus(SkillIds.Swords));
        Assert.Equal(5, skills.GetBonus(SkillIds.Swords, permanent: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForgottenSpellStateCannotReapplyAnOldAuraWhenSkillsAreLaterLearned(bool knownSkill)
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        PlayerSkills skills = Attach(player);
        if (knownSkill)
        {
            skills.Set(SkillIds.Swords, 20, 75);
        }

        Cast(kit, player, Temporary);
        kit.System.RemoveUnit(player);
        if (knownSkill)
        {
            skills.Set(SkillIds.Swords, 0, 0);
        }

        skills.Set(SkillIds.Swords, 20, 75);
        Assert.Equal(0, skills.GetBonus(SkillIds.Swords));
    }

    [Fact]
    public void AuraBeforeSkillsAttachmentAndLoadReceivesOneBonusAfterSlotsAreRestored()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        Cast(kit, player, Temporary, Permanent);

        PlayerSkills skills = Attach(player);
        skills.Load([new CharacterSkillRow((ushort)SkillIds.Swords, 20, 75)], []);

        Assert.Equal(10, skills.GetBonus(SkillIds.Swords));
        Assert.Equal(5, skills.GetBonus(SkillIds.Swords, permanent: true));
        Assert.False(skills.IsDirty);
        kit.System.RemoveAuras(player, Temporary);
        Assert.Equal(25, skills.GetValue(SkillIds.Swords));
    }

    [Fact]
    public void AttachingAlreadyLoadedSkillsRebuildsPendingAuraAndClearedAuraStaysCleared()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        Cast(kit, player, Temporary, Permanent);
        kit.System.RemoveAuras(player, Temporary);
        PlayerSkills skills = CreateSkills(player);
        skills.Load([new CharacterSkillRow((ushort)SkillIds.Swords, 20, 75)], []);

        player.AttachSkills(skills);

        Assert.Equal(0, skills.GetBonus(SkillIds.Swords));
        Assert.Equal(5, skills.GetBonus(SkillIds.Swords, permanent: true));
    }

    [Fact]
    public void RestoredStackAmountIsAlreadyMultipliedAndDoesNotPersistIntoSkillRow()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        PlayerSkills skills = Attach(player);
        skills.Load([new CharacterSkillRow((ushort)SkillIds.Swords, 20, 75)], []);
        var saved = new PersistedAura
        {
            SpellId = Stackable,
            CasterGuid = player.Guid,
            CasterLevel = player.Level,
            EffectMask = 0b001,
            Amounts = [12, 0, 0],
            StackAmount = 3,
            MaxDurationMs = 60_000,
            RemainingMs = 30_000,
            SavedAtUnixMs = 1_800_000_000_000,
        };

        Assert.Single(kit.System.RestoreAuras(player, [saved], saved.SavedAtUnixMs));

        Assert.Equal(12, skills.GetBonus(SkillIds.Swords));
        Assert.Equal(20, Assert.Single(skills.TakeSnapshot().Skills).Value);
        kit.System.RemoveAuras(player, Stackable);
        Assert.Equal(0, skills.GetBonus(SkillIds.Swords));
    }

    [Fact]
    public void ExpirationAndDeathUnapplyTemporaryBonusWhilePassiveTalentSurvivesDeath()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        PlayerSkills skills = Attach(player);
        skills.Set(SkillIds.Swords, 20, 75);
        Cast(kit, player, Temporary, Permanent);
        kit.Advance(60_000);
        Assert.Equal(0, skills.GetBonus(SkillIds.Swords));
        Assert.Equal(5, skills.GetBonus(SkillIds.Swords, permanent: true));
        Cast(kit, player, Temporary);
        player.Health = 0;
        kit.System.OnUnitDied(player);
        Assert.Equal(0, skills.GetBonus(SkillIds.Swords));
        Assert.Equal(5, skills.GetBonus(SkillIds.Swords, permanent: true));
    }

    [Fact]
    public void BonusChangesNotifySkillChangedAndTargetOnlyTheirOwnSkill()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        PlayerSkills skills = Attach(player);
        skills.Set(SkillIds.Swords, 20, 75);
        skills.Set(SkillIds.Axes, 15, 75);
        var changed = new List<(uint Skill, ushort Value)>();
        skills.SkillChanged += id => changed.Add((id, skills.GetValue(id)));

        Cast(kit, player, OtherSkill, InvalidSkill);
        Assert.Equal(20, skills.GetValue(SkillIds.Swords));
        Assert.Equal((SkillIds.Axes, (ushort)22), Assert.Single(changed));
        kit.System.RemoveAuras(player, OtherSkill);
        Assert.Equal((SkillIds.Axes, (ushort)15), changed[1]);
    }

    private static PlayerSkills CreateSkills(Player player)
        => new(player, SkillTestKit.Catalog(), new SkillOptions(), new FakeSkillSpellHost());

    private static PlayerSkills Attach(Player player)
    {
        PlayerSkills skills = CreateSkills(player);
        player.AttachSkills(skills);
        return skills;
    }

    private static void Cast(SpellTestKit kit, Player player, params uint[] spells)
    {
        foreach (uint spell in spells)
        {
            Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, spell, SpellCastTargets.ForSelf(), triggered: true));
        }
    }

    private static SpellInfo Bonus(uint id, int amount, AuraType type, uint skill = SkillIds.Swords)
        => Spell(id, Effect(SpellEffectName.ApplyAura, amount, aura: type, misc: (int)skill)) with
        {
            Duration = new SpellDuration(60_000, 0, 60_000),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };

    private static SpellTestKit Kit() => new(
        Bonus(Temporary, 10, AuraType.ModSkill),
        Bonus(Permanent, 5, AuraType.ModSkillTalent) with
        {
            Attributes = SpellAttributes.Passive,
            Duration = new SpellDuration(-1, 0, -1),
        },
        Bonus(Negative, -3, AuraType.ModSkill),
        Bonus(Stackable, 4, AuraType.ModSkill) with { StackAmount = 3 },
        Bonus(OtherSkill, 7, AuraType.ModSkill, SkillIds.Axes),
        Bonus(InvalidSkill, 99, AuraType.ModSkill, 65536 + SkillIds.Swords));
}

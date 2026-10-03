using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Skills;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Skills.SkillTestKit;

namespace ArcaneCore.Game.Tests.Skills;

/// <summary>
/// PlayerSkills against vmangos Player.cpp (the cited line ranges): the update-field layout, SetSkill,
/// the gain functions, level updates, spell-trained skills and skill-trained spells, persistence state.
/// </summary>
public sealed class PlayerSkillsTests
{
    [Fact]
    public void SetSkill_WritesThreeWordsPerSlot_IdStepValueMaxBonus()
    {
        (Player player, PlayerSkills skills, _, _) = CreateSkills();

        Assert.True(skills.Set(SkillIds.Swords, 1, 5));
        Assert.Equal(SkillIds.Swords, player.GetUInt32(SlotIndex(0)));
        Assert.Equal(1u | (5u << 16), player.GetUInt32(SlotIndex(0) + 1));
        Assert.Equal(0u, player.GetUInt32(SlotIndex(0) + 2));

        Assert.True(skills.Set(SkillIds.Mining, 1, 75, step: 1));
        Assert.Equal(SkillIds.Mining | (1u << 16), player.GetUInt32(SlotIndex(1)));
        Assert.Equal(1u | (75u << 16), player.GetUInt32(SlotIndex(1) + 1));
        Assert.Equal(1, skills.GetStep(SkillIds.Mining));

        // A new step rewrites word 0 only when non-zero (Player.cpp:5515-5517).
        Assert.True(skills.Set(SkillIds.Mining, 10, 150, step: 2));
        Assert.Equal(SkillIds.Mining | (2u << 16), player.GetUInt32(SlotIndex(1)));
        Assert.True(skills.Set(SkillIds.Mining, 11, 150));
        Assert.Equal(SkillIds.Mining | (2u << 16), player.GetUInt32(SlotIndex(1)));
        Assert.Equal(11u | (150u << 16), player.GetUInt32(SlotIndex(1) + 1));
    }

    [Fact]
    public void Bonus_TemporaryInTheLowHalf_PermanentInTheHigh_AndNegativeBonusesBorrow()
    {
        (Player player, PlayerSkills skills, _, _) = CreateSkills();
        skills.Set(SkillIds.Swords, 100, 300);

        Assert.True(skills.ModifyBonus(SkillIds.Swords, 10));
        Assert.True(skills.ModifyBonus(SkillIds.Swords, 5, permanent: true));
        Assert.Equal(10u | (5u << 16), player.GetUInt32(SlotIndex(0) + 2));
        Assert.Equal((short)10, skills.GetBonus(SkillIds.Swords));
        Assert.Equal((short)5, skills.GetBonus(SkillIds.Swords, permanent: true));

        // GetSkill: pure value, + permanent, + both (Player.cpp:5668-5701).
        Assert.Equal((ushort)100, skills.GetValuePure(SkillIds.Swords));
        Assert.Equal((ushort)105, skills.GetValueBase(SkillIds.Swords));
        Assert.Equal((ushort)115, skills.GetValue(SkillIds.Swords));
        Assert.Equal((ushort)300, skills.GetMaxPure(SkillIds.Swords));
        Assert.Equal((ushort)315, skills.GetMax(SkillIds.Swords));

        Assert.True(skills.ModifyBonus(SkillIds.Swords, -30));
        Assert.Equal(unchecked((ushort)(short)-20) | (5u << 16), player.GetUInt32(SlotIndex(0) + 2));
        Assert.Equal((ushort)85, skills.GetValue(SkillIds.Swords));
        Assert.True(skills.ModifyBonus(SkillIds.Swords, -500, permanent: true));
        Assert.Equal((ushort)0, skills.GetValue(SkillIds.Swords));   // never below zero

        Assert.False(skills.ModifyBonus(SkillIds.Swords, 0));
        Assert.False(skills.ModifyBonus(SkillIds.Axes, 5));
    }

    [Fact]
    public void SetSkill_TakesTheFirstFreeSlot_Max127_AndRemovingClearsTheThreeWords()
    {
        SkillCatalog catalog = Catalog(lines => lines.Concat(Enumerable.Range(1000, 130).Select(id => new SkillLineRecord((uint)id, SkillCategories.Weapon, $"s{id}", 0))));
        (Player player, PlayerSkills skills, _, _) = CreateSkills(catalog: catalog);

        for (uint id = 1000; id < 1000 + PlayerSkills.MaxSkills; id++)
        {
            Assert.True(skills.Set(id, 1, 5));
        }

        Assert.False(skills.Set(1127, 1, 5));   // no free slot (PLAYER_MAX_SKILLS 127)
        Assert.False(skills.Has(1127));

        Assert.True(skills.Set(1003, 0, 0));    // remove slot 3
        Assert.False(skills.Has(1003));
        Assert.Equal(0u, player.GetUInt32(SlotIndex(3)));
        Assert.Equal(0u, player.GetUInt32(SlotIndex(3) + 1));
        Assert.Equal(0u, player.GetUInt32(SlotIndex(3) + 2));
        Assert.Equal((ushort)0, skills.GetValue(1003));

        Assert.True(skills.Set(1127, 7, 9));    // takes the freed slot
        Assert.Equal(1127u, player.GetUInt32(SlotIndex(3)));
        Assert.Equal(7u | (9u << 16), player.GetUInt32(SlotIndex(3) + 1));
    }

    [Fact]
    public void SetSkill_RefusesIdZero_UnknownLines_AndRemovalOfWhatIsNotKnown()
    {
        (_, PlayerSkills skills, _, _) = CreateSkills();
        Assert.False(skills.Set(0, 1, 5));
        Assert.False(skills.Set(55555, 1, 5));   // not in SkillLine.dbc (Player.cpp:5609-5614)
        Assert.False(skills.Set(SkillIds.Swords, 0, 0));
        Assert.False(skills.Has(SkillIds.Swords));
    }

    [Fact]
    public void SetSkill_RaisesAddedRemovingRemovedAndChanged_InOrder()
    {
        (_, PlayerSkills skills, _, _) = CreateSkills();
        var log = new List<string>();
        skills.SkillAdded += id => log.Add($"added {id}");
        skills.SkillRemoving += id => log.Add($"removing {id} has={skills.Has(id)}");
        skills.SkillRemoved += id => log.Add($"removed {id} has={skills.Has(id)}");
        skills.SkillChanged += id => log.Add($"changed {id}");

        skills.Set(SkillIds.Swords, 1, 5);
        skills.Set(SkillIds.Swords, 2, 5);
        skills.Set(SkillIds.Swords, 0, 0);

        Assert.Equal(
        [
            "added 43", "changed 43",
            "changed 43",
            "removing 43 has=True", "removed 43 has=False", "changed 43",
        ], log);
    }

    [Fact]
    public void Update_AddsStepCappedAtMax_FalseWhenAtMaxOrUnknown()
    {
        (Player player, PlayerSkills skills, _, _) = CreateSkills();
        skills.Set(SkillIds.Swords, 4, 5);
        Assert.True(skills.Update(SkillIds.Swords, 1));
        Assert.Equal(5u | (5u << 16), player.GetUInt32(SlotIndex(0) + 1));
        Assert.False(skills.Update(SkillIds.Swords, 1));        // at max
        Assert.False(skills.Update(SkillIds.Axes, 1));          // unknown
        skills.Set(SkillIds.Axes, 4, 5);
        Assert.True(skills.Update(SkillIds.Axes, 10));
        Assert.Equal((ushort)5, skills.GetValuePure(SkillIds.Axes));
    }

    [Fact]
    public void UpdatePro_RollsLeChance_AndReportsTrueOnAMissWhileBelowMax()
    {
        (_, PlayerSkills skills, _, ScriptedSkillRandom random) = CreateSkills();
        skills.Set(SkillIds.Mining, 10, 75, 1);

        random.Ints.Enqueue(500);
        Assert.True(skills.UpdatePro(SkillIds.Mining, 500, 1));      // roll == chance: success
        Assert.Equal((ushort)11, skills.GetValuePure(SkillIds.Mining));

        random.Ints.Enqueue(501);
        Assert.True(skills.UpdatePro(SkillIds.Mining, 500, 1));      // miss still returns true (Player.cpp:5334-5338)
        Assert.Equal((ushort)11, skills.GetValuePure(SkillIds.Mining));

        Assert.False(skills.UpdatePro(SkillIds.Mining, 0, 1));       // chance <= 0 is rejected before the roll
        Assert.False(skills.UpdatePro(SkillIds.Mining, -5, 1));
        Assert.False(skills.UpdatePro(SkillIds.Herbalism, 1000, 1)); // unknown skill

        skills.Set(SkillIds.Mining, 75, 75, 1);
        Assert.False(skills.UpdatePro(SkillIds.Mining, 1000, 1));    // at max
    }

    [Fact]
    public void UpdateCraft_UsesTheFirstSkilledAbilityAndItsTrivialRanks()
    {
        // Smelt Copper: ability with max_value (grey) 100 and min_value (yellow) 25.
        (_, PlayerSkills skills, _, ScriptedSkillRandom random) = CreateSkills();
        skills.Set(SkillIds.Blacksmithing, 1, 75, 1);

        // skill 1 < yellow 25: orange 100% -> 1000 per mille: any roll wins.
        random.Ints.Enqueue(1000);
        Assert.True(skills.UpdateCraft(SmeltCopper));
        Assert.Equal((ushort)2, skills.GetValuePure(SkillIds.Blacksmithing));

        // skill 30: yellow (>= 25, < 62): 75% -> 750; a roll of 751 misses.
        skills.Set(SkillIds.Blacksmithing, 30, 75, 1);
        random.Ints.Enqueue(751);
        Assert.True(skills.UpdateCraft(SmeltCopper));
        Assert.Equal((ushort)30, skills.GetValuePure(SkillIds.Blacksmithing));
        random.Ints.Enqueue(750);
        Assert.True(skills.UpdateCraft(SmeltCopper));
        Assert.Equal((ushort)31, skills.GetValuePure(SkillIds.Blacksmithing));

        // skill 100 is grey (>= trivial high 100): chance 0 -> false, no roll.
        skills.Set(SkillIds.Blacksmithing, 100, 150, 2);
        Assert.False(skills.UpdateCraft(SmeltCopper));
        Assert.False(skills.UpdateCraft(99999));       // not a recipe
    }

    [Fact]
    public void UpdateGather_DispatchesBySkill_AndOtherSkillsReturnFalse()
    {
        (_, PlayerSkills skills, _, ScriptedSkillRandom random) = CreateSkills();
        skills.Set(SkillIds.Herbalism, 1, 75, 1);
        skills.Set(SkillIds.Mining, 80, 150, 2);

        random.Ints.Enqueue(1000);
        Assert.True(skills.UpdateGather(SkillIds.Herbalism, 1, 1));
        Assert.Equal((ushort)2, skills.GetValuePure(SkillIds.Herbalism));

        // mining req 65, skill 80: orange 1000, halved once (80 / 75 = 1) = 500.
        random.Ints.Enqueue(501);
        Assert.True(skills.UpdateGather(SkillIds.Mining, 80, 65));
        Assert.Equal((ushort)80, skills.GetValuePure(SkillIds.Mining));
        random.Ints.Enqueue(500);
        Assert.True(skills.UpdateGather(SkillIds.Mining, 80, 65));
        Assert.Equal((ushort)81, skills.GetValuePure(SkillIds.Mining));

        Assert.False(skills.UpdateGather(SkillIds.Swords, 1, 1));
    }

    [Fact]
    public void UpdateFishing_FollowsTheFishingCurve()
    {
        (_, PlayerSkills skills, _, ScriptedSkillRandom random) = CreateSkills();
        skills.Set(SkillIds.Fishing, 100, 150, 2);
        // 2500 / (100 - 50) = 50 percent -> 500 per mille.
        random.Ints.Enqueue(501);
        Assert.True(skills.UpdateFishing());
        Assert.Equal((ushort)100, skills.GetValuePure(SkillIds.Fishing));
        random.Ints.Enqueue(500);
        Assert.True(skills.UpdateFishing());
        Assert.Equal((ushort)101, skills.GetValuePure(SkillIds.Fishing));
    }

    [Fact]
    public void UpdateCombatSkills_RollsAgainstTheGoldenChance_AndGainsTheWeaponSkill()
    {
        (_, PlayerSkills skills, _, ScriptedSkillRandom random) = CreateSkills(level: 10);
        skills.Set(SkillIds.Swords, 20, 50);

        var weapon = new CombatSkillContext(false, SkillAttack.Base, 10, false, false, SkillIds.Swords, true, 0f);

        // level 10 skill 20: chance 100 -> rand in [0,100) is always lower.
        random.Floats.Enqueue(99.99f);
        Assert.True(skills.UpdateCombatSkills(weapon));
        Assert.Equal((ushort)21, skills.GetValuePure(SkillIds.Swords));

        // skill 48 (diff 2): ~4.9 percent: 4.8 wins, 5.0 loses.
        skills.Set(SkillIds.Swords, 48, 50);
        random.Floats.Enqueue(5.0f);
        Assert.False(skills.UpdateCombatSkills(weapon));
        random.Floats.Enqueue(4.8f);
        Assert.True(skills.UpdateCombatSkills(weapon));
        Assert.Equal((ushort)49, skills.GetValuePure(SkillIds.Swords));
    }

    [Fact]
    public void UpdateCombatSkills_NoGainVsPlayers_NoWeaponGainShapeshifted_NoGainForFishingPoles_UnarmedForEmptyHands()
    {
        (_, PlayerSkills skills, _, ScriptedSkillRandom random) = CreateSkills(level: 10);
        skills.Set(SkillIds.Swords, 20, 50);
        skills.Set(SkillIds.Unarmed, 20, 50);
        skills.Set(SkillIds.Defense, 20, 50);
        random.Floats.Enqueue(0f);
        random.Floats.Enqueue(0f);
        random.Floats.Enqueue(0f);
        random.Floats.Enqueue(0f);
        random.Floats.Enqueue(0f);

        Assert.False(skills.UpdateCombatSkills(new CombatSkillContext(false, SkillAttack.Base, 10, true, false, SkillIds.Swords, true, 0f)));          // PvP victim
        Assert.False(skills.UpdateCombatSkills(new CombatSkillContext(false, SkillAttack.Base, 10, false, true, SkillIds.Swords, true, 0f)));          // feral form
        Assert.False(skills.UpdateCombatSkills(new CombatSkillContext(false, SkillAttack.Base, 10, false, false, SkillIds.Swords, false, 0f)));         // fishing pole
        Assert.Equal((ushort)20, skills.GetValuePure(SkillIds.Swords));

        Assert.True(skills.UpdateCombatSkills(new CombatSkillContext(false, SkillAttack.Base, 10, false, false, SkillIds.Unarmed, true, 0f)));
        Assert.Equal((ushort)21, skills.GetValuePure(SkillIds.Unarmed));

        // Defence still gains while shapeshifted (the form check is for weapon skill only).
        Assert.True(skills.UpdateCombatSkills(new CombatSkillContext(true, SkillAttack.Base, 12, false, true, 0, false, 0f)));
        Assert.Equal((ushort)21, skills.GetValuePure(SkillIds.Defense));

        // An empty off hand has no skill at all.
        Assert.False(skills.UpdateCombatSkills(new CombatSkillContext(false, SkillAttack.Off, 10, false, false, 0, true, 0f)));
    }

    [Fact]
    public void UpdateCombatSkills_AtTheMaximumForTheLevel_RollsNothing()
    {
        (_, PlayerSkills skills, _, ScriptedSkillRandom random) = CreateSkills(level: 10);
        skills.Set(SkillIds.Swords, 50, 100);
        random.Floats.Enqueue(0f);
        Assert.False(skills.UpdateCombatSkills(new CombatSkillContext(false, SkillAttack.Base, 10, false, false, SkillIds.Swords, true, 0f)));
        Assert.Single(random.Floats);   // the roll was never drawn
    }

    [Fact]
    public void UpdateSkillsForLevel_OnlyLevelRangeSkills_FollowTheLevel_AlwaysMaxFlagAndOption()
    {
        (Player player, PlayerSkills skills, _, _) = CreateSkills(level: 10);
        skills.Set(SkillIds.Swords, 12, 50);        // level range
        skills.Set(SkillIds.Unarmed, 50, 50);       // level range, ALWAYS_MAX flag
        skills.Set(SkillIds.Mining, 5, 75, 1);      // rank range: untouched
        skills.Set(SkillIds.PlateMail, 1, 1);       // mono: untouched (max 1)
        skills.Set(SkillIds.LanguageCommon, 300, 300);

        player.Level = 11;
        skills.UpdateSkillsForLevel();
        Assert.Equal(((ushort)12, (ushort)55), (skills.GetValuePure(SkillIds.Swords), skills.GetMaxPure(SkillIds.Swords)));
        Assert.Equal(((ushort)55, (ushort)55), (skills.GetValuePure(SkillIds.Unarmed), skills.GetMaxPure(SkillIds.Unarmed)));
        Assert.Equal(((ushort)5, (ushort)75), (skills.GetValuePure(SkillIds.Mining), skills.GetMaxPure(SkillIds.Mining)));
        Assert.Equal(((ushort)1, (ushort)1), (skills.GetValuePure(SkillIds.PlateMail), skills.GetMaxPure(SkillIds.PlateMail)));
        Assert.Equal(((ushort)300, (ushort)300), (skills.GetValuePure(SkillIds.LanguageCommon), skills.GetMaxPure(SkillIds.LanguageCommon)));

        // The world-wide maximum (300) is left alone (Player.cpp:5484).
        player.Level = 60;
        skills.UpdateSkillsForLevel();
        Assert.Equal((ushort)300, skills.GetMaxPure(SkillIds.Swords));
        player.Level = 61;
        skills.UpdateSkillsForLevel();
        Assert.Equal((ushort)300, skills.GetMaxPure(SkillIds.Swords));   // 300 == config max: not re-derived
    }

    [Fact]
    public void UpdateSkillsForLevel_AlwaysMaxOption_SetsValueToMaxToo()
    {
        (Player player, PlayerSkills skills, _, _) = CreateSkills(level: 10, options: new SkillOptions { AlwaysMaxSkillForLevel = true });
        skills.Set(SkillIds.Swords, 12, 50);
        player.Level = 20;
        skills.UpdateSkillsForLevel();
        Assert.Equal(((ushort)100, (ushort)100), (skills.GetValuePure(SkillIds.Swords), skills.GetMaxPure(SkillIds.Swords)));
    }

    [Fact]
    public void UpdateSkillsToMax_GmCommand_SkipsProfessionsAndRiding()
    {
        (_, PlayerSkills skills, _, _) = CreateSkills(level: 10);
        skills.Set(SkillIds.Swords, 12, 50);
        skills.Set(SkillIds.Mining, 5, 75, 1);
        skills.Set(SkillIds.Cooking, 5, 75, 1);
        skills.Set(SkillIds.Riding, 75, 75, 1);
        skills.Set(SkillIds.PlateMail, 1, 1);
        skills.UpdateSkillsToMax();
        Assert.Equal((ushort)50, skills.GetValuePure(SkillIds.Swords));
        Assert.Equal((ushort)5, skills.GetValuePure(SkillIds.Mining));
        Assert.Equal((ushort)5, skills.GetValuePure(SkillIds.Cooking));
        Assert.Equal((ushort)75, skills.GetValuePure(SkillIds.Riding));
        Assert.Equal((ushort)1, skills.GetValuePure(SkillIds.PlateMail));
    }

    [Fact]
    public void StartingSpells_GrantTheirSkills_LevelMonoLanguage()
    {
        (_, PlayerSkills skills, FakeSkillSpellHost host, _) = CreateSkills(level: 1);
        host.Known.Add(SwordsSpell);
        host.Known.Add(CommonSpell);
        host.Known.Add(PlateSpell);
        foreach (uint spell in new[] { SwordsSpell, CommonSpell, PlateSpell })
        {
            skills.OnSpellLearned(spell);
        }

        Assert.Equal(((ushort)1, (ushort)5), (skills.GetValuePure(SkillIds.Swords), skills.GetMaxPure(SkillIds.Swords)));   // level range 1..5*level
        Assert.Equal(((ushort)300, (ushort)300), (skills.GetValuePure(SkillIds.LanguageCommon), skills.GetMaxPure(SkillIds.LanguageCommon)));
        Assert.Equal(((ushort)1, (ushort)1), (skills.GetValuePure(SkillIds.PlateMail), skills.GetMaxPure(SkillIds.PlateMail)));   // mono
    }

    [Fact]
    public void WeaponSkill_ALwaysMaxFlagOrOption_StartsAtTheMaximumForTheLevel()
    {
        (_, PlayerSkills skills, _, _) = CreateSkills(level: 10, options: new SkillOptions { AlwaysMaxSkillForLevel = true });
        skills.OnSpellLearned(SwordsSpell);
        Assert.Equal(((ushort)50, (ushort)50), (skills.GetValuePure(SkillIds.Swords), skills.GetMaxPure(SkillIds.Swords)));
    }

    [Fact]
    public void ProfessionSpell_SetsSkillAndStep_SkillTrainsItsSpellsByRequirement_FirstRankSpendsAFreePoint()
    {
        (_, PlayerSkills skills, FakeSkillSpellHost host, _) = CreateSkills();
        skills.InitPrimaryProfessions();
        Assert.Equal(2u, skills.FreePrimaryProfessionPoints);

        host.Known.Add(MiningApprentice);
        skills.OnSpellLearned(MiningApprentice);

        Assert.Equal(1u, skills.FreePrimaryProfessionPoints);
        Assert.Equal(((ushort)1, (ushort)75), (skills.GetValuePure(SkillIds.Mining), skills.GetMaxPure(SkillIds.Mining)));
        Assert.Equal(1, skills.GetStep(SkillIds.Mining));
        // Find Minerals (req 1) is taught on getting the skill; the req-100 spell waits.
        Assert.Contains(FindMinerals, host.Known);
        Assert.DoesNotContain(2581u, host.Known);
        Assert.Contains("remove 2581", host.Calls);

        // Journeyman: a higher step and maximum, the skill value is kept; not a first rank so no free point.
        host.Known.Add(MiningJourneyman);
        skills.OnSpellLearned(MiningJourneyman);
        Assert.Equal(1u, skills.FreePrimaryProfessionPoints);
        Assert.Equal(((ushort)1, (ushort)150), (skills.GetValuePure(SkillIds.Mining), skills.GetMaxPure(SkillIds.Mining)));
        Assert.Equal(2, skills.GetStep(SkillIds.Mining));

        // The skill rising to 100 teaches the req-100 spell.
        skills.Set(SkillIds.Mining, 100, 150, 2);
        Assert.Contains(2581u, host.Known);
    }

    [Fact]
    public void FreePrimaryProfessionPoints_AreNeverSpentBelowZero_AndNeverRestoredAboveTheMaximum()
    {
        (_, PlayerSkills skills, FakeSkillSpellHost host, _) = CreateSkills();
        skills.InitPrimaryProfessions();
        skills.FreePrimaryProfessionPoints = 0;                 // a GM learned more professions than allowed
        host.Known.Add(MiningApprentice);
        skills.OnSpellLearned(MiningApprentice);
        Assert.Equal(0u, skills.FreePrimaryProfessionPoints);

        skills.FreePrimaryProfessionPoints = 2;
        skills.OnSpellForgotten(MiningApprentice);              // 2 + 1 > max 2: not restored (Player.cpp:3870-3874)
        Assert.Equal(2u, skills.FreePrimaryProfessionPoints);
        skills.FreePrimaryProfessionPoints = 1;
        skills.OnSpellForgotten(MiningApprentice);
        Assert.Equal(2u, skills.FreePrimaryProfessionPoints);
    }

    [Fact]
    public void ForgettingTheFirstProfessionRank_RemovesTheSkillAndItsSpells_AHigherRankFallsBack()
    {
        (_, PlayerSkills skills, FakeSkillSpellHost host, _) = CreateSkills();
        host.Known.Add(MiningApprentice);
        skills.OnSpellLearned(MiningApprentice);
        host.Known.Add(MiningJourneyman);
        skills.OnSpellLearned(MiningJourneyman);
        skills.Set(SkillIds.Mining, 40, 150, 2);

        // Forget Journeyman: falls back to the Apprentice grant (value min(1, 40) = 1, max min(75, 150) = 75, step 1).
        host.Known.Remove(MiningJourneyman);
        skills.OnSpellForgotten(MiningJourneyman);
        Assert.Equal(((ushort)1, (ushort)75), (skills.GetValuePure(SkillIds.Mining), skills.GetMaxPure(SkillIds.Mining)));
        Assert.Equal(1, skills.GetStep(SkillIds.Mining));

        // Forget the first rank: the skill goes, and every spell the skill taught with it.
        host.Known.Remove(MiningApprentice);
        skills.OnSpellForgotten(MiningApprentice);
        Assert.False(skills.Has(SkillIds.Mining));
        Assert.DoesNotContain(FindMinerals, host.Known);
    }

    [Fact]
    public void ForgottenWeaponSkill_IsRememberedAndRestoredWhenRelearned()
    {
        (_, PlayerSkills skills, FakeSkillSpellHost host, _) = CreateSkills(level: 30);
        host.Known.Add(SwordsSpell);
        skills.OnSpellLearned(SwordsSpell);
        skills.Set(SkillIds.Swords, 73, 150);

        host.Known.Remove(SwordsSpell);
        skills.OnSpellForgotten(SwordsSpell);
        Assert.False(skills.Has(SkillIds.Swords));
        Assert.Equal((ushort)73, skills.ForgottenSkills[(ushort)SkillIds.Swords]);
        Assert.Equal(1, host.WeaponSkillRemovedCount);

        host.Known.Add(SwordsSpell);
        skills.OnSpellLearned(SwordsSpell);
        Assert.Equal((ushort)73, skills.GetValuePure(SkillIds.Swords));   // client patch 1.11.0 behaviour
        Assert.Equal((ushort)150, skills.GetMaxPure(SkillIds.Swords));
    }

    [Fact]
    public void ForgottenWeaponSkill_AboveTheLevelMaximum_IsNotRestored()
    {
        (Player player, PlayerSkills skills, FakeSkillSpellHost host, _) = CreateSkills(level: 30);
        host.Known.Add(SwordsSpell);
        skills.OnSpellLearned(SwordsSpell);
        skills.Set(SkillIds.Swords, 140, 150);
        host.Known.Remove(SwordsSpell);
        skills.OnSpellForgotten(SwordsSpell);

        player.Level = 10;
        host.Known.Add(SwordsSpell);
        skills.OnSpellLearned(SwordsSpell);
        Assert.Equal(((ushort)1, (ushort)50), (skills.GetValuePure(SkillIds.Swords), skills.GetMaxPure(SkillIds.Swords)));
    }

    [Fact]
    public void PoisonsRow_WithoutAMaxValue_GrantsAndRemovesTheSkill_ProfessionsAreKept()
    {
        // Poisons has no "learn on get" flag; the unrestricted row (max_value 0) is special-cased both ways (Player.cpp:5826-5832, 5869-5873).
        (_, PlayerSkills skills, FakeSkillSpellHost host, _) = CreateSkills(level: 5);
        host.Known.Add(PoisonsSpell);
        skills.OnSpellLearned(PoisonsSpell);
        Assert.Equal(((ushort)1, (ushort)25), (skills.GetValuePure(SkillIds.Poisons), skills.GetMaxPure(SkillIds.Poisons)));

        host.Known.Remove(PoisonsSpell);
        skills.OnSpellForgotten(PoisonsSpell);
        Assert.False(skills.Has(SkillIds.Poisons));

        // A profession (Cooking is "secondary") keeps its skill when a race/class-flagged spell of it goes (Player.cpp:5875-5879).
        skills.Set(SkillIds.Cooking, 40, 75, 1);
        host.Known.Add(CookingSpell);
        skills.OnSpellLearned(CookingSpell);
        host.Known.Remove(CookingSpell);
        skills.OnSpellForgotten(CookingSpell);
        Assert.True(skills.Has(SkillIds.Cooking));
    }

    [Fact]
    public void Load_AppliesFixedRanges_DropsBadRows_AndLearnsSkillSpellsAfterwards()
    {
        (Player player, PlayerSkills skills, FakeSkillSpellHost host, _) = CreateSkills(level: 10);
        skills.Load(
        [
            new CharacterSkillRow((ushort)SkillIds.LanguageCommon, 12, 99),     // language -> 300/300
            new CharacterSkillRow((ushort)SkillIds.PlateMail, 7, 9),            // mono -> 1/1
            new CharacterSkillRow((ushort)SkillIds.Swords, 20, 5),              // level -> max follows level
            new CharacterSkillRow((ushort)SkillIds.Mining, 60, 150),            // rank -> as stored
            new CharacterSkillRow(55555, 1, 1),                                 // unknown line
            new CharacterSkillRow((ushort)SkillIds.Axes, 0, 5),                 // value 0
            new CharacterSkillRow((ushort)SkillIds.Swords, 3, 3),               // duplicate
        ],
        [
            new ForgottenSkillRow((ushort)SkillIds.Axes, 33),
            new ForgottenSkillRow((ushort)SkillIds.Mining, 44),                 // not a weapon skill
            new ForgottenSkillRow(44444, 10),
        ]);

        Assert.Equal(((ushort)300, (ushort)300), (skills.GetValuePure(SkillIds.LanguageCommon), skills.GetMaxPure(SkillIds.LanguageCommon)));
        Assert.Equal(((ushort)1, (ushort)1), (skills.GetValuePure(SkillIds.PlateMail), skills.GetMaxPure(SkillIds.PlateMail)));
        Assert.Equal(((ushort)20, (ushort)50), (skills.GetValuePure(SkillIds.Swords), skills.GetMaxPure(SkillIds.Swords)));
        Assert.Equal(((ushort)60, (ushort)150), (skills.GetValuePure(SkillIds.Mining), skills.GetMaxPure(SkillIds.Mining)));
        Assert.False(skills.Has(55555));
        Assert.False(skills.Has(SkillIds.Axes));

        // Slots are contiguous from 0 in row order; the step is not stored at load (Player.cpp:20585-20587).
        Assert.Equal(SkillIds.LanguageCommon, player.GetUInt32(SlotIndex(0)));
        Assert.Equal(SkillIds.PlateMail, player.GetUInt32(SlotIndex(1)));
        Assert.Equal(SkillIds.Swords, player.GetUInt32(SlotIndex(2)));
        Assert.Equal(SkillIds.Mining, player.GetUInt32(SlotIndex(3)));
        Assert.Equal(0u, player.GetUInt32(SlotIndex(4)));

        // Skill-taught spells are learned after the whole load (value 60 >= 1, < 100).
        Assert.Contains(FindMinerals, host.Known);
        Assert.DoesNotContain(2581u, host.Known);

        Assert.Equal((ushort)33, skills.ForgottenSkills[(ushort)SkillIds.Axes]);
        Assert.Single(skills.ForgottenSkills);

        Assert.Throws<InvalidOperationException>(() => skills.Load([], []));
    }

    [Fact]
    public void Snapshot_ContainsLiveSkillsInSlotOrder_ThenIsCleanUntilSomethingChanges()
    {
        (_, PlayerSkills skills, _, _) = CreateSkills(level: 10);
        Assert.Null(skills.TakeSnapshotIfChanged());

        skills.Set(SkillIds.Swords, 5, 50);
        skills.Set(SkillIds.Mining, 1, 75, 1);
        Assert.True(skills.IsDirty);
        CharacterSkillSnapshot snapshot = skills.TakeSnapshotIfChanged()!;
        Assert.Equal(
        [
            new CharacterSkillRow((ushort)SkillIds.Swords, 5, 50),
            new CharacterSkillRow((ushort)SkillIds.Mining, 1, 75),
        ], snapshot.Skills);
        Assert.Empty(snapshot.Forgotten);
        Assert.False(skills.IsDirty);
        Assert.Null(skills.TakeSnapshotIfChanged());

        skills.Update(SkillIds.Swords, 1);
        Assert.True(skills.IsDirty);
        Assert.Equal((ushort)6, skills.TakeSnapshotIfChanged()!.Skills[0].Value);

        // A removed skill is gone from the next snapshot (and a never-saved one never appeared).
        skills.Set(SkillIds.Mining, 0, 0);
        skills.Set(SkillIds.Axes, 1, 5);
        skills.Set(SkillIds.Axes, 0, 0);
        CharacterSkillSnapshot after = skills.TakeSnapshotIfChanged()!;
        Assert.Equal([new CharacterSkillRow((ushort)SkillIds.Swords, 6, 50)], after.Skills);
    }

    [Fact]
    public void Snapshot_IncludesForgottenWeaponValuesAboveOne()
    {
        (_, PlayerSkills skills, FakeSkillSpellHost host, _) = CreateSkills(level: 30);
        host.Known.Add(SwordsSpell);
        skills.OnSpellLearned(SwordsSpell);
        skills.Set(SkillIds.Swords, 73, 150);
        host.Known.Remove(SwordsSpell);
        skills.OnSpellForgotten(SwordsSpell);

        CharacterSkillSnapshot snapshot = skills.TakeSnapshotIfChanged()!;
        Assert.Empty(snapshot.Skills);
        Assert.Equal([new ForgottenSkillRow((ushort)SkillIds.Swords, 73)], snapshot.Forgotten);
        Assert.Null(skills.TakeSnapshotIfChanged());
    }

    [Fact]
    public void Proficiency_MasksGrowOnlyWhenNewBitsArrive()
    {
        (_, PlayerSkills skills, _, _) = CreateSkills();
        Assert.Equal(0x80u, skills.AddProficiency(ItemClass.Weapon, 0x80));
        Assert.Null(skills.AddProficiency(ItemClass.Weapon, 0x80));
        Assert.Equal(0x81u, skills.AddProficiency(ItemClass.Weapon, 0x01));
        Assert.Equal(0x08u, skills.AddProficiency(ItemClass.Armor, 0x08));
        Assert.Null(skills.AddProficiency(ItemClass.Consumable, 0x08));
        Assert.Equal((0x81u, 0x08u), (skills.WeaponProficiency, skills.ArmorProficiency));
    }

    [Fact]
    public void SetProficiencyPacket_IsClassByteThenSubclassMask()
    {
        // SMSG_SET_PROFICIENCY 0x0127 (wow_messages smsg_set_proficiency.wowm): u8 class, u32 mask.
        Assert.Equal(0x0127, (int)WorldOpcode.SmsgSetProficiency);
        Assert.Equal([2, 0x81, 0x00, 0x00, 0x00], SkillPackets.SetProficiency(ItemClass.Weapon, 0x81));
        Assert.Equal([4, 0x08, 0x00, 0x00, 0x00], SkillPackets.SetProficiency(ItemClass.Armor, 0x08));
    }

    [Fact]
    public void ItemRequirements_AnswerFromTheRealSkillsAndSpellbook()
    {
        (Player player, PlayerSkills skills, FakeSkillSpellHost host, _) = CreateSkills(level: 10);
        var requirements = new PlayerItemRequirements(DefaultItemRequirements.Instance);
        PlayerInventory inventory = player.Inventory;

        Assert.Equal(0u, requirements.SkillValue(inventory, SkillIds.Mining));
        skills.Set(SkillIds.Mining, 50, 75, 1);
        skills.ModifyBonus(SkillIds.Mining, 5);
        skills.ModifyBonus(SkillIds.Mining, 2, permanent: true);
        Assert.Equal(57u, requirements.SkillValue(inventory, SkillIds.Mining));

        Assert.False(requirements.HasSpell(inventory, 1234));
        host.Known.Add(1234);
        Assert.True(requirements.HasSpell(inventory, 1234));

        Assert.False(requirements.CanDualWield(inventory));
        skills.CanDualWield = true;
        Assert.True(requirements.CanDualWield(inventory));

        Assert.Equal(DefaultItemRequirements.Instance.HonorRank(inventory), requirements.HonorRank(inventory));
        Assert.Equal(DefaultItemRequirements.Instance.ReputationRank(inventory, 72), requirements.ReputationRank(inventory, 72));
    }

    [Fact]
    public void ItemRequirements_APlayerWithoutSkillsKnowsNothing()
    {
        Player player = TestWorld.CreatePlayer(2, 0, 0, new FakeSession());
        var requirements = new PlayerItemRequirements(DefaultItemRequirements.Instance);
        Assert.Equal(0u, requirements.SkillValue(player.Inventory, SkillIds.Swords));
        Assert.False(requirements.HasSpell(player.Inventory, 1));
        Assert.False(requirements.CanDualWield(player.Inventory));
    }

    [Fact]
    public void AttachSkills_IsOnce()
    {
        (Player player, PlayerSkills skills, _, _) = CreateSkills();
        Assert.Same(skills, player.Skills);
        Assert.Throws<InvalidOperationException>(() => player.AttachSkills(skills));
    }
}

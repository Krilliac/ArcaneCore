using Xunit;
using ArcaneCore.Game.Conditions;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Npc;
using static ArcaneCore.Game.Tests.Conditions.ConditionTestSupport;

namespace ArcaneCore.Game.Tests.Conditions;

/// <summary>
/// ConditionEntry::Meets / Evaluate truth tables with the cmangos numbering
/// (D:\refs\mangos-classic\src\game\Globals\Conditions.cpp:109-514, Conditions.h:30-88).
/// </summary>
public sealed class ConditionEvaluatorTests
{
    [Fact]
    public void TypeIds_AreTheCmangosNumbering_NotVmangos()
    {
        // Conditions.h:30-82. vmangos reuses 11, 13, 27, 28, 31, 35 and 40 for other meanings; a re-map would
        // silently turn gender rows into something else.
        Assert.Equal(
            new[] { -3, -2, -1, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 17, 18, 19, 22, 23, 26, 28, 29, 30, 31, 33, 35, 36, 37, 38, 39, 40, 42, 43 },
            new[]
            {
                ConditionType.Not, ConditionType.Or, ConditionType.And, ConditionType.None, ConditionType.Aura, ConditionType.Item,
                ConditionType.ItemEquipped, ConditionType.AreaId, ConditionType.ReputationRankMin, ConditionType.Team, ConditionType.Skill,
                ConditionType.QuestRewarded, ConditionType.QuestTaken, ConditionType.AdCommissionAura, ConditionType.PvpRank,
                ConditionType.ActiveGameEvent, ConditionType.AreaFlag, ConditionType.RaceClass, ConditionType.Level, ConditionType.Spell,
                ConditionType.InstanceScript, ConditionType.QuestAvailable, ConditionType.QuestNone, ConditionType.ItemWithBank,
                ConditionType.ActiveHoliday, ConditionType.LearnableAbility, ConditionType.SkillBelow, ConditionType.ReputationRankMax,
                ConditionType.CompletedEncounter, ConditionType.LastWaypoint, ConditionType.Gender, ConditionType.DeadOrAway,
                ConditionType.CreatureInRange, ConditionType.PvpScript, ConditionType.SpawnCount, ConditionType.WorldScript,
                ConditionType.WorldState, ConditionType.IsInCombat,
            }.Select(t => (int)t));
        Assert.Equal(1, (int)ConditionFlags.ReverseResult);
        Assert.Equal(2, (int)ConditionFlags.SwapTargets);
    }

    [Fact]
    public void GenderRows_UseType35_AndAreaFlagRows_UseType13()
    {
        ConditionEvaluator gender = Evaluator(null, Row(1, ConditionType.Gender, 1));
        Assert.True(gender.IsSatisfied(1, CreatePlayer(gender: Gender.Female), null));
        Assert.False(gender.IsSatisfied(1, CreatePlayer(gender: Gender.Male), null));

        // The same numeric type 35 must not be read as vmangos MAP_EVENT_DATA, nor 13 as CANT_PATH_TO_VICTIM.
        var ctx = new ConditionContext { ZoneAndArea = (_, _, _, _) => (12, 24), AreaFlags = a => a == 24 ? 0x8u : null };
        ConditionEvaluator areaFlag = Evaluator(ctx, Row(2, ConditionType.AreaFlag, 0x8));
        Assert.True(areaFlag.IsSatisfied(2, CreatePlayer(), null));
    }

    [Fact]
    public void NoneIsAlwaysMet_AndMissingConditionIsNeverMet()
    {
        ConditionEvaluator e = Evaluator(null, Row(1, ConditionType.None));
        Assert.True(e.IsSatisfied(1, CreatePlayer(), null));
        Assert.False(e.IsSatisfied(2, CreatePlayer(), null));
        Assert.False(e.IsSatisfied(0, CreatePlayer(), null));
    }

    [Fact]
    public void InstanceScriptCondition_DelegatesValue1AndFailsClosedWithoutAnInstance()
    {
        // mangos-classic Conditions.cpp:285-292 passes value1 to
        // InstanceData::CheckConditionCriteriaMeet; Dire Maul uses 0..6 for its tribute tier.
        var context = new ConditionContext { InstanceScript = (_, id) => id == 4 };
        ConditionEvaluator evaluator = Evaluator(context,
            Row(41, ConditionType.InstanceScript, 4), Row(42, ConditionType.InstanceScript, 5));
        Player player = CreatePlayer();
        Assert.True(evaluator.IsSatisfied(41, player, null));
        Assert.False(evaluator.IsSatisfied(42, player, null));

        ConditionEvaluator missing = Evaluator(new ConditionContext { InstanceScript = (_, _) => null },
            Row(41, ConditionType.InstanceScript, 4));
        Assert.False(missing.IsSatisfied(41, player, null));
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, true, true, true)]
    public void AndOrNot_TruthTable(bool a, bool b, bool expectedOr, bool expectedAnd)
    {
        ConditionRecord ca = Row(1, ConditionType.None, flags: a ? ConditionFlags.None : ConditionFlags.ReverseResult);
        ConditionRecord cb = Row(2, ConditionType.None, flags: b ? ConditionFlags.None : ConditionFlags.ReverseResult);
        ConditionEvaluator e = Evaluator(null, ca, cb,
            Row(3, ConditionType.Or, 1, 2), Row(4, ConditionType.And, 1, 2), Row(5, ConditionType.Not, 1));
        Player p = CreatePlayer();
        Assert.Equal(expectedOr, e.IsSatisfied(3, p, null));
        Assert.Equal(expectedAnd, e.IsSatisfied(4, p, null));
        Assert.Equal(!a, e.IsSatisfied(5, p, null));
    }

    [Fact]
    public void AndOr_ThirdAndFourthIdsAreHonoured()
    {
        // Conditions.cpp:157-176: value3/value4 are optional extra operands.
        ConditionEvaluator e = Evaluator(null,
            Row(1, ConditionType.None),
            Row(2, ConditionType.None, flags: ConditionFlags.ReverseResult),
            Row(3, ConditionType.And, 1, 1, 2),
            Row(4, ConditionType.And, 1, 1, 1, 1),
            Row(5, ConditionType.Or, 2, 2, 2, 1),
            Row(6, ConditionType.Or, 2, 2, 2, 2));
        Player p = CreatePlayer();
        Assert.False(e.IsSatisfied(3, p, null));
        Assert.True(e.IsSatisfied(4, p, null));
        Assert.True(e.IsSatisfied(5, p, null));
        Assert.False(e.IsSatisfied(6, p, null));
    }

    [Fact]
    public void ReverseFlag_NegatesTheResult()
    {
        ConditionEvaluator e = Evaluator(null,
            Row(1, ConditionType.Level, 5, 1),
            Row(2, ConditionType.Level, 5, 1, flags: ConditionFlags.ReverseResult));
        Player low = CreatePlayer(level: 4);
        Player high = CreatePlayer(level: 5);
        Assert.False(e.IsSatisfied(1, low, null));
        Assert.True(e.IsSatisfied(2, low, null));
        Assert.True(e.IsSatisfied(1, high, null));
        Assert.False(e.IsSatisfied(2, high, null));
    }

    [Fact]
    public void SwapTargets_ExchangesWhoIsTheTarget()
    {
        // Conditions.cpp:111-113 swaps source and target first. A player-only type then has a creature
        // target, fails the parameter check and is false even with the reverse flag (Conditions.cpp:115-122
        // returns before the reverse at 126).
        ConditionEvaluator e = Evaluator(null,
            Row(1, ConditionType.RaceClass, 1, 0, flags: ConditionFlags.SwapTargets),
            Row(2, ConditionType.RaceClass, 1, 0, flags: ConditionFlags.SwapTargets | ConditionFlags.ReverseResult));
        Player human = CreatePlayer();
        NpcInfo npc = CreateNpc();
        Assert.False(e.IsSatisfied(1, human, npc));
        Assert.False(e.IsSatisfied(2, human, npc));

        // Without a source the swap leaves the player as the source and nobody as the target.
        Assert.False(e.IsSatisfied(1, human, null));
    }

    [Fact]
    public void AreaId_SearchesTheSource_ThenTheTarget_AndSwapChangesWhichOne()
    {
        // Conditions.cpp:194-199: searcher = source ? source : target; (zone == v || area == v) == (v2 == 0).
        var ctx = new ConditionContext { ZoneAndArea = (_, x, _, _) => x > 100 ? (40u, 99u) : (12u, 24u) };
        ConditionEvaluator e = Evaluator(ctx,
            Row(1, ConditionType.AreaId, 99, 0),
            Row(2, ConditionType.AreaId, 99, 0, flags: ConditionFlags.SwapTargets),
            Row(3, ConditionType.AreaId, 40, 1),
            Row(4, ConditionType.AreaId, 12, 0));
        Player player = CreatePlayer(x: 0);
        NpcInfo farNpc = CreateNpc(x: 200);
        Assert.True(e.IsSatisfied(1, player, farNpc));   // the NPC (source) is in area 99
        Assert.False(e.IsSatisfied(1, player, null));    // no source: the player's area 24
        Assert.False(e.IsSatisfied(2, player, farNpc));  // swapped: the source is now the player
        Assert.False(e.IsSatisfied(3, player, farNpc));  // "not in zone 40" but the NPC is in zone 40
        Assert.True(e.IsSatisfied(3, player, null));
        Assert.True(e.IsSatisfied(4, player, null));     // zone match
    }

    [Fact]
    public void AreaFlag_RequiresAndForbidsBits()
    {
        // Conditions.cpp:249-257.
        var ctx = new ConditionContext { ZoneAndArea = (_, _, _, _) => (1, 5), AreaFlags = a => a == 5 ? 0x4u | 0x1000u : null };
        ConditionEvaluator e = Evaluator(ctx,
            Row(1, ConditionType.AreaFlag, 0x4, 0),
            Row(2, ConditionType.AreaFlag, 0x2, 0),
            Row(3, ConditionType.AreaFlag, 0, 0x1000),
            Row(4, ConditionType.AreaFlag, 0, 0x2),
            Row(5, ConditionType.AreaFlag, 0x4, 0x2));
        Player p = CreatePlayer();
        Assert.True(e.IsSatisfied(1, p, null));
        Assert.False(e.IsSatisfied(2, p, null));
        Assert.False(e.IsSatisfied(3, p, null));
        Assert.True(e.IsSatisfied(4, p, null));
        Assert.True(e.IsSatisfied(5, p, null));

        // An area missing from the area table is false (no AreaTableEntry).
        var unknown = new ConditionContext { ZoneAndArea = (_, _, _, _) => (0, 0), AreaFlags = _ => null };
        Assert.False(Evaluator(unknown, Row(1, ConditionType.AreaFlag, 0x4, 0)).IsSatisfied(1, p, null));
    }

    [Fact]
    public void RaceClass_MasksAreTheOneBasedBit()
    {
        // Conditions.cpp:259-263; getRaceMask() = 1 << (race - 1), Unit.h:1316-1318.
        ConditionEvaluator e = Evaluator(null,
            Row(1, ConditionType.RaceClass, 1 << 0, 0),                       // human
            Row(2, ConditionType.RaceClass, 0, 1 << 7),                       // mage
            Row(3, ConditionType.RaceClass, 1 << 0, 1 << 0),                  // human warrior
            Row(4, ConditionType.RaceClass, (1 << 1) | (1 << 7), 0),          // orc or troll
            Row(5, ConditionType.RaceClass, 0, (1 << 2) | (1 << 10)));        // hunter or druid
        Player humanWarrior = CreatePlayer();
        Player humanMage = CreatePlayer(cls: Class.Mage);
        Player trollDruid = CreatePlayer(race: Race.Troll, cls: Class.Druid);
        Assert.True(e.IsSatisfied(1, humanWarrior, null));
        Assert.False(e.IsSatisfied(2, humanWarrior, null));
        Assert.True(e.IsSatisfied(2, humanMage, null));
        Assert.True(e.IsSatisfied(3, humanWarrior, null));
        Assert.False(e.IsSatisfied(3, humanMage, null));
        Assert.False(e.IsSatisfied(4, humanWarrior, null));
        Assert.True(e.IsSatisfied(4, trollDruid, null));
        Assert.True(e.IsSatisfied(5, trollDruid, null));
        Assert.False(e.IsSatisfied(5, humanMage, null));
    }

    [Theory]
    [InlineData(0u, 10, 10, true)]
    [InlineData(0u, 10, 11, false)]
    [InlineData(1u, 10, 10, true)]
    [InlineData(1u, 10, 9, false)]
    [InlineData(1u, 10, 60, true)]
    [InlineData(2u, 10, 10, true)]
    [InlineData(2u, 10, 11, false)]
    [InlineData(2u, 10, 1, true)]
    public void Level_Modes(uint mode, uint level, int playerLevel, bool expected)
    {
        ConditionEvaluator e = Evaluator(null, Row(1, ConditionType.Level, level, mode));
        Assert.Equal(expected, e.IsSatisfied(1, CreatePlayer(level: (byte)playerLevel), null));
    }

    [Fact]
    public void QuestConditions_FollowTheQuestLog()
    {
        var quests = new FakeQuests();
        quests.State[10] = (Incomplete: true, CompleteNotRewarded: false, Rewarded: false);
        quests.State[11] = (false, true, false);
        quests.State[12] = (false, false, true);
        quests.Takeable.Add(13);
        var ctx = new ConditionContext { Quests = () => quests };
        ConditionEvaluator e = Evaluator(ctx,
            Row(1, ConditionType.QuestTaken, 10, 0), Row(2, ConditionType.QuestTaken, 10, 1), Row(3, ConditionType.QuestTaken, 10, 2),
            Row(4, ConditionType.QuestTaken, 11, 0), Row(5, ConditionType.QuestTaken, 11, 1), Row(6, ConditionType.QuestTaken, 11, 2),
            Row(7, ConditionType.QuestRewarded, 12), Row(8, ConditionType.QuestRewarded, 10),
            Row(9, ConditionType.QuestNone, 12), Row(10, ConditionType.QuestNone, 10), Row(11, ConditionType.QuestNone, 99),
            Row(12, ConditionType.QuestAvailable, 13), Row(13, ConditionType.QuestAvailable, 10));
        Player p = CreatePlayer();
        bool[] expected = [true, true, false, true, false, true, true, false, false, false, true, true, false];
        for (uint id = 1; id <= 13; id++)
        {
            Assert.True(expected[id - 1] == e.IsSatisfied(id, p, null), $"condition {id}");
        }
    }

    [Fact]
    public void Item_ExcludesTheBank_ItemWithBank_IncludesIt()
    {
        // Conditions.cpp:186-189 and 314-317: HasItemCount(item, count, inBankAlso).
        var ctx = new ConditionContext { ItemCount = (_, item, bank) => item == 7 ? (bank ? 3u : 1u) : 0 };
        ConditionEvaluator e = Evaluator(ctx,
            Row(1, ConditionType.Item, 7, 1), Row(2, ConditionType.Item, 7, 2),
            Row(3, ConditionType.ItemWithBank, 7, 2), Row(4, ConditionType.ItemWithBank, 7, 4),
            Row(5, ConditionType.Item, 8, 1));
        Player p = CreatePlayer();
        Assert.True(e.IsSatisfied(1, p, null));
        Assert.False(e.IsSatisfied(2, p, null));
        Assert.True(e.IsSatisfied(3, p, null));
        Assert.False(e.IsSatisfied(4, p, null));
        Assert.False(e.IsSatisfied(5, p, null));
    }

    [Fact]
    public void ItemEquipped_ChecksTheWornSlotsOnly()
    {
        (Player player, _) = ItemTestData.CreatePlayer();
        Item shirt = ItemTestData.Give(player.Inventory, ItemTestData.RecruitsShirt);
        ItemTestData.Give(player.Inventory, ItemTestData.RecruitsPants);
        player.Inventory.AutoEquipItem(shirt.BagSlot, shirt.Slot);
        Assert.Contains(player.Inventory.Equipped, e => e.Item.Entry == ItemTestData.RecruitsShirt);
        ConditionEvaluator e = Evaluator(null,
            Row(1, ConditionType.ItemEquipped, ItemTestData.RecruitsShirt),
            Row(2, ConditionType.ItemEquipped, ItemTestData.RecruitsPants));
        Assert.True(e.IsSatisfied(1, player, null));
        Assert.False(e.IsSatisfied(2, player, null));
    }

    [Fact]
    public void Team_Gender_Skill_Spell_Aura_Reputation()
    {
        var ctx = new ConditionContext
        {
            SkillValueBase = (_, skill) => skill == 164 ? 150u : 0,
            HasSpell = (_, spell) => spell == 100,
            HasAura = (_, spell, effect) => spell == 25 && effect == 1,
            FactionExists = f => f != 999,
            ReputationRank = (_, faction) => faction == 72 ? (byte)5 : (byte)3,
        };
        ConditionEvaluator e = Evaluator(ctx,
            Row(1, ConditionType.Team, 469), Row(2, ConditionType.Team, 67),
            Row(3, ConditionType.Skill, 164, 150), Row(4, ConditionType.Skill, 164, 151), Row(5, ConditionType.Skill, 165, 1),
            Row(6, ConditionType.SkillBelow, 164, 151), Row(7, ConditionType.SkillBelow, 164, 150),
            Row(8, ConditionType.SkillBelow, 165, 1), Row(9, ConditionType.SkillBelow, 164, 1),
            Row(10, ConditionType.Spell, 100, 0), Row(11, ConditionType.Spell, 100, 1), Row(12, ConditionType.Spell, 101, 1),
            Row(13, ConditionType.Aura, 25, 1), Row(14, ConditionType.Aura, 25, 0),
            Row(15, ConditionType.ReputationRankMin, 72, 5), Row(16, ConditionType.ReputationRankMin, 72, 6),
            Row(17, ConditionType.ReputationRankMax, 72, 5), Row(18, ConditionType.ReputationRankMax, 72, 4),
            Row(19, ConditionType.ReputationRankMin, 999, 0), Row(20, ConditionType.ReputationRankMax, 999, 7));
        Player alliance = CreatePlayer();
        Player horde = CreatePlayer(race: Race.Orc);
        Assert.True(e.IsSatisfied(1, alliance, null));
        Assert.False(e.IsSatisfied(2, alliance, null));
        Assert.True(e.IsSatisfied(2, horde, null));
        bool[] expected =
        [
            true, false, true, false, false, true, false, true, false, true, false, true, true, false, true, false, true, false, false, false,
        ];
        for (uint id = 1; id <= 20; id++)
        {
            if (id <= 2)
            {
                continue;
            }

            Assert.True(expected[id - 1] == e.IsSatisfied(id, alliance, null), $"condition {id}");
        }
    }

    [Fact]
    public void GameEvents_AndHolidays_FollowTheProvider()
    {
        var ctx = new ConditionContext { IsGameEventActive = id => id == 7, IsHolidayActive = id => id == 141 };
        ConditionEvaluator e = Evaluator(ctx,
            Row(1, ConditionType.ActiveGameEvent, 7), Row(2, ConditionType.ActiveGameEvent, 8),
            Row(3, ConditionType.ActiveHoliday, 141), Row(4, ConditionType.ActiveHoliday, 142));
        Player p = CreatePlayer();
        Assert.True(e.IsSatisfied(1, p, null));
        Assert.False(e.IsSatisfied(2, p, null));
        Assert.True(e.IsSatisfied(3, p, null));
        Assert.False(e.IsSatisfied(4, p, null));
    }

    [Fact]
    public void PvpRank_AndAdCommission_UseTheirProviders()
    {
        var ctx = new ConditionContext { HonorRank = _ => 5, HasAdCommissionAura = _ => true };
        ConditionEvaluator e = Evaluator(ctx,
            Row(1, ConditionType.PvpRank, 3, 7), Row(2, ConditionType.PvpRank, 6, 7), Row(3, ConditionType.PvpRank, 1, 4),
            Row(4, ConditionType.AdCommissionAura));
        Player p = CreatePlayer();
        Assert.True(e.IsSatisfied(1, p, null));
        Assert.False(e.IsSatisfied(2, p, null));
        Assert.False(e.IsSatisfied(3, p, null));
        Assert.True(e.IsSatisfied(4, p, null));
    }

    [Fact]
    public void UnsupportedType_HidesTheOption_EvenReversed_AndIsCounted()
    {
        // WORLD_SCRIPT (40) cannot be decided from an NPC interaction: it must never read as satisfied.
        ConditionEvaluator e = Evaluator(null,
            Row(1, ConditionType.WorldScript),
            Row(2, ConditionType.WorldScript, flags: ConditionFlags.ReverseResult),
            Row(3, ConditionType.Not, 1),
            Row(4, ConditionType.Or, 1, 1),
            Row(5, ConditionType.None),
            Row(6, ConditionType.Or, 1, 5));
        Player p = CreatePlayer();
        Assert.False(e.IsSatisfied(1, p, null));
        Assert.False(e.IsSatisfied(2, p, null));
        Assert.False(e.IsSatisfied(3, p, null));
        Assert.False(e.IsSatisfied(4, p, null));
        Assert.True(e.IsSatisfied(6, p, null));   // a definite true in an OR still decides it
        Assert.True(e.Unavailable.TryGetValue((int)ConditionType.WorldScript, out long count));
        Assert.True(count >= 4);

        ConditionSummary summary = e.Summarize();
        Assert.Equal(6, summary.Total);
        Assert.Equal(1, summary.Evaluable);                                  // only NONE; OR(1,5) and the rest involve type 40
        Assert.Equal(2, summary.UnavailableByType[(int)ConditionType.WorldScript]);
    }

    [Fact]
    public void MissingCollaborator_FailsClosed_NotOpen()
    {
        // No HasSpell provider: Spell(has not) must not read as "true" and NOT(Spell) must not either.
        ConditionEvaluator e = Evaluator(null,
            Row(1, ConditionType.Spell, 100, 1),
            Row(2, ConditionType.Spell, 100, 0),
            Row(3, ConditionType.Not, 2),
            Row(4, ConditionType.Spell, 100, 0, flags: ConditionFlags.ReverseResult));
        Player p = CreatePlayer();
        foreach (uint id in new uint[] { 1, 2, 3, 4 })
        {
            Assert.False(e.IsSatisfied(id, p, null), $"condition {id}");
        }

        ConditionSummary summary = e.Summarize();
        Assert.Equal(0, summary.Evaluable);
        Assert.Equal(3, summary.UnavailableByType[(int)ConditionType.Spell]);
    }

    [Fact]
    public void QuestConditions_WithoutALoadedQuestLog_FailClosed()
    {
        ConditionEvaluator e = Evaluator(new ConditionContext { Quests = () => null }, Row(1, ConditionType.QuestNone, 5));
        Assert.False(e.IsSatisfied(1, CreatePlayer(), null));
    }

    [Fact]
    public void Table_RejectsWhatCmangosIsValidRefuses()
    {
        ConditionRecord[] rows =
        [
            Row(1, ConditionType.None),
            Row(2, ConditionType.And, 1, 3),                     // forward reference
            Row(3, ConditionType.Not, 99),                       // missing reference
            Row(4, ConditionType.Level, 0, 0),                   // level 0
            Row(5, ConditionType.Level, 61, 1),                  // above max level
            Row(6, ConditionType.Level, 10, 3),                  // bad mode
            Row(7, ConditionType.Team, 5),                       // unknown team
            Row(8, ConditionType.RaceClass, 0, 0),               // both masks zero
            Row(9, ConditionType.Item, 25, 0),                   // count 0
            new ConditionRecord(10, 41, 0, 0, 0, 0, 0),          // CONDITION_UNUSED_7: bad type
            new ConditionRecord(11, 0, 0, 0, 0, 0, 4),           // unknown flag bit
            Row(12, ConditionType.Gender, 3),                    // gender out of range
            Row(13, ConditionType.Skill, 164, 0),                // skill value 0
            Row(14, ConditionType.ReputationRankMin, 72, 8),     // rank out of range
            Row(15, ConditionType.Spell, 1, 2),                  // bad mode
            Row(16, ConditionType.AreaId, 12, 2),                // bad mode
            Row(17, ConditionType.Or, 1, 4),                     // references the rejected 4
            Row(18, ConditionType.Level, 60, 2),                 // ok
            Row(18, ConditionType.None),                         // duplicate entry id
        ];
        ConditionTable table = ConditionTable.Build(rows);
        Assert.Equal(new uint[] { 1, 18 }, table.Entries.Select(e => e.Entry).OrderBy(x => x).ToArray());
        Assert.Equal(
            new uint[] { 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18 },
            table.Rejected.Select(r => r.Entry).OrderBy(x => x).ToArray());
        Assert.Contains(table.Rejected, r => r.Entry == 18 && r.Reason.Contains("duplicate"));
        Assert.Contains(table.Rejected, r => r.Entry == 17 && r.Reason.Contains("does not exist or is invalid"));
    }

    [Fact]
    public void Table_ValidatesInEntryOrder_WhateverTheInputOrder()
    {
        ConditionTable table = ConditionTable.Build([Row(5, ConditionType.Not, 2), Row(2, ConditionType.None)]);
        Assert.Empty(table.Rejected);
        Assert.Equal(2, table.Count);
        Assert.Same(ConditionTable.Empty, ConditionTable.Empty);
        Assert.Equal(0, ConditionTable.Empty.Count);
    }
}

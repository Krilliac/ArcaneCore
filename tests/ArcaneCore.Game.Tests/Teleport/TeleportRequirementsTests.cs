using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.WorldData;
using Xunit;

namespace ArcaneCore.Game.Tests.Teleport;

/// <summary>
/// Entry requirements of an <c>areatrigger_teleport</c> row (docs/areas/area-triggers.md): level, items, the quest and further gates, the
/// conditions-table reference, the row's own message and the game master bypass. The items are the real inventory over the test item
/// templates; the quest, condition and gate collaborators are recording fakes.
/// </summary>
public sealed class TeleportRequirementsTests
{
    private const uint Key = ItemTestData.UniqueKey;      // "Test Key"
    private const uint Second = ItemTestData.QuestNote;   // "Test Note"
    private const uint Unlisted = 99999;                  // no template

    private static AreaTriggerTeleport Row(byte level = 0, uint item = 0, uint item2 = 0, uint quest = 0, uint condition = 0, string message = "")
        => new(78, "Entrance", message, level, 36, -16.4f, -383.07f, 61.78f, 1.86f, item, item2, quest, condition);

    private static Player NewPlayer(byte level = 60)
        => ItemTestData.CreatePlayer(level: level).Player;

    [Fact]
    public void NoRequirements_AllowsAnyPlayer()
    {
        Assert.Equal(AreaTriggerVerdict.Allow, AreaTriggerRequirements.Evaluate(NewPlayer(1), Row(), null, []));
    }

    [Fact]
    public void LevelBelowTheRequirement_IsRefusedWithTheStockText_AtTheLevelItPasses()
    {
        AreaTriggerVerdict refused = AreaTriggerRequirements.Evaluate(NewPlayer(9), Row(level: 10), null, []);

        Assert.False(refused.Allowed);
        Assert.Equal("You must be at least level 10 to enter.", refused.Message);
        Assert.True(AreaTriggerRequirements.Evaluate(NewPlayer(10), Row(level: 10), null, []).Allowed);
    }

    [Fact]
    public void MissingItem_IsRefusedNamingTheItem_AndCarryingItPasses()
    {
        Player player = NewPlayer();

        AreaTriggerVerdict refused = AreaTriggerRequirements.Evaluate(player, Row(item: Key), null, []);
        ItemTestData.Give(player.Inventory, Key);
        AreaTriggerVerdict allowed = AreaTriggerRequirements.Evaluate(player, Row(item: Key), null, []);

        Assert.False(refused.Allowed);
        Assert.Equal("You must have item Test Key to enter.", refused.Message);
        Assert.True(allowed.Allowed);
    }

    [Fact]
    public void EitherAlternativeItemAllowsEntry_AndMissingBothNamesTheFirst()
    {
        Player player = NewPlayer();
        AreaTriggerTeleport row = Row(item: Key, item2: Second);

        Assert.Equal("You must have item Test Key to enter.", AreaTriggerRequirements.Evaluate(player, row, null, []).Message);
        ItemTestData.Give(player.Inventory, Second);
        Assert.True(AreaTriggerRequirements.Evaluate(player, row, null, []).Allowed);

        Player other = NewPlayer();
        ItemTestData.Give(other.Inventory, Key);
        Assert.True(AreaTriggerRequirements.Evaluate(other, row, null, []).Allowed);

        Player onlySecondColumn = NewPlayer();
        Assert.Equal("You must have item Test Note to enter.", AreaTriggerRequirements.Evaluate(onlySecondColumn, Row(item2: Second), null, []).Message);
        ItemTestData.Give(onlySecondColumn.Inventory, Second);
        Assert.True(AreaTriggerRequirements.Evaluate(onlySecondColumn, Row(item2: Second), null, []).Allowed);
    }

    [Fact]
    public void AnItemWithoutATemplate_IsNamedByItsEntry_InsteadOfFailing()
    {
        AreaTriggerVerdict refused = AreaTriggerRequirements.Evaluate(NewPlayer(), Row(item: Unlisted), null, []);

        Assert.False(refused.Allowed);
        Assert.Equal("You must have item 99999 to enter.", refused.Message);
    }

    [Fact]
    public void TheRowsOwnMessage_ReplacesEveryGeneratedRefusalText()
    {
        const string text = "You must have the Drakefire Amulet.";
        var gate = new FakeGate(AreaTriggerVerdict.Refuse("gate text"));

        Assert.Equal(text, AreaTriggerRequirements.Evaluate(NewPlayer(1), Row(level: 10, message: text), null, []).Message);
        Assert.Equal(text, AreaTriggerRequirements.Evaluate(NewPlayer(), Row(item: Key, message: text), null, []).Message);
        Assert.Equal(text, AreaTriggerRequirements.Evaluate(NewPlayer(), Row(message: text), null, [gate]).Message);
        Assert.Equal(text, AreaTriggerRequirements.Evaluate(NewPlayer(), Row(condition: 5, message: text), new FakeConditions(false), []).Message);
    }

    [Fact]
    public void GameMasters_PassEveryRequirement_WithoutConsultingTheCollaborators()
    {
        Player gm = NewPlayer(1);
        gm.Flags |= PlayerFlags.Gm;
        var gate = new FakeGate(AreaTriggerVerdict.Refuse("no"));
        var conditions = new FakeConditions(false);

        AreaTriggerVerdict verdict = AreaTriggerRequirements.Evaluate(gm, Row(level: 60, item: Key, item2: Second, quest: 9, condition: 3), conditions, [gate]);

        Assert.True(verdict.Allowed);
        Assert.Equal(0, gate.Calls);
        Assert.Equal(0, conditions.Calls);
    }

    [Fact]
    public void AGateRefusal_IsSilentUnlessItSuppliesAText_AndTheFirstRefusalWins()
    {
        Player player = NewPlayer();
        var silent = new FakeGate(AreaTriggerVerdict.Refuse(null));
        var loud = new FakeGate(AreaTriggerVerdict.Refuse("Loud"));

        AreaTriggerVerdict first = AreaTriggerRequirements.Evaluate(player, Row(quest: 9), null, [silent, loud]);
        AreaTriggerVerdict second = AreaTriggerRequirements.Evaluate(player, Row(quest: 9), null, [loud, silent]);

        Assert.False(first.Allowed);
        Assert.Null(first.Message);
        Assert.Equal("Loud", second.Message);
        Assert.Equal(1, silent.Calls);
        Assert.Equal(1, loud.Calls);
    }

    [Fact]
    public void AllowingGates_LetThePlayerThrough_AndSeeTheRowAndPlayer()
    {
        Player player = NewPlayer();
        var gate = new FakeGate(AreaTriggerVerdict.Allow);
        AreaTriggerTeleport row = Row(quest: 9);

        Assert.True(AreaTriggerRequirements.Evaluate(player, row, null, [gate]).Allowed);

        Assert.Same(row, gate.LastTeleport);
        Assert.Same(player, gate.LastPlayer);
    }

    [Fact]
    public void ChecksRunInOrder_LevelThenItemsThenGatesThenCondition()
    {
        var gate = new FakeGate(AreaTriggerVerdict.Refuse("gate"));
        var conditions = new FakeConditions(false);
        AreaTriggerTeleport row = Row(level: 10, item: Key, quest: 9, condition: 4);

        Assert.Equal("You must be at least level 10 to enter.", AreaTriggerRequirements.Evaluate(NewPlayer(5), row, conditions, [gate]).Message);
        Assert.Equal("You must have item Test Key to enter.", AreaTriggerRequirements.Evaluate(NewPlayer(), row, conditions, [gate]).Message);
        Assert.Equal((0, 0), (gate.Calls, conditions.Calls));

        Player keyed = NewPlayer();
        ItemTestData.Give(keyed.Inventory, Key);
        Assert.Equal("gate", AreaTriggerRequirements.Evaluate(keyed, row, conditions, [gate]).Message);
        Assert.Equal((1, 0), (gate.Calls, conditions.Calls));
    }

    [Fact]
    public void ARequiredCondition_NeedsAnEvaluatorThatSaysYes_AndNoEvaluatorFailsClosed()
    {
        Player player = NewPlayer();
        var no = new FakeConditions(false);
        var yes = new FakeConditions(true);

        AreaTriggerVerdict withoutEvaluator = AreaTriggerRequirements.Evaluate(player, Row(condition: 7), null, []);
        AreaTriggerVerdict refused = AreaTriggerRequirements.Evaluate(player, Row(condition: 7), no, []);
        AreaTriggerVerdict allowed = AreaTriggerRequirements.Evaluate(player, Row(condition: 7), yes, []);

        Assert.False(withoutEvaluator.Allowed);
        Assert.Null(withoutEvaluator.Message);
        Assert.False(refused.Allowed);
        Assert.Null(refused.Message);
        Assert.True(allowed.Allowed);
        Assert.Equal(7u, yes.LastId);
        Assert.Same(player, yes.LastPlayer);
    }

    [Fact]
    public void NoConditionReference_NeverAsksTheEvaluator()
    {
        var conditions = new FakeConditions(false);

        Assert.True(AreaTriggerRequirements.Evaluate(NewPlayer(), Row(), conditions, []).Allowed);
        Assert.Equal(0, conditions.Calls);
    }

    private sealed class FakeGate(AreaTriggerVerdict verdict) : IAreaTriggerGate
    {
        public int Calls { get; private set; }

        public Player? LastPlayer { get; private set; }

        public AreaTriggerTeleport? LastTeleport { get; private set; }

        public AreaTriggerVerdict Check(Player player, AreaTriggerTeleport teleport)
        {
            Calls++;
            LastPlayer = player;
            LastTeleport = teleport;
            return verdict;
        }
    }

    private sealed class FakeConditions(bool result) : IConditionEvaluator
    {
        public int Calls { get; private set; }

        public uint LastId { get; private set; }

        public Player? LastPlayer { get; private set; }

        public bool IsSatisfied(uint conditionId, Player player, NpcInfo? source)
        {
            Calls++;
            LastId = conditionId;
            LastPlayer = player;
            return result;
        }
    }
}

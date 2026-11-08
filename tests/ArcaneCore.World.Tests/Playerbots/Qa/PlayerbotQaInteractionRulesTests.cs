using ArcaneCore.Game.Diagnostics;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.World.Playerbots.Qa;
using Xunit;
using InventoryResult = ArcaneCore.Game.Items.InventoryResult;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>The expected-versus-suspicious tables of the interaction and quest kinds.</summary>
public sealed class PlayerbotQaInteractionRulesTests
{
    public static TheoryData<InteractionFailure, bool, QaCause> Interactions => new()
    {
        // A refusal of play: only a bot that repeats it is reported.
        { InteractionFailure.BuyVendorUnreachable, false, QaCause.BehaviourBug },
        { InteractionFailure.BuySoldOut, false, QaCause.BehaviourBug },
        { InteractionFailure.BuyReputation, false, QaCause.BehaviourBug },
        { InteractionFailure.BuyHonorRank, false, QaCause.BehaviourBug },
        { InteractionFailure.BuyNoMoney, false, QaCause.BehaviourBug },
        { InteractionFailure.BuybackVendorUnreachable, false, QaCause.BehaviourBug },
        { InteractionFailure.BuybackNoMoney, false, QaCause.BehaviourBug },
        { InteractionFailure.SellVendorUnreachable, false, QaCause.BehaviourBug },
        { InteractionFailure.TrainerUnreachable, false, QaCause.BehaviourBug },
        { InteractionFailure.TrainerSpellNotReady, false, QaCause.BehaviourBug },
        { InteractionFailure.TrainerNoMoney, false, QaCause.BehaviourBug },
        // What should not happen: reported at once.
        { InteractionFailure.BuyItemTemplateMissing, true, QaCause.MissingData },
        { InteractionFailure.BuyNotSold, true, QaCause.BehaviourBug },
        { InteractionFailure.BuyStoreFailed, true, QaCause.NotImplemented },
        { InteractionFailure.BuybackEmptySlot, true, QaCause.BehaviourBug },
        { InteractionFailure.SellNoItemService, true, QaCause.NotImplemented },
        { InteractionFailure.TrainerNotForPlayer, true, QaCause.BehaviourBug },
        { InteractionFailure.TrainerNoSpellService, true, QaCause.NotImplemented },
        { InteractionFailure.TrainerSpellNotListed, true, QaCause.BehaviourBug },
        { InteractionFailure.TrainerSpellUnknown, true, QaCause.MissingData },
        { InteractionFailure.TrainerTeachingFailed, true, QaCause.NotImplemented },
    };

    [Theory]
    [MemberData(nameof(Interactions))]
    public void EveryDecisionSite_HasItsClass(InteractionFailure failure, bool suspicious, QaCause cause)
    {
        QaInteractionRule rule = PlayerbotQaInteractionRules.Of(failure, 0);
        Assert.Equal((suspicious, cause), (rule.Class == QaRefusalClass.Suspicious, rule.Cause));
        Assert.Equal(rule, PlayerbotQaInteractionRules.OfToken(rule.Token, 0)); // the token of a finding finds its rule again
    }

    [Fact]
    public void EveryDecisionSite_HasARule_AndItsOwnToken()
    {
        InteractionFailure[] all = Enum.GetValues<InteractionFailure>();
        foreach (InteractionFailure failure in all) Assert.DoesNotContain("interaction-", PlayerbotQaInteractionRules.Of(failure, 0).Token, StringComparison.Ordinal);
        Assert.Equal(all.Length, all.Select(f => PlayerbotQaInteractionRules.Of(f, 0).Token).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(all.Length, Interactions.Count + 5); // the table above, plus the three code-dependent sites and the two store sites
    }

    [Theory]
    [InlineData(InventoryResult.BagFull, false)]
    [InlineData(InventoryResult.InventoryFull, false)]
    [InlineData(InventoryResult.CantEquipLevelI, false)]
    [InlineData(InventoryResult.YouAreDead, false)]
    [InlineData(InventoryResult.ItemNotFound, true)]
    [InlineData(InventoryResult.SlotIsEmpty, true)]
    [InlineData(InventoryResult.DontOwnThatItem, true)]
    [InlineData(InventoryResult.IntBagError, true)]
    [InlineData(InventoryResult.YouCanNeverUseThatItem, true)]
    [InlineData(InventoryResult.ItemDoesntGoToSlot, true)]
    public void InventoryResults_AreExpectedUnlessTheBotActedOnWhatItDoesNotHave(InventoryResult result, bool suspicious)
    {
        Assert.Equal(suspicious, PlayerbotQaInteractionRules.Of(InteractionFailure.InventoryChangeFailed, (int)result).Class == QaRefusalClass.Suspicious);
        Assert.Equal(suspicious, PlayerbotQaInteractionRules.Of(InteractionFailure.BuyCannotStore, (int)result).Class == QaRefusalClass.Suspicious);
    }

    [Theory]
    [InlineData(SellResult.CantSellItem, false)]
    [InlineData(SellResult.OnlyEmptyBag, false)]
    [InlineData(SellResult.CantFindItem, true)]
    [InlineData(SellResult.YouDontOwnThatItem, true)]
    [InlineData(SellResult.Unk, true)]
    public void SellResults(SellResult result, bool suspicious)
        => Assert.Equal(suspicious, PlayerbotQaInteractionRules.Of(InteractionFailure.SellRefused, (int)result).Class == QaRefusalClass.Suspicious);

    [Theory]
    [InlineData(LootResult.Unsupported, true)]
    [InlineData(LootResult.TooFar, false)]
    [InlineData(LootResult.NotAllowed, false)]
    [InlineData(LootResult.NothingToLoot, false)]
    [InlineData(LootResult.Locked, false)]
    public void LootResults(LootResult result, bool suspicious)
        => Assert.Equal(suspicious, PlayerbotQaInteractionRules.Of(InteractionFailure.LootRefused, (int)result).Class == QaRefusalClass.Suspicious);

    [Theory]
    [InlineData(QuestFailure.Unsupported, QuestInvalidReason.DontHaveReq, true)]
    [InlineData(QuestFailure.TurnInRefused, QuestInvalidReason.DontHaveReq, true)]
    [InlineData(QuestFailure.TurnInRefused, QuestInvalidReason.InventoryFull, false)]
    [InlineData(QuestFailure.Invalid, QuestInvalidReason.LowLevel, false)]
    [InlineData(QuestFailure.Invalid, QuestInvalidReason.NotEnoughMoney, false)]
    [InlineData(QuestFailure.Failed, QuestInvalidReason.DontHaveReq, false)]
    public void QuestRefusals(QuestFailure failure, QuestInvalidReason code, bool suspicious)
    {
        QaInteractionRule rule = PlayerbotQaInteractionRules.Of(failure, (int)code);
        Assert.Equal(suspicious, rule.Class == QaRefusalClass.Suspicious);
        Assert.Equal(rule, PlayerbotQaInteractionRules.OfQuestToken(rule.Token, (int)code));
    }

    [Fact]
    public void AnObjectiveThatNeverProgresses_IsSuspicious()
    {
        foreach (string token in new[] { PlayerbotQaInteractionRules.NoProgressKill, PlayerbotQaInteractionRules.NoProgressItem, PlayerbotQaInteractionRules.NoProgressObject })
        {
            QaInteractionRule rule = PlayerbotQaInteractionRules.NoProgress(token);
            Assert.Equal((QaRefusalClass.Suspicious, QaCause.NotImplemented, token), (rule.Class, rule.Cause, rule.Token));
        }
    }

    [Theory]
    [InlineData("buy-no-money", 2, "NotEnoughMoney (2)")]
    [InlineData("buy-not-sold", 0, "CantFindItem (0)")]
    [InlineData("sell-refused", 2, "CantSellItem (2)")]
    [InlineData("inventory", 4, "BagFull (4)")]
    [InlineData("buy-cannot-store", 50, "InventoryFull (50)")]
    [InlineData("loot", 8, "Unsupported (8)")]
    [InlineData("train-no-money", 1, "NotEnoughMoney (1)")]
    [InlineData("train-teaching-failed", 0, "Unavailable (0)")]
    public void CodeNames(string token, int code, string name) => Assert.Equal(name, PlayerbotQaInteractionRules.CodeName(token, code));

    [Fact]
    public void TheCauseText_NamesTheContent_AndTheQuestObjectives()
    {
        var facts = new NamedFacts();
        QaClassification buy = PlayerbotQaClassifier.Classify(new QaSignal
        {
            Kind = QaSignalKind.InteractionFailed, Reason = "buy-not-sold", Npc = 3312, Item = 159, Code = 0,
        }, facts);
        Assert.Equal("creature 3312 (Ellandrieth), item 159 (Refreshing Spring Water) [CantFindItem (0)]: the vendor does not list the item, or not for this "
            + "character (npc_vendor row, condition, faction): the bot chose an item the server does not offer it", buy.Detail);

        QaClassification quest = PlayerbotQaClassifier.Classify(new QaSignal
        {
            Kind = QaSignalKind.QuestBroken, Quest = 783, Reason = PlayerbotQaInteractionRules.NoProgressKill, EffectIndex = 0, Npc = 3098,
        }, facts);
        Assert.Equal(QaCause.NotImplemented, quest.Cause);
        Assert.Equal("A Threat Within", quest.QuestTitle);
        Assert.Contains("quest 783 (A Threat Within) objective 0, creature 3098 (Mottled Boar): the bot killed the objective creature", quest.Detail, StringComparison.Ordinal);
        Assert.EndsWith("Objectives: \"Kill 8 boars.\"; kill/speak 8 x creature 3098 (Mottled Boar)", quest.Detail, StringComparison.Ordinal);

        QaClassification go = PlayerbotQaClassifier.Classify(new QaSignal { Kind = QaSignalKind.ObjectUnsupported, GameObject = 179697, Code = 26, Reason = "type" }, facts);
        Assert.StartsWith("game object 179697 (Silverwing Flag) (type 26 FlagDrop): the type has no use handler", go.Detail, StringComparison.Ordinal);

        QaClassification summon = PlayerbotQaClassifier.Classify(new QaSignal
        {
            Kind = QaSignalKind.ScriptUnsupported, Npc = 3312, Reason = "summon", Target = 777, Detail = "script summon",
        }, facts);
        Assert.Equal((QaCause.MissingData, "creature 3312 (Ellandrieth) summons creature 777, which has no creature_template row: the summon is skipped (script summon)"),
            (summon.Cause, summon.Detail));
    }

    /// <summary>Content facts with names and a quest's objectives (the interaction kinds' issue text).</summary>
    private sealed class NamedFacts : IQaContentFacts
    {
        public bool Available => true;

        public string? QuestTitle(uint quest) => quest == 783 ? "A Threat Within" : null;

        public int? QuestLevel(uint quest) => null;

        public IReadOnlyList<uint> QuestObjectiveCreatures(uint quest) => [];

        public IReadOnlyList<uint> QuestEnderEntries(uint quest) => [];

        public IReadOnlyList<QaEnderFact> QuestEnders(uint quest, uint entry) => [];

        public int SpawnCount(uint map, uint entry) => 0;

        public int SpawnCountAnyMap(uint entry) => 0;

        public IReadOnlyList<(float X, float Y)> SpawnPositions(uint map, uint entry) => [];

        public QaTemplateFacts? Template(uint entry) => null;

        public bool HasWaypointPath(uint entry, uint pathId) => false;

        public bool HasScript(uint entry, uint map) => false;

        public string? CreatureName(uint entry) => entry switch { 3312 => "Ellandrieth", 3098 => "Mottled Boar", _ => null };

        public string? GameObjectName(uint entry) => entry == 179697 ? "Silverwing Flag" : null;

        public string? ItemName(uint entry) => entry == 159 ? "Refreshing Spring Water" : null;

        public string QuestObjectives(uint quest) => quest == 783 ? "\"Kill 8 boars.\"; kill/speak 8 x creature 3098 (Mottled Boar)" : string.Empty;
    }
}

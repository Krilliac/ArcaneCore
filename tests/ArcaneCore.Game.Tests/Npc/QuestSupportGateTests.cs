using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Quests;
using Xunit;

namespace ArcaneCore.Game.Tests.Npc;

/// <summary>
/// The quest support gate: <see cref="QuestNeeds"/> (a pure classifier), the adapter registry and
/// Quests:RewardMode. vmangos has no reward allowlist (Player::CanRewardQuest, Player.cpp:12682-12739); the allowlist
/// survives only as Quests:RewardMode = AllowlistOnly.
/// </summary>
public sealed class QuestSupportGateTests
{
    private const uint Id = 920001;

    private static Quest QuestOf(QuestTemplate template) => new QuestStore(new QuestContent([template], [], [])).Get(template.Entry)!;

    public static TheoryData<string, QuestTemplate, QuestAdapter> Shapes() => new()
    {
        { "ordinary", QuestFlowKit.Task(Id), QuestAdapter.None },
        { "method 0", QuestFlowKit.Task(Id, method: 0), QuestAdapter.Autocomplete },
        { "party accept", QuestFlowKit.Task(Id, flags: (uint)QuestFlags.PartyAccept), QuestAdapter.PartyAccept },
        { "auto rewarded", QuestFlowKit.Task(Id, flags: (uint)QuestFlags.AutoRewarded), QuestAdapter.AutoRewarded },
        { "source spell", new QuestTemplate { Entry = Id, Method = 2, SrcSpell = 100 }, QuestAdapter.SrcSpell },
        { "loot source", QuestFlowKit.Task(Id, reqSource: 5, reqSourceCount: 1), QuestAdapter.ReqSource },
        { "pvp type", QuestFlowKit.Task(Id, type: 41), QuestAdapter.PvpType },
        { "reputation objective", new QuestTemplate { Entry = Id, Method = 2, RepObjectiveFaction = 72, RepObjectiveValue = 3000 }, QuestAdapter.RepObjective },
        { "exploration", QuestFlowKit.Task(Id, special: (byte)QuestSpecialFlags.ExplorationOrEvent), QuestAdapter.EventCredit },
        { "reward mail", new QuestTemplate { Entry = Id, Method = 2, RewMailTemplateId = -84 }, QuestAdapter.Mail },
        {
            "method 0 + party accept + mail",
            new QuestTemplate { Entry = Id, Method = 0, QuestFlags = (uint)QuestFlags.PartyAccept, RewMailTemplateId = 3 },
            QuestAdapter.Autocomplete | QuestAdapter.PartyAccept | QuestAdapter.Mail
        },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void QuestNeeds_Of_ClassifiesEachTemplateShape(string name, QuestTemplate template, QuestAdapter expected)
    {
        Assert.Equal(expected, QuestNeeds.Of(QuestOf(template)));
        Assert.False(string.IsNullOrEmpty(name));
    }

    [Theory]
    [InlineData(0x1u)] // STAY_ALIVE: "Not used currently" (vmangos QuestDef.h:150)
    [InlineData(0x4u)] // EXPLORATION: "Not used currently" (QuestDef.h:152)
    [InlineData(0x5u)]
    public void UnusedFlags_AreNotNeeds(uint flags)
        => Assert.Equal(QuestAdapter.None, QuestNeeds.Of(QuestOf(QuestFlowKit.Task(Id, flags: flags))));

    [Theory]
    [InlineData(1u)] // elite
    [InlineData(21u)] // life
    [InlineData(62u)] // raid
    [InlineData(81u)] // dungeon
    [InlineData(82u)] // world event: client icon only
    [InlineData(83u)] // legendary
    [InlineData(84u)] // escort
    public void QuestTypesOtherThanPvp_AreNotNeeds(uint type)
        => Assert.Equal(QuestAdapter.None, QuestNeeds.Of(QuestOf(QuestFlowKit.Task(Id, type: type))));

    [Fact]
    public void DefaultConfig_RewardsAnOrdinaryQuest_WithoutAnyAllowlistEntry()
    {
        using var kit = new QuestFlowKit([QuestFlowKit.Task(Id)], [QuestFlowKit.Row(Id, QuestStatus.Complete, kills: 2)],
            starters: [Id], enders: [Id]);
        Assert.Equal(QuestRewardMode.AllSupported, kit.Services.Options.RewardMode);
        Assert.Empty(kit.Services.Options.OrdinaryRewardQuestIds);
        Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, Id, 0, out _));
    }

    [Fact]
    public void AllowlistOnlyMode_RefusesAnUnlistedQuest_AndAcceptsAListedOne()
    {
        QuestTemplate[] quests = [QuestFlowKit.Task(Id), QuestFlowKit.Task(Id + 1)];
        CharacterQuestStatus[] rows = [QuestFlowKit.Row(Id, QuestStatus.Complete, kills: 2), QuestFlowKit.Row(Id + 1, QuestStatus.Complete, kills: 2)];
        using var kit = new QuestFlowKit(quests, rows, starters: [Id, Id + 1], enders: [Id, Id + 1], rewardable: [Id],
            configure: o => o.RewardMode = QuestRewardMode.AllowlistOnly);
        Assert.False(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, Id + 1, 0, out _));
        Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, Id, 0, out _));
    }

    [Fact]
    public void StayAliveQuest_IsOfferedAndRewarded_BecauseTheFlagIsUnusedInRetail()
    {
        using var kit = new QuestFlowKit([QuestFlowKit.Task(Id, flags: 0x1)], [QuestFlowKit.Row(Id, QuestStatus.Complete, kills: 2)],
            starters: [Id], enders: [Id]);
        Assert.True(kit.Services.Supported(kit.Services.Quests.Get(Id)!));
        Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, Id, 0, out _));
    }

    [Fact]
    public void WithheldQuest_IsNotAccepted_AndTheSummaryCountsItByReason()
    {
        QuestTemplate[] quests =
        [
            QuestFlowKit.Task(Id), QuestFlowKit.Task(Id + 1, flags: (uint)QuestFlags.PartyAccept), QuestFlowKit.Task(Id + 2, flags: (uint)QuestFlags.PartyAccept),
            new QuestTemplate { Entry = Id + 3, Method = 2, SrcSpell = 5, RewMailTemplateId = 9 },
            QuestFlowKit.Task(Id + 4, method: 1),
        ];
        using var kit = new QuestFlowKit(quests, starters: [Id, Id + 1, Id + 2, Id + 3, Id + 4]);
        Assert.False(kit.Accept(Id + 1));
        Assert.True(kit.Accept(Id));

        QuestSupportSummary summary = kit.Services.SupportSummary();
        // The Method 1 quest is disabled, not withheld, so it is not an active quest.
        Assert.Equal(4, summary.ActiveQuests);
        Assert.Equal(1, summary.Supported);
        Assert.Equal(3, summary.Withheld);
        Assert.Equal(2, summary.WithheldByReason[QuestAdapter.PartyAccept]);
        Assert.Equal(1, summary.WithheldByReason[QuestAdapter.SrcSpell]);
        Assert.Equal(1, summary.WithheldByReason[QuestAdapter.Mail]);
    }

    [Fact]
    public void ReputationObjective_IsANeedOnlyWithoutAReputationOwner()
    {
        using var kit = new QuestFlowKit([new QuestTemplate { Entry = Id, Method = 2, RepObjectiveFaction = 72, RepObjectiveValue = 1 }], starters: [Id]);
        Assert.Equal(QuestAdapter.RepObjective, kit.Services.MissingAdapters(kit.Services.Quests.Get(Id)!));
        Assert.Equal(QuestAdapter.None, kit.Services.ProvidedAdapters & QuestAdapter.RepObjective);
    }

    [Fact]
    public void ExplorationQuest_IsCoveredByAnAreaTriggerRelation()
    {
        QuestTemplate quest = QuestFlowKit.Task(Id, special: (byte)QuestSpecialFlags.ExplorationOrEvent);
        using var without = new QuestFlowKit([quest], starters: [Id]);
        Assert.Equal(QuestAdapter.EventCredit, without.Services.MissingAdapters(without.Services.Quests.Get(Id)!));
        using var with = new QuestFlowKit([quest], starters: [Id], configure: o => o.AreaTriggerQuests = [new QuestAreaTrigger { TriggerId = 9, QuestId = Id }]);
        Assert.Equal(QuestAdapter.None, with.Services.MissingAdapters(with.Services.Quests.Get(Id)!));
    }

    [Fact]
    public void TwoProvidersOfOneAdapter_FailTheMerge_AndAnEventModuleCoversItsQuests()
    {
        using var kit = new QuestFlowKit([QuestFlowKit.Task(Id)], starters: [Id]);
        Assert.Throws<InvalidOperationException>(() => kit.Services.MergeProviders([new Provider(QuestAdapter.Mail), new OtherProvider(QuestAdapter.Mail | QuestAdapter.SrcSpell)]));
        (QuestAdapter provided, HashSet<uint> covered) = kit.Services.MergeProviders(
            [new Provider(QuestAdapter.Mail, Id), new OtherProvider(QuestAdapter.SrcSpell)]);
        Assert.Equal(QuestAdapter.Mail | QuestAdapter.SrcSpell | QuestAdapter.ReqSource, provided);
        Assert.Equal([Id], covered);
    }

    [Fact]
    public void DiscoveredModules_NeverProvideTheSameAdapterTwice()
    {
        // The first support query merges every discovered module; a duplicate provider would throw here.
        using var kit = new QuestFlowKit([QuestFlowKit.Task(Id)], starters: [Id]);
        _ = kit.Services.SupportSummary();
        // PvP quests, loot-source quests and autocomplete quests have adapters; party confirmation does not exist yet.
        Assert.Equal(QuestAdapter.PvpType | QuestAdapter.ReqSource | QuestAdapter.Autocomplete, kit.Services.ProvidedAdapters);
    }

    private sealed class Provider(QuestAdapter adapters, params uint[] covered) : IQuestAdapterModule
    {
        public QuestAdapter Provides(QuestNpcServices services) => adapters;

        public IEnumerable<uint> EventQuestsCovered(QuestNpcServices services) => covered;
    }

    private sealed class OtherProvider(QuestAdapter adapters) : IQuestAdapterModule
    {
        public QuestAdapter Provides(QuestNpcServices services) => adapters;
    }
}

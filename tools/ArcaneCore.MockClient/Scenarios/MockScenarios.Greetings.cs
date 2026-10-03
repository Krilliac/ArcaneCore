using ArcaneCore.MockClient.Hosting;
using ArcaneCore.Protocol;

namespace ArcaneCore.MockClient.Scenarios;

public static partial class MockScenarios
{
    private const int TemporaryNpcQuestField = NpcQuestIdField + 3;

    private static async Task GreetingListAsync(ScenarioConnection connection, WorldOpcode hello, CancellationToken token)
    {
        await connection.SendAsync(hello, ScenarioWire.Guid(SyntheticArcaneServer.NpcGuid), token).ConfigureAwait(false);
        MockQuestList list = ScenarioWire.QuestList(await connection.ReadUntilAsync(WorldOpcode.SmsgQuestgiverQuestList, token).ConfigureAwait(false));
        Require(list is { Guid: SyntheticArcaneServer.NpcGuid, Greeting: "", EmoteDelay: 0, Emote: 0 }
            && list.Quests.OrderBy(quest => quest.QuestId).SequenceEqual(new[]
            {
                new MockQuestMenuEntry(SyntheticArcaneServer.NpcQuestId, 5, 1, "Mock NPC quest"),
                new MockQuestMenuEntry(SyntheticArcaneServer.RewardQuestId, 5, 1, "Mock combat reward"),
            }), "NPC greeting did not list exactly its two eligible ordinary quests with full vanilla fields.");
    }

    private static async Task GreetingAvailableRewardAsync(ScenarioConnection connection, CancellationToken token)
    {
        await connection.SendAsync(WorldOpcode.CmsgGossipHello, ScenarioWire.Guid(SyntheticArcaneServer.NpcGuid), token).ConfigureAwait(false);
        ValidateRewardDetails(ScenarioWire.QuestDetails(await connection.ReadUntilAsync(WorldOpcode.SmsgQuestgiverQuestDetails, token).ConfigureAwait(false)));
    }

    private static async Task GreetingMixedRewardAsync(ScenarioConnection connection, bool complete, CancellationToken token)
    {
        await connection.SendAsync(WorldOpcode.CmsgGossipHello, ScenarioWire.Guid(SyntheticArcaneServer.NpcGuid), token).ConfigureAwait(false);
        MockQuestList list = ScenarioWire.QuestList(await connection.ReadUntilAsync(WorldOpcode.SmsgQuestgiverQuestList, token).ConfigureAwait(false));
        Require(list is { Guid: SyntheticArcaneServer.NpcGuid, Greeting: "", EmoteDelay: 0, Emote: 0 }
            && list.Quests.OrderBy(quest => quest.QuestId).SequenceEqual(new[]
            {
                new MockQuestMenuEntry(SyntheticArcaneServer.NpcQuestId, 5, 1, "Mock NPC quest"),
                new MockQuestMenuEntry(SyntheticArcaneServer.RewardQuestId, complete ? 4u : 3u, 1, "Mock combat reward"),
            }), "Mixed greeting did not retain available and current quest entries with their exact phase icons.");
    }

    private static void ValidateRewardDetails(MockQuestDetails details)
        => Require(details is { Guid: SyntheticArcaneServer.NpcGuid, QuestId: SyntheticArcaneServer.RewardQuestId,
            Title: "Mock combat reward", Details: "synthetic combat and durable reward", Objectives: "defeat two live synthetic targets",
            ActivateAccept: 1, Money: (int)SyntheticArcaneServer.RewardMoney, Spell: 0 }
            && details.Choices.SequenceEqual(new[]
            {
                new MockQuestReward(SyntheticArcaneServer.UnchosenRewardItem, 1, SyntheticArcaneServer.UnchosenRewardItem + 100),
                new MockQuestReward(SyntheticArcaneServer.ChosenRewardItem, 1, SyntheticArcaneServer.ChosenRewardItem + 100),
            })
            && details.Rewards.SequenceEqual(new[] { new MockQuestReward(SyntheticArcaneServer.FixedRewardItem, 1, SyntheticArcaneServer.FixedRewardItem + 100) })
            && details.Emotes.Length == 4 && details.Emotes.All(emote => emote is { Id: 0, Delay: 0 }),
            "Selected reward quest details differ from the actual synthetic ordinary reward.");

    private static async Task AcceptTemporaryNpcQuestAsync(ScenarioConnection connection, ulong guid, CancellationToken token)
    {
        await connection.SendAsync(WorldOpcode.CmsgQuestgiverAcceptQuest,
            ScenarioWire.GuidQuest(SyntheticArcaneServer.NpcGuid, SyntheticArcaneServer.NpcQuestId), token).ConfigureAwait(false);
        Require((await connection.ReadUntilAsync(WorldOpcode.SmsgGossipComplete, token).ConfigureAwait(false)).Length == 0,
            "Temporary ordinary quest acceptance did not close gossip.");
        await connection.ReadUntilFieldAsync(guid, TemporaryNpcQuestField, SyntheticArcaneServer.NpcQuestId, token).ConfigureAwait(false);
        Require(connection.FieldsOf(guid).GetValueOrDefault(TemporaryNpcQuestField + 1) == 0
            && connection.FieldsOf(guid).GetValueOrDefault(TemporaryNpcQuestField + 2) == 0,
            "Temporary ordinary quest did not start with zero progress and timer in slot two.");
        ValidateOriginalJournalDelta(connection.FieldsOf(guid));
    }

    private static async Task GreetingIncompleteRewardAsync(ScenarioConnection connection, CancellationToken token)
    {
        await connection.SendAsync(WorldOpcode.CmsgGossipHello, ScenarioWire.Guid(SyntheticArcaneServer.NpcGuid), token).ConfigureAwait(false);
        await ReadIncompleteRewardAsync(connection, token).ConfigureAwait(false);
    }

    private static async Task ReadIncompleteRewardAsync(ScenarioConnection connection, CancellationToken token, uint closeOnCancel = 1)
    {
        MockQuestRequestItems request = ScenarioWire.QuestRequestItems(await connection.ReadUntilAsync(WorldOpcode.SmsgQuestgiverRequestItems, token).ConfigureAwait(false));
        Require(request is { Guid: SyntheticArcaneServer.NpcGuid, QuestId: SyntheticArcaneServer.RewardQuestId,
            Title: "Mock combat reward", Text: "Defeat both synthetic combat targets before returning.",
            EmoteDelay: 0, Emote: 0, RequiredMoney: 0 } && request.CloseOnCancel == closeOnCancel
            && request.RequiredItems.Length == 0 && request.CompleteFlags.SequenceEqual(new uint[] { 2, 0, 4, 8 }),
            "Incomplete greeting did not return exact RequestItems text and disabled completion flags.");
    }

    private static async Task GreetingCompletedRewardAsync(ScenarioConnection connection, CancellationToken token)
    {
        await connection.SendAsync(WorldOpcode.CmsgQuestgiverHello, ScenarioWire.Guid(SyntheticArcaneServer.NpcGuid), token).ConfigureAwait(false);
        ValidateRewardOffer(ScenarioWire.QuestOfferReward(await connection.ReadUntilAsync(WorldOpcode.SmsgQuestgiverOfferReward, token).ConfigureAwait(false)));
    }

    private static async Task GreetingAvailableNpcAsync(ScenarioConnection connection, CancellationToken token)
    {
        await connection.SendAsync(WorldOpcode.CmsgGossipHello, ScenarioWire.Guid(SyntheticArcaneServer.NpcGuid), token).ConfigureAwait(false);
        ValidateNpcDetails(ScenarioWire.QuestDetails(await connection.ReadUntilAsync(WorldOpcode.SmsgQuestgiverQuestDetails, token).ConfigureAwait(false)));
    }
}

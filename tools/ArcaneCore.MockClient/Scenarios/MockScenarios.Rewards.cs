using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.MockClient.Scenarios;

public static partial class MockScenarios
{
    private const int RewardQuestCountField = NpcQuestIdField + 1;
    private const int MoneyField = 0x0498;
    private const int InventorySlotField = 0x01E6;
    private const int ItemEntryField = 0x0003;
    private const int ItemCountField = 0x000E;
    private const uint CompleteCountTwo = 0x01000002;

    private static async Task RunRewardFlowAsync(SyntheticArcaneServer server, ScenarioConnection connection,
        ulong guid, List<MockScenarioCheck> checks, CancellationToken token)
    {
        await AssertVisibleRewardTargetsAsync(server, connection, guid, token).ConfigureAwait(false);
        Check(checks, "reward.live-targets", "Both distinct low health target spawns and the starter/ender exist in the real map and the player's actual visible-object set.");
        await GreetingListAsync(connection, WorldOpcode.CmsgGossipHello, token).ConfigureAwait(false);
        Check(checks, "reward.greeting-list", "The combat lifecycle began from a real NPC greeting listing both eligible quests.");
        await connection.SendAsync(WorldOpcode.CmsgQuestgiverQueryQuest,
            ScenarioWire.GuidQuest(SyntheticArcaneServer.NpcGuid, SyntheticArcaneServer.RewardQuestId), token).ConfigureAwait(false);
        ValidateRewardDetails(ScenarioWire.QuestDetails(await connection.ReadUntilAsync(WorldOpcode.SmsgQuestgiverQuestDetails, token).ConfigureAwait(false)));
        Check(checks, "reward.greeting-details", "Selecting listed 900003 decoded its objectives, fixed reward, both choices and money before real acceptance.");
        await AcceptRewardQuestAsync(server, connection, guid, token).ConfigureAwait(false);
        Check(checks, "reward.accept-fields", "Quest 900003 occupies the empty second journal slot with zero objective counters and no timer.");
        await GreetingMixedRewardAsync(connection, complete: false, token).ConfigureAwait(false);
        Check(checks, "reward.greeting-mixed-incomplete", "After acceptance the list contained available 900002/icon five and current incomplete 900003/icon three without changing progress.");
        await connection.SendAsync(WorldOpcode.CmsgQuestgiverCompleteQuest,
            ScenarioWire.GuidQuest(SyntheticArcaneServer.NpcGuid, SyntheticArcaneServer.RewardQuestId), token).ConfigureAwait(false);
        await ReadIncompleteRewardAsync(connection, token, closeOnCancel: 0).ConfigureAwait(false);
        Check(checks, "reward.greeting-current-selection", "Selecting the current incomplete quest returned the exact progress text and disabled completion flags.");
        await AcceptTemporaryNpcQuestAsync(connection, guid, token).ConfigureAwait(false);
        await GreetingIncompleteRewardAsync(connection, token).ConfigureAwait(false);
        await ChooseRewardAsync(connection, 1, token).ConfigureAwait(false);
        await connection.AssertNoRewardUntilPongAsync(0x90000320, token).ConfigureAwait(false);
        ValidateRewardObservation(await ObserveRewardAsync(server, guid, token).ConfigureAwait(false), rewarded: false);
        await ValidatePersistedRewardAsync(server, guid, rewarded: false, count: 0, token).ConfigureAwait(false);
        Check(checks, "reward.greeting-incomplete", "With 900002 temporarily accepted into slot two, the sole-current greeting disabled completion and an early real choice granted nothing.");

        await KillRewardTargetAsync(server, connection, guid, SyntheticArcaneServer.FirstTargetGuid, 1, token).ConfigureAwait(false);
        await ValidatePersistedRewardAsync(server, guid, rewarded: false, count: 1, token).ConfigureAwait(false);
        Check(checks, "reward.kill-partial", "An exact eight-byte CMSG_ATTACKSWING killed spawn 900021 through MapCombat and produced count one, incomplete journal fields and a saved objective row.");
        await GreetingIncompleteRewardAsync(connection, token).ConfigureAwait(false);
        Check(checks, "reward.greeting-partial", "After the first actual kill, greeting retained the progress text and disabled completion flags at objective count one.");
        await KillRewardTargetAsync(server, connection, guid, SyntheticArcaneServer.SecondTargetGuid, 2, token).ConfigureAwait(false);
        await ValidatePersistedRewardAsync(server, guid, rewarded: false, count: 2, token).ConfigureAwait(false);
        Check(checks, "reward.kill-complete", "A second real client swing killed spawn 900022, produced count two and set the journal complete bit while retaining original journal progress.");
        await GreetingCompletedRewardAsync(connection, token).ConfigureAwait(false);
        Check(checks, "reward.greeting-complete", "After two actual deaths, the sole-current questgiver hello returned the fully decoded reward offer.");
        await connection.SendAsync(WorldOpcode.CmsgQuestlogRemoveQuest, [2], token).ConfigureAwait(false);
        await connection.ReadUntilFieldAsync(guid, TemporaryNpcQuestField, 0, token).ConfigureAwait(false);
        await ValidatePersistedNpcQuestAsync(server, guid, expectedStatus: 0, token).ConfigureAwait(false);
        ValidateOriginalJournalDelta(connection.FieldsOf(guid));
        Check(checks, "reward.greeting-restore-slot", "A real slot-two abandonment restored 900002's prior None history while preserving the completed reward slot and original journal.");
        await GreetingMixedRewardAsync(connection, complete: true, token).ConfigureAwait(false);
        Check(checks, "reward.greeting-mixed-complete", "The completed mixed list contained available 900002/icon five and completed unrewarded 900003/icon four.");

        await connection.SendAsync(WorldOpcode.CmsgQuestgiverCompleteQuest,
            ScenarioWire.GuidQuest(SyntheticArcaneServer.NpcGuid, SyntheticArcaneServer.RewardQuestId), token).ConfigureAwait(false);
        ValidateRewardOffer(ScenarioWire.QuestOfferReward(await connection.ReadUntilAsync(WorldOpcode.SmsgQuestgiverOfferReward, token).ConfigureAwait(false)));
        Check(checks, "reward.complete-offer", "The twelve-byte complete request returned the full offer reward body with both choices, fixed reward, 1234 copper, emotes and the pinned zero flags/spell suffix.");
        Check(checks, "reward.greeting-complete-selection", "Selecting completed 900003 from the mixed menu returned the same strict offer before any settlement.");
        await connection.SendAsync(WorldOpcode.CmsgQuestgiverRequestReward,
            ScenarioWire.GuidQuest(SyntheticArcaneServer.NpcGuid, SyntheticArcaneServer.RewardQuestId), token).ConfigureAwait(false);
        ValidateRewardOffer(ScenarioWire.QuestOfferReward(await connection.ReadUntilAsync(WorldOpcode.SmsgQuestgiverOfferReward, token).ConfigureAwait(false)));
        Check(checks, "reward.request-offer", "The distinct twelve-byte request reward exchange returned the same fully decoded offer.");

        await ChooseRewardAsync(connection, choice: 1, token).ConfigureAwait(false);
        MockQuestComplete complete = ScenarioWire.QuestComplete(await connection.ReadUntilAsync(WorldOpcode.SmsgQuestgiverQuestComplete, token).ConfigureAwait(false));
        Require(complete is { QuestId: SyntheticArcaneServer.RewardQuestId, Type: 3, Experience: 0, Money: SyntheticArcaneServer.RewardMoney }
            && complete.Rewards.Length == 1 && complete.Rewards[0] is { ItemId: SyntheticArcaneServer.FixedRewardItem, Count: 1 },
            "Quest reward success differs from the exact fixed-reward-only completion layout.");
        Check(checks, "reward.choose-complete", "Zero-based choice slot one selected item 900042; opcode 0x0191 contains quest id, type three, zero XP, 1234 copper and only the fixed item pair, without a GUID or chosen item.");
        MockFieldUpdate money = await connection.ReadUntilFieldAsync(guid, MoneyField, SyntheticArcaneServer.RewardMoney, token).ConfigureAwait(false);
        ValidateOriginalJournalDelta(money.Fields);
        RewardObservation reward = await ObserveRewardAsync(server, guid, token).ConfigureAwait(false);
        ValidateRewardObservation(reward, rewarded: true);
        ValidateRewardWireFields(connection, guid, reward);
        Check(checks, "reward.inventory-money-fields", "Independently decoded player coinage, cleared quest fields, backpack item GUIDs and both item entry/count fields; item 900041 is absent.");
        await ValidatePersistedRewardAsync(server, guid, rewarded: true, count: 2, token).ConfigureAwait(false);
        Check(checks, "reward.atomic-store", "Real character, inventory and quest stores agree on 1234 copper, one fixed item, one chosen item and rewarded history recording item entry 900042.");

        await ChooseRewardAsync(connection, choice: 1, token).ConfigureAwait(false);
        await connection.AssertNoRewardUntilPongAsync(0x90000301, token).ConfigureAwait(false);
        ValidateRewardObservation(await ObserveRewardAsync(server, guid, token).ConfigureAwait(false), rewarded: true);
        await ValidatePersistedRewardAsync(server, guid, rewarded: true, count: 2, token).ConfigureAwait(false);
        Check(checks, "reward.duplicate-choice", "Repeating the sixteen-byte choice request emitted no reward success or item grant, and live/durable copper and item counts stayed unchanged.");
        await GreetingAvailableNpcAsync(connection, token).ConfigureAwait(false);
        Check(checks, "reward.greeting-rewarded", "After settlement and duplicate choice, greeting offered only available 900002 and excluded rewarded nonrepeatable 900003.");
    }

    internal static async Task AcceptRewardQuestAsync(SyntheticArcaneServer server, ScenarioConnection connection,
        ulong guid, CancellationToken token)
    {
        await connection.SendAsync(WorldOpcode.CmsgQuestgiverAcceptQuest,
            ScenarioWire.GuidQuest(SyntheticArcaneServer.NpcGuid, SyntheticArcaneServer.RewardQuestId), token).ConfigureAwait(false);
        Require((await connection.ReadUntilAsync(WorldOpcode.SmsgGossipComplete, token).ConfigureAwait(false)).Length == 0,
            "Combat quest acceptance did not close gossip with an empty packet.");
        await connection.ReadUntilFieldAsync(guid, NpcQuestIdField, SyntheticArcaneServer.RewardQuestId, token).ConfigureAwait(false);
        IReadOnlyDictionary<int, uint> fields = connection.FieldsOf(guid);
        Require(fields.GetValueOrDefault(RewardQuestCountField) == 0 && fields.GetValueOrDefault(RewardQuestCountField + 1) == 0,
            "Accepted combat reward quest did not start with empty count/state and timer fields.");
        ValidateOriginalJournalDelta(fields);
        await ValidatePersistedRewardAsync(server, guid, rewarded: false, count: 0, token).ConfigureAwait(false);
    }

    internal static async Task KillRewardTargetAsync(SyntheticArcaneServer server, ScenarioConnection connection,
        ulong guid, ulong targetGuid, uint count, CancellationToken token)
    {
        await connection.SendAsync(WorldOpcode.CmsgAttackswing, ScenarioWire.Guid(targetGuid), token).ConfigureAwait(false);
        MockQuestKill kill = ScenarioWire.QuestKill(await connection.ReadUntilAsync(WorldOpcode.SmsgQuestupdateAddKill, token).ConfigureAwait(false));
        Require(kill.QuestId == SyntheticArcaneServer.RewardQuestId && kill.CreatureId == SyntheticArcaneServer.TargetEntry
            && kill.Count == count && kill.RequiredCount == 2 && kill.Guid == targetGuid,
            "Combat death objective packet differs from its live target and expected count.");
        uint expectedWord = count == 2 ? CompleteCountTwo : count;
        MockFieldUpdate progress = await connection.ReadUntilFieldAsync(guid, RewardQuestCountField, expectedWord, token).ConfigureAwait(false);
        ValidateOriginalJournalDelta(progress.Fields);
        bool dead = await server.World.InvokeAsync(() =>
        {
            Player? player = server.World.FindOnlinePlayer(new ObjectGuid(guid));
            return player?.Map?.FindObject(new ObjectGuid(targetGuid)) is Unit { Health: 0, IsAlive: false };
        }).WaitAsync(token).ConfigureAwait(false);
        Require(dead, "Client attack did not leave the actual map target dead.");
    }

    internal static Task ChooseRewardAsync(ScenarioConnection connection, uint choice, CancellationToken token)
        => connection.SendAsync(WorldOpcode.CmsgQuestgiverChooseReward,
            ScenarioWire.GuidQuestChoice(SyntheticArcaneServer.NpcGuid, SyntheticArcaneServer.RewardQuestId, choice), token);

    /// <summary>Stop through the real client opcode, then await the map's normal leave-combat check.</summary>
    internal static async Task WaitForCombatExitAsync(SyntheticArcaneServer server, ScenarioConnection connection,
        ulong guid, CancellationToken token)
    {
        await connection.SendAsync(WorldOpcode.CmsgAttackstop, [], token).ConfigureAwait(false);
        while (true)
        {
            bool idle = await server.World.InvokeAsync(() =>
            {
                Player player = server.World.FindOnlinePlayer(new ObjectGuid(guid))
                    ?? throw new InvalidOperationException("Synthetic reward player went offline before leaving combat.");
                return (player.UnitFlags & UnitFlags.InCombat) == 0;
            }).WaitAsync(token).ConfigureAwait(false);
            if (idle)
            {
                return;
            }

            // The map owns combat timing and state; this observes actual ticks without
            // clearing flags or advancing simulation time from the fixture.
            await Task.Delay(25, token).ConfigureAwait(false);
        }
    }

    private static async Task AssertVisibleRewardTargetsAsync(SyntheticArcaneServer server, ScenarioConnection connection,
        ulong guid, CancellationToken token)
    {
        bool visible = await server.World.InvokeAsync(() =>
        {
            Player? player = server.World.FindOnlinePlayer(new ObjectGuid(guid));
            return player?.Map is { } map && new[] { SyntheticArcaneServer.NpcGuid,
                SyntheticArcaneServer.FirstTargetGuid, SyntheticArcaneServer.SecondTargetGuid }.All(value =>
                player.VisibleObjects.Contains(new ObjectGuid(value)) && map.FindObject(new ObjectGuid(value)) is Unit { IsAlive: true });
        }).WaitAsync(token).ConfigureAwait(false);
        Require(visible, "Synthetic NPC or combat targets are missing from actual live visibility.");
        // The map sends self create immediately, then world states, while nearby creates
        // remain queued until its end-of-tick flush. Live visibility can therefore already
        // be true before this client has consumed that next update packet.
        foreach ((ulong objectGuid, uint entry) in new[]
        {
            (SyntheticArcaneServer.NpcGuid, SyntheticArcaneServer.NpcEntry),
            (SyntheticArcaneServer.FirstTargetGuid, SyntheticArcaneServer.TargetEntry),
            (SyntheticArcaneServer.SecondTargetGuid, SyntheticArcaneServer.TargetEntry),
        })
        {
            if (!connection.FieldsOf(objectGuid).ContainsKey(ItemEntryField))
            {
                await connection.ReadUntilFieldAsync(objectGuid, ItemEntryField, entry, token).ConfigureAwait(false);
            }
        }

        Require(connection.FieldsOf(SyntheticArcaneServer.NpcGuid).GetValueOrDefault(ItemEntryField) == SyntheticArcaneServer.NpcEntry
            && connection.FieldsOf(SyntheticArcaneServer.FirstTargetGuid).GetValueOrDefault(ItemEntryField) == SyntheticArcaneServer.TargetEntry
            && connection.FieldsOf(SyntheticArcaneServer.SecondTargetGuid).GetValueOrDefault(ItemEntryField) == SyntheticArcaneServer.TargetEntry,
            "Client did not receive the real NPC and target creates before its quest/combat requests.");
    }

    private static void ValidateRewardOffer(MockQuestOfferReward offer)
    {
        Require(offer is { Guid: SyntheticArcaneServer.NpcGuid, QuestId: SyntheticArcaneServer.RewardQuestId,
            Title: "Mock combat reward", Text: "Choose one synthetic keepsake.", EnableNext: 1,
            Money: (int)SyntheticArcaneServer.RewardMoney, Flags: 0, Spell: 0 }
            && offer.Choices.SequenceEqual(new[] { new MockQuestReward(SyntheticArcaneServer.UnchosenRewardItem, 1, SyntheticArcaneServer.UnchosenRewardItem + 100),
                new MockQuestReward(SyntheticArcaneServer.ChosenRewardItem, 1, SyntheticArcaneServer.ChosenRewardItem + 100) })
            && offer.Rewards.SequenceEqual(new[] { new MockQuestReward(SyntheticArcaneServer.FixedRewardItem, 1, SyntheticArcaneServer.FixedRewardItem + 100) })
            && offer.Emotes.Length == 0,
            "Offer reward body differs from the supported synthetic quest and vanilla suffix.");
    }

    internal static async Task<RewardObservation> ObserveRewardAsync(SyntheticArcaneServer server, ulong guid, CancellationToken token)
        => await server.World.InvokeAsync(() =>
        {
            Player player = server.World.FindOnlinePlayer(new ObjectGuid(guid))
                ?? throw new InvalidOperationException("Synthetic reward player is offline.");
            return new RewardObservation(player.Money, player.Inventory.CreateSnapshot().Items.ToArray(),
                player.GetUInt32(NpcQuestIdField), player.GetUInt32(RewardQuestCountField),
                player.GetUInt32(QuestIdField), player.GetUInt32(QuestCountField));
        }).WaitAsync(token).ConfigureAwait(false);

    internal static void ValidateRewardObservation(RewardObservation reward, bool rewarded)
    {
        Require(reward.JournalQuest == SyntheticArcaneServer.JournalQuestId && reward.JournalProgress == 1,
            "Combat reward changed the original saved quest journal.");
        Require(reward.Money == (rewarded ? SyntheticArcaneServer.RewardMoney : 0)
            && reward.QuestSlot == (rewarded ? 0 : SyntheticArcaneServer.RewardQuestId),
            "Live combat reward money or quest slot differs.");
        if (rewarded)
        {
            ValidateRewardItems(reward.Items);
            Require(reward.QuestProgress == 0, "Rewarded journal slot retained objective/state bits.");
        }
        else
        {
            Require(reward.Items.Count == 0, "Unsettled combat quest granted an item.");
        }
    }

    private static void ValidateRewardWireFields(ScenarioConnection connection, ulong guid, RewardObservation reward)
    {
        IReadOnlyDictionary<int, uint> player = connection.FieldsOf(guid);
        Require(player.GetValueOrDefault(MoneyField) == SyntheticArcaneServer.RewardMoney
            && player.GetValueOrDefault(NpcQuestIdField) == 0 && player.GetValueOrDefault(RewardQuestCountField) == 0
            && player.GetValueOrDefault(RewardQuestCountField + 1) == 0,
            "Wire reward coinage and cleared quest journal fields differ.");
        ValidateOriginalJournalDelta(player);
        foreach (InventoryItemData item in reward.Items)
        {
            ulong itemGuid = ((ulong)0x4000 << 48) | item.Item.Guid;
            int slotField = InventorySlotField + (item.Slot * 2);
            Require(item.ContainerGuid == 0 && item.Slot is >= 23 and < 39
                && player.GetValueOrDefault(slotField) == (uint)itemGuid
                && player.GetValueOrDefault(slotField + 1) == (uint)(itemGuid >> 32),
                "Reward inventory slot fields do not contain the actual full item GUID.");
            IReadOnlyDictionary<int, uint> fields = connection.FieldsOf(itemGuid);
            Require(fields.GetValueOrDefault(ItemEntryField) == item.Item.Entry && fields.GetValueOrDefault(ItemCountField) == 1,
                "Reward item create fields contain a different entry or count.");
        }
    }

    internal static async Task ValidatePersistedRewardAsync(SyntheticArcaneServer server, ulong guid,
        bool rewarded, uint count, CancellationToken token)
    {
        int characterId = checked((int)guid);
        await server.FlushCharacterAsync(characterId, token).ConfigureAwait(false);
        await using AsyncServiceScope scope = server.Services.CreateAsyncScope();
        CharacterQuestData data = await scope.ServiceProvider.GetRequiredService<ICharacterQuestStore>().LoadAsync(characterId, token).ConfigureAwait(false);
        CharacterQuestStatus? row = data.Quests.SingleOrDefault(value => value.Quest == SyntheticArcaneServer.RewardQuestId);
        Require(row is not null && row.Status == (count == 2 ? 1 : 3) && row.Rewarded == rewarded && row.MobCount1 == count
            && row.RewardChoice == (rewarded ? SyntheticArcaneServer.ChosenRewardItem : 0)
            && row is { Explored: false, Timer: 0, MobCount2: 0, MobCount3: 0, MobCount4: 0,
                ItemCount1: 0, ItemCount2: 0, ItemCount3: 0, ItemCount4: 0 },
            "Durable combat quest status, progress or chosen item entry differs.");
        CharacterRecord? character = await scope.ServiceProvider.GetRequiredService<ICharacterStore>().GetByIdAsync(characterId, token).ConfigureAwait(false);
        Require(character is not null && character.Money == (rewarded ? SyntheticArcaneServer.RewardMoney : 0),
            "Durable character coinage differs from the settled reward.");
        IReadOnlyList<InventoryItemData> inventory = await scope.ServiceProvider.GetRequiredService<IItemStore>().GetInventoryAsync(characterId, token).ConfigureAwait(false);
        if (rewarded)
        {
            ValidateRewardItems(inventory);
        }
        else
        {
            Require(inventory.Count == 0, "Durable unrewarded quest inventory contains a reward.");
        }
    }

    private static void ValidateRewardItems(IReadOnlyList<InventoryItemData> items)
        => Require(items.Count == 2 && items.Count(item => item.Item is { Entry: SyntheticArcaneServer.FixedRewardItem, Count: 1 }) == 1
            && items.Count(item => item.Item is { Entry: SyntheticArcaneServer.ChosenRewardItem, Count: 1 }) == 1
            && items.All(item => item.Item.Entry != SyntheticArcaneServer.UnchosenRewardItem),
            "Reward inventory does not contain exactly one fixed and chosen item with the unchosen item absent.");
}

internal sealed record RewardObservation(uint Money, IReadOnlyList<InventoryItemData> Items, uint QuestSlot,
    uint QuestProgress, uint JournalQuest, uint JournalProgress);

using System.Net.Sockets;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Quests;
using ArcaneCore.Data.Stores;
using ArcaneCore.Game;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

/// <summary>Rejected and interrupted turn-ins on authenticated, owned loopback sessions and real SQLite stores.</summary>
public sealed class QuestRewardAdversarialTests
{
    private const string AccountName = "REWARDTEST";
    private const string Password = "PASSWORD";
    private const int RewardQuestField = 0x00C9;
    private const int RewardProgressField = 0x00CA;
    private const int MoneyField = 0x0498;

    [Theory]
    [InlineData(WorldOpcode.CmsgQuestgiverCompleteQuest, 11)]
    [InlineData(WorldOpcode.CmsgQuestgiverCompleteQuest, 13)]
    [InlineData(WorldOpcode.CmsgQuestgiverRequestReward, 11)]
    [InlineData(WorldOpcode.CmsgQuestgiverRequestReward, 13)]
    [InlineData(WorldOpcode.CmsgQuestgiverChooseReward, 15)]
    [InlineData(WorldOpcode.CmsgQuestgiverChooseReward, 17)]
    public async Task MalformedRewardRequest_DisconnectsWithoutChangingTheAcceptedQuest(WorldOpcode opcode, int length)
    {
        using var deadline = TestDeadline();
        CancellationToken token = deadline.Token;
        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(token);
        await using OwnedCharacterClient owned = await CreateAcceptedQuestAsync(server, token);
        byte[] valid = opcode == WorldOpcode.CmsgQuestgiverChooseReward
            ? ScenarioWire.GuidQuestChoice(SyntheticArcaneServer.NpcGuid, SyntheticArcaneServer.RewardQuestId, 1)
            : ScenarioWire.GuidQuest(SyntheticArcaneServer.NpcGuid, SyntheticArcaneServer.RewardQuestId);
        byte[] malformed = new byte[length];
        valid.AsSpan(0, Math.Min(valid.Length, malformed.Length)).CopyTo(malformed);

        await owned.Connection.SendAsync(opcode, malformed, token);
        await AssertDisconnectedWithoutRewardAsync(owned.Client, token);
        await owned.Client.DisposeAsync();
        await WaitUntilOfflineAsync(server, token);
        await AssertStoredAsync(server, owned.Guid, rewarded: false, kills: 0, token);

        await using OwnedCharacterClient reconnected = await ReconnectAsync(server, owned.Guid, token);
        Assert.Equal(SyntheticArcaneServer.RewardQuestId, reconnected.Connection.FieldsOf(owned.Guid).GetValueOrDefault(RewardQuestField));
        Assert.Equal(0u, reconnected.Connection.FieldsOf(owned.Guid).GetValueOrDefault(RewardProgressField));
        MockScenarios.ValidateRewardObservation(await MockScenarios.ObserveRewardAsync(server, owned.Guid, token), rewarded: false);
    }

    [Fact]
    public async Task CompleteAndChooseBeforeCombat_CannotCreditObjectivesOrGrantRewards()
    {
        using var deadline = TestDeadline();
        CancellationToken token = deadline.Token;
        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(token);
        await using OwnedCharacterClient owned = await CreateAcceptedQuestAsync(server, token);

        await owned.Connection.SendAsync(WorldOpcode.CmsgQuestgiverCompleteQuest,
            ScenarioWire.GuidQuest(SyntheticArcaneServer.NpcGuid, SyntheticArcaneServer.RewardQuestId), token);
        Assert.NotEmpty(await owned.Connection.ReadUntilAsync(WorldOpcode.SmsgQuestgiverRequestItems, token));
        await owned.Connection.SendAsync(WorldOpcode.CmsgQuestgiverRequestReward,
            ScenarioWire.GuidQuest(SyntheticArcaneServer.NpcGuid, SyntheticArcaneServer.RewardQuestId), token);
        await AssertRejectedUntilPongAsync(owned.Connection, 0x90000310, token);
        await MockScenarios.ChooseRewardAsync(owned.Connection, 1, token);
        await AssertRejectedUntilPongAsync(owned.Connection, 0x90000311, token);

        RewardObservation live = await MockScenarios.ObserveRewardAsync(server, owned.Guid, token);
        MockScenarios.ValidateRewardObservation(live, rewarded: false);
        Assert.Equal(0u, live.QuestProgress);
        await AssertStoredAsync(server, owned.Guid, rewarded: false, kills: 0, token);
        await owned.Connection.LogoutAsync(token);
        await owned.Client.DisposeAsync();
        await using OwnedCharacterClient reconnected = await ReconnectAsync(server, owned.Guid, token);
        Assert.Equal(0u, reconnected.Connection.FieldsOf(owned.Guid).GetValueOrDefault(RewardProgressField));
        await AssertStoredAsync(server, owned.Guid, rewarded: false, kills: 0, token);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task WrongCreatureStaleGuidOrInvalidChoice_DoesNotSettleACompletedQuest(int rejection)
    {
        using var deadline = TestDeadline();
        CancellationToken token = deadline.Token;
        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(token);
        await using OwnedCharacterClient owned = await CreateCompletedQuestAsync(server, token);
        ulong npc = rejection switch
        {
            0 => SyntheticArcaneServer.FirstTargetGuid,
            1 => SyntheticArcaneServer.NpcGuid + 1,
            _ => SyntheticArcaneServer.NpcGuid,
        };
        uint choice = rejection switch
        {
            2 => 2,
            3 => uint.MaxValue,
            4 => SyntheticArcaneServer.ChosenRewardItem,
            _ => 1,
        };

        await owned.Connection.SendAsync(WorldOpcode.CmsgQuestgiverChooseReward,
            ScenarioWire.GuidQuestChoice(npc, SyntheticArcaneServer.RewardQuestId, choice), token);
        await AssertRejectedUntilPongAsync(owned.Connection, 0x90000320 + (uint)rejection, token);
        RewardObservation live = await MockScenarios.ObserveRewardAsync(server, owned.Guid, token);
        MockScenarios.ValidateRewardObservation(live, rewarded: false);
        Assert.Equal(0x01000002u, live.QuestProgress);
        await AssertStoredAsync(server, owned.Guid, rewarded: false, kills: 2, token);
    }

    [Fact]
    public async Task FullBackpack_PreservesEveryItemMoneyAndCompletedQuestAcrossRelog()
    {
        using var deadline = TestDeadline();
        CancellationToken token = deadline.Token;
        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(token);
        await using OwnedCharacterClient owned = await CreateCompletedQuestAsync(server, token);
        await LogoutAfterCombatAsync(server, owned, token);
        await owned.Client.DisposeAsync();
        await server.FlushCharacterAsync(checked((int)owned.Guid), token);
        InventoryItemData[] full = Enumerable.Range(23, 16).Select(slot => new InventoryItemData(0, (byte)slot,
            new ItemInstanceData { Guid = 10_000u + (uint)slot, Entry = SyntheticArcaneServer.UnchosenRewardItem, Count = 20 })).ToArray();
        await using (AsyncServiceScope scope = server.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IItemStore>().SaveInventoryAsync(
                checked((int)owned.Guid), new InventorySnapshot(full), token);
        }

        await using OwnedCharacterClient filled = await ReconnectAsync(server, owned.Guid, token);
        await MockScenarios.ChooseRewardAsync(filled.Connection, 1, token);
        Assert.NotEmpty(await filled.Connection.ReadUntilAsync(WorldOpcode.SmsgInventoryChangeFailure, token));
        await AssertRejectedUntilPongAsync(filled.Connection, 0x90000330, token);
        RewardObservation live = await MockScenarios.ObserveRewardAsync(server, owned.Guid, token);
        Assert.Equal(0u, live.Money);
        Assert.Equal(SyntheticArcaneServer.RewardQuestId, live.QuestSlot);
        Assert.Equal(0x01000002u, live.QuestProgress);
        AssertSameItems(full, live.Items);
        await AssertStoredAsync(server, owned.Guid, rewarded: false, kills: 2, token, full);
        await filled.Connection.LogoutAsync(token);
        await filled.Client.DisposeAsync();

        await using OwnedCharacterClient reconnected = await ReconnectAsync(server, owned.Guid, token);
        RewardObservation restored = await MockScenarios.ObserveRewardAsync(server, owned.Guid, token);
        Assert.Equal(0u, restored.Money);
        Assert.Equal(SyntheticArcaneServer.RewardQuestId, restored.QuestSlot);
        Assert.Equal(0x01000002u, restored.QuestProgress);
        AssertSameItems(full, restored.Items);
        await AssertStoredAsync(server, owned.Guid, rewarded: false, kills: 2, token, full);
    }

    [Fact]
    public async Task FailedCommit_DoesNotGrantAnythingAndRetryCommitsExactlyOnce()
    {
        using var deadline = TestDeadline();
        CancellationToken token = deadline.Token;
        var fault = new RewardCommitFault(afterCommit: false);
        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(
            services => ReplaceRewardStore(services, fault), token);
        await using OwnedCharacterClient owned = await CreateCompletedQuestAsync(server, token);

        await MockScenarios.ChooseRewardAsync(owned.Connection, 1, token);
        await fault.StoreEntered.Task.WaitAsync(token);
        await server.Services.GetRequiredService<QuestNpcFeature>()
            .WaitForSettlementAsync(checked((int)owned.Guid), token);
        await AssertRejectedUntilPongAsync(owned.Connection, 0x90000340, token);
        Assert.Equal(1, fault.Attempts);
        RewardObservation rejected = await MockScenarios.ObserveRewardAsync(server, owned.Guid, token);
        MockScenarios.ValidateRewardObservation(rejected, rewarded: false);
        Assert.Equal(0x01000002u, rejected.QuestProgress);
        await AssertStoredAsync(server, owned.Guid, rewarded: false, kills: 2, token);

        await MockScenarios.ChooseRewardAsync(owned.Connection, 1, token);
        MockQuestComplete complete = ScenarioWire.QuestComplete(
            await owned.Connection.ReadUntilAsync(WorldOpcode.SmsgQuestgiverQuestComplete, token));
        Assert.Equal(SyntheticArcaneServer.RewardQuestId, complete.QuestId);
        await owned.Connection.ReadUntilFieldAsync(owned.Guid, MoneyField, SyntheticArcaneServer.RewardMoney, token);
        Assert.Equal(2, fault.Attempts);
        await AssertStoredAsync(server, owned.Guid, rewarded: true, kills: 2, token);
        await MockScenarios.ChooseRewardAsync(owned.Connection, 1, token);
        await AssertRejectedUntilPongAsync(owned.Connection, 0x90000341, token);
        Assert.Equal(2, fault.Attempts);
        await LogoutAfterCombatAsync(server, owned, token);
        await owned.Client.DisposeAsync();

        await using OwnedCharacterClient reconnected = await ReconnectAsync(server, owned.Guid, token);
        MockScenarios.ValidateRewardObservation(await MockScenarios.ObserveRewardAsync(server, owned.Guid, token), rewarded: true);
        await MockScenarios.ChooseRewardAsync(reconnected.Connection, 1, token);
        await AssertRejectedUntilPongAsync(reconnected.Connection, 0x90000342, token);
        Assert.Equal(2, fault.Attempts);
        await AssertStoredAsync(server, owned.Guid, rewarded: true, kills: 2, token);
    }

    [Fact]
    public async Task LostCommitAcknowledgement_RelogLoadsDurableRewardAndDuplicateCannotGrantAgain()
    {
        using var deadline = TestDeadline();
        CancellationToken token = deadline.Token;
        var fault = new RewardCommitFault(afterCommit: true);
        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(
            services => ReplaceRewardStore(services, fault), token);
        await using OwnedCharacterClient owned = await CreateCompletedQuestAsync(server, token);

        await MockScenarios.ChooseRewardAsync(owned.Connection, 1, token);
        await fault.DurableCommit.Task.WaitAsync(token);
        // Do not consume any reward success: both the store acknowledgement and this
        // owned world connection are lost after the actual SQLite commit.
        await owned.Client.DisposeAsync();
        await WaitUntilOfflineAsync(server, token);
        await AssertStoredAsync(server, owned.Guid, rewarded: true, kills: 2, token);
        Assert.Equal(1, fault.Attempts);

        await using OwnedCharacterClient reconnected = await ReconnectAsync(server, owned.Guid, token);
        MockScenarios.ValidateRewardObservation(await MockScenarios.ObserveRewardAsync(server, owned.Guid, token), rewarded: true);
        Assert.Equal(SyntheticArcaneServer.RewardMoney, reconnected.Connection.FieldsOf(owned.Guid).GetValueOrDefault(MoneyField));
        Assert.Equal(0u, reconnected.Connection.FieldsOf(owned.Guid).GetValueOrDefault(RewardQuestField));
        await MockScenarios.ChooseRewardAsync(reconnected.Connection, 1, token);
        await AssertRejectedUntilPongAsync(reconnected.Connection, 0x90000350, token);
        Assert.Equal(1, fault.Attempts);
        await AssertStoredAsync(server, owned.Guid, rewarded: true, kills: 2, token);
        await reconnected.Connection.LogoutAsync(token);
        await reconnected.Client.DisposeAsync();
        await using OwnedCharacterClient secondRelog = await ReconnectAsync(server, owned.Guid, token);
        MockScenarios.ValidateRewardObservation(await MockScenarios.ObserveRewardAsync(server, owned.Guid, token), rewarded: true);
        await AssertStoredAsync(server, owned.Guid, rewarded: true, kills: 2, token);
    }

    [Fact]
    public async Task UnreadableCommitOutcome_BlocksStaleAutosaveAndDisconnectUntilAuthoritativeRelog()
    {
        using var deadline = TestDeadline();
        CancellationToken token = deadline.Token;
        var fault = new RewardCommitFault(afterCommit: true, failReconciliationRead: true);
        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(services =>
        {
            ReplaceRewardStore(services, fault);
            services.AddScoped<ICharacterStore>(provider => new ReconciliationFaultCharacterStore(
                new EfCharacterStore(provider.GetRequiredService<CharacterDbContext>()), fault));
        }, token);
        await using OwnedCharacterClient owned = await CreateCompletedQuestAsync(server, token);
        CharacterSaveQueue saves = server.Services.GetRequiredService<CharacterSaveQueue>();
        int id = checked((int)owned.Guid);
        try
        {
            await MockScenarios.ChooseRewardAsync(owned.Connection, 1, token);
            await fault.DurableCommit.Task.WaitAsync(token);
            await server.World.InvokeAsync(() =>
            {
                var player = server.World.FindOnlinePlayer(new ObjectGuid(owned.Guid))
                    ?? throw new InvalidOperationException("The committed reward player left before reconciliation.");
                fault.StaleAutosave = player.CreateSnapshot(server.World.NowMs) with { Inventory = player.Inventory.CreateSnapshot() };
                server.World.SaveAll(); // Pending live Before must not overwrite the durable After.
                return true;
            }).WaitAsync(token);
            Assert.True(saves.IsQuarantined(id));
            fault.AcknowledgementRelease.TrySetResult(true);
            await fault.ReadFailure.Task.WaitAsync(token);
            await server.Services.GetRequiredService<QuestNpcFeature>().WaitForSettlementAsync(id, token);
            await AssertDisconnectedWithoutRewardAsync(owned.Client, token);
            await WaitUntilOfflineAsync(server, token);
            Assert.True(saves.IsQuarantined(id));
            Assert.Equal(1, fault.Attempts);
            Assert.Equal(1, fault.FailedReads);
            CharacterState stale = Assert.IsType<CharacterState>(fault.StaleAutosave);
            Assert.Equal(0u, stale.Money);
            Assert.Empty(stale.Inventory!.Items);
            saves.Enqueue(stale); // A delayed callback can still own the captured Before.
            await AssertStoredAsync(server, owned.Guid, rewarded: true, kills: 2, token);
            Assert.Equal(0, fault.PostCommitSaveCalls);
            Assert.True(saves.IsQuarantined(id));

            await owned.Client.DisposeAsync();
            await using OwnedCharacterClient reconnected = await ReconnectAsync(server, owned.Guid, token);
            Assert.False(saves.IsQuarantined(id));
            MockScenarios.ValidateRewardObservation(await MockScenarios.ObserveRewardAsync(server, owned.Guid, token), rewarded: true);
            await server.World.InvokeAsync(() =>
            {
                server.World.SaveAll(); // The authoritative After state can now be saved normally.
                return true;
            }).WaitAsync(token);
            await AssertStoredAsync(server, owned.Guid, rewarded: true, kills: 2, token);
            Assert.True(fault.PostCommitSaveCalls > 0);
            await MockScenarios.ChooseRewardAsync(reconnected.Connection, 1, token);
            await AssertRejectedUntilPongAsync(reconnected.Connection, 0x90000360, token);
            Assert.Equal(1, fault.Attempts);
            Assert.Equal(1, fault.FailedReads);
            await AssertStoredAsync(server, owned.Guid, rewarded: true, kills: 2, token);
        }
        finally
        {
            fault.AcknowledgementRelease.TrySetResult(true);
        }
    }

    private static async Task<OwnedCharacterClient> CreateAcceptedQuestAsync(SyntheticArcaneServer server, CancellationToken token)
    {
        await server.AddAccountAsync(AccountName, Password, token);
        WorldClient client = await AuthenticateAsync(server, token);
        try
        {
            var connection = new ScenarioConnection(client);
            await connection.CreateCharacterAsync("Rewardhero", token);
            ulong guid = Assert.Single(await connection.EnumerateAsync(token)).Guid;
            await MockScenarios.SeedJournalAsync(server, guid, token);
            await connection.LoginAsync(guid, token);
            await MockScenarios.AcceptRewardQuestAsync(server, connection, guid, token);
            return new OwnedCharacterClient(client, connection, guid);
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    private static async Task<OwnedCharacterClient> CreateCompletedQuestAsync(SyntheticArcaneServer server, CancellationToken token)
    {
        OwnedCharacterClient owned = await CreateAcceptedQuestAsync(server, token);
        try
        {
            await MockScenarios.KillRewardTargetAsync(server, owned.Connection, owned.Guid, SyntheticArcaneServer.FirstTargetGuid, 1, token);
            await MockScenarios.KillRewardTargetAsync(server, owned.Connection, owned.Guid, SyntheticArcaneServer.SecondTargetGuid, 2, token);
            await AssertStoredAsync(server, owned.Guid, rewarded: false, kills: 2, token);
            return owned;
        }
        catch
        {
            await owned.DisposeAsync();
            throw;
        }
    }

    private static async Task<OwnedCharacterClient> ReconnectAsync(SyntheticArcaneServer server, ulong guid, CancellationToken token)
    {
        await server.FlushCharacterAsync(checked((int)guid), token);
        WorldClient client = await AuthenticateAsync(server, token);
        try
        {
            var connection = new ScenarioConnection(client);
            Assert.Equal(guid, Assert.Single(await connection.EnumerateAsync(token)).Guid);
            await connection.LoginAsync(guid, token);
            return new OwnedCharacterClient(client, connection, guid);
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    private static async Task<WorldClient> AuthenticateAsync(SyntheticArcaneServer server, CancellationToken token)
    {
        LogonResult logon = await LogonClient.AuthenticateAsync(server.RealmEndpoint, AccountName, Password, token);
        var endpoint = Assert.Single(logon.Realms).GetLoopbackEndpoint();
        Assert.Equal(server.WorldEndpoint, endpoint);
        WorldClient client = await WorldClient.ConnectAsync(endpoint, token);
        try
        {
            Assert.Equal((byte)0x0C, await client.AuthenticateAsync(AccountName, logon.SessionKey, token));
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    private static async Task AssertStoredAsync(SyntheticArcaneServer server, ulong guid, bool rewarded, uint kills,
        CancellationToken token, IReadOnlyList<InventoryItemData>? expectedItems = null)
    {
        int characterId = checked((int)guid);
        await server.FlushCharacterAsync(characterId, token);
        await using AsyncServiceScope scope = server.Services.CreateAsyncScope();
        // Read the actual provider independently of injected orchestration faults.
        CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        CharacterRecord character = Assert.IsType<CharacterRecord>(
            await new EfCharacterStore(db).GetByIdAsync(characterId, token));
        Assert.Equal(rewarded ? SyntheticArcaneServer.RewardMoney : 0u, character.Money);
        CharacterQuestData data = await new EfCharacterQuestStore(db).LoadAsync(characterId, token);
        CharacterQuestStatus row = Assert.Single(data.Quests, value => value.Quest == SyntheticArcaneServer.RewardQuestId);
        Assert.Equal((byte)(kills == 2 ? 1 : 3), row.Status);
        Assert.Equal(rewarded, row.Rewarded);
        Assert.Equal(kills, row.MobCount1);
        Assert.Equal(rewarded ? SyntheticArcaneServer.ChosenRewardItem : 0u, row.RewardChoice);
        Assert.Equal(0, row.Timer);
        Assert.False(row.Explored);
        Assert.Equal(0u, row.MobCount2);
        Assert.Equal(0u, row.MobCount3);
        Assert.Equal(0u, row.MobCount4);
        Assert.Equal(0u, row.ItemCount1);
        Assert.Equal(0u, row.ItemCount2);
        Assert.Equal(0u, row.ItemCount3);
        Assert.Equal(0u, row.ItemCount4);
        CharacterQuestStatus journal = Assert.Single(data.Quests, value => value.Quest == SyntheticArcaneServer.JournalQuestId);
        Assert.Equal((byte)3, journal.Status);
        Assert.False(journal.Rewarded);
        Assert.Equal(1u, journal.MobCount1);
        IReadOnlyList<InventoryItemData> inventory = await new EfItemStore(db).GetInventoryAsync(characterId, token);
        if (expectedItems is not null)
        {
            AssertSameItems(expectedItems, inventory);
        }
        else if (rewarded)
        {
            Assert.Equal(2, inventory.Count);
            Assert.Single(inventory, item => item.Item.Entry == SyntheticArcaneServer.FixedRewardItem && item.Item.Count == 1);
            Assert.Single(inventory, item => item.Item.Entry == SyntheticArcaneServer.ChosenRewardItem && item.Item.Count == 1);
            Assert.DoesNotContain(inventory, item => item.Item.Entry == SyntheticArcaneServer.UnchosenRewardItem);
        }
        else
        {
            Assert.Empty(inventory);
        }
    }

    private static void AssertSameItems(IReadOnlyList<InventoryItemData> expected, IReadOnlyList<InventoryItemData> actual)
        => Assert.Equal(expected.OrderBy(item => item.Item.Guid).Select(ItemIdentity), actual.OrderBy(item => item.Item.Guid).Select(ItemIdentity));

    private static (uint Container, byte Slot, uint Guid, uint Entry, uint Count) ItemIdentity(InventoryItemData item)
        => (item.ContainerGuid, item.Slot, item.Item.Guid, item.Item.Entry, item.Item.Count);

    private static async Task AssertRejectedUntilPongAsync(ScenarioConnection connection, uint sequence, CancellationToken token)
    {
        // Ping is answered on the socket task. The following NPC status request is
        // dispatched by the map after the preceding turn-in, and proves it ran.
        await connection.SendAsync(WorldOpcode.CmsgQuestgiverStatusQuery, ScenarioWire.Guid(SyntheticArcaneServer.NpcGuid), token);
        WorldFrame status = await ReadWithoutRewardsUntilAsync(connection, WorldOpcode.SmsgQuestgiverStatus, token);
        Assert.Equal(SyntheticArcaneServer.NpcGuid, ScenarioWire.QuestgiverStatus(status.Payload).Guid);
        await connection.SendAsync(WorldOpcode.CmsgPing, ScenarioWire.Ping(sequence, 0), token);
        WorldFrame pong = await ReadWithoutRewardsUntilAsync(connection, WorldOpcode.SmsgPong, token);
        Assert.Equal(ScenarioWire.UInt32(sequence), pong.Payload);
    }

    private static async Task<WorldFrame> ReadWithoutRewardsUntilAsync(ScenarioConnection connection, WorldOpcode expected, CancellationToken token)
    {
        for (int index = 0; index < 128; index++)
        {
            WorldFrame frame = await connection.ReadAsync(token);
            Assert.NotEqual((ushort)WorldOpcode.SmsgQuestgiverOfferReward, frame.Opcode);
            Assert.NotEqual((ushort)WorldOpcode.SmsgQuestgiverQuestComplete, frame.Opcode);
            Assert.NotEqual((ushort)WorldOpcode.SmsgItemPushResult, frame.Opcode);
            if (frame.Opcode == (ushort)expected)
            {
                return frame;
            }
        }

        throw new MockProtocolException($"Rejected reward did not reach {expected} within 128 frames.");
    }

    private static async Task AssertDisconnectedWithoutRewardAsync(WorldClient client, CancellationToken token)
    {
        for (int index = 0; index < 128; index++)
        {
            try
            {
                WorldFrame frame = await client.ReadAsync(token);
                Assert.NotEqual((ushort)WorldOpcode.SmsgQuestgiverQuestComplete, frame.Opcode);
                Assert.NotEqual((ushort)WorldOpcode.SmsgItemPushResult, frame.Opcode);
            }
            catch (EndOfStreamException)
            {
                return;
            }
            catch (IOException exception) when (exception.InnerException is SocketException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }
        }

        Assert.Fail("Malformed reward did not disconnect its owned client within 128 frames.");
    }

    private static async Task WaitUntilOfflineAsync(SyntheticArcaneServer server, CancellationToken token)
    {
        for (int index = 0; index < 100; index++)
        {
            if (await server.World.InvokeAsync(() => server.World.OnlinePlayerCount).WaitAsync(token) == 0)
            {
                return;
            }

            await Task.Delay(10, token);
        }

        Assert.Fail("Owned disconnected reward player remained online after the cleanup deadline.");
    }

    private static async Task LogoutAfterCombatAsync(SyntheticArcaneServer server, OwnedCharacterClient owned, CancellationToken token)
    {
        await MockScenarios.WaitForCombatExitAsync(server, owned.Connection, owned.Guid, token);
        await owned.Connection.LogoutAsync(token);
    }

    private static void ReplaceRewardStore(IServiceCollection services, RewardCommitFault fault)
        => services.AddScoped<ICharacterQuestRewardStore>(provider => new FaultingRewardStore(
            new EfCharacterQuestRewardStore(provider.GetRequiredService<CharacterDbContext>()), fault));

    private sealed class FaultingRewardStore(ICharacterQuestRewardStore inner, RewardCommitFault fault) : ICharacterQuestRewardStore
    {
        public async Task<QuestRewardCommitResult> CommitAsync(CharacterQuestRewardRequest request, CancellationToken cancellationToken = default)
        {
            int attempt = Interlocked.Increment(ref fault.AttemptCount);
            fault.StoreEntered.TrySetResult(true);
            if (attempt == 1 && !fault.AfterCommit)
            {
                throw new IOException("Synthetic reward store failure before its transaction.");
            }

            QuestRewardCommitResult result = await inner.CommitAsync(request, cancellationToken);
            if (attempt == 1 && fault.AfterCommit && result == QuestRewardCommitResult.Committed)
            {
                Volatile.Write(ref fault.Committed, 1);
                fault.DurableCommit.TrySetResult(true);
                if (fault.FailReconciliationRead)
                {
                    // The test drives its real world autosave before allowing background reconciliation.
                    await fault.AcknowledgementRelease.Task.WaitAsync(cancellationToken);
                    Interlocked.Exchange(ref fault.ArmedReadFailure, 1);
                }

                throw new IOException("Synthetic acknowledgement lost after the real SQLite commit.");
            }

            return result;
        }
    }

    private sealed class ReconciliationFaultCharacterStore(ICharacterStore inner, RewardCommitFault fault) : ICharacterStore
    {
        public Task<CharacterRecord?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref fault.ArmedReadFailure, 0) == 1)
            {
                Interlocked.Increment(ref fault.ReadFailureCount);
                fault.ReadFailure.TrySetResult(true);
                throw new IOException("Synthetic first reconciliation read failed after the real SQLite commit.");
            }

            return inner.GetByIdAsync(id, cancellationToken);
        }

        public Task SaveStateAsync(CharacterState state, CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref fault.Committed) == 1)
            {
                Interlocked.Increment(ref fault.PostCommitSaveCount);
            }

            return inner.SaveStateAsync(state, cancellationToken);
        }

        public Task<IReadOnlyList<CharacterRecord>> GetByAccountAsync(int accountId, CancellationToken cancellationToken = default)
            => inner.GetByAccountAsync(accountId, cancellationToken);
        public Task<bool> IsNameTakenAsync(string name, CancellationToken cancellationToken = default)
            => inner.IsNameTakenAsync(name, cancellationToken);
        public Task<int> CountByAccountAsync(int accountId, CancellationToken cancellationToken = default)
            => inner.CountByAccountAsync(accountId, cancellationToken);
        public Task<CharacterRecord> CreateAsync(CharacterRecord character, CancellationToken cancellationToken = default)
            => inner.CreateAsync(character, cancellationToken);
        public Task<bool> DeleteAsync(int id, int accountId, CancellationToken cancellationToken = default)
            => inner.DeleteAsync(id, accountId, cancellationToken);
        public Task<IReadOnlyList<ActionButton>> GetActionButtonsAsync(int characterId, CancellationToken cancellationToken = default)
            => inner.GetActionButtonsAsync(characterId, cancellationToken);
        public Task<IReadOnlyList<CharacterIdentity>> GetAllIdentitiesAsync(CancellationToken cancellationToken = default)
            => inner.GetAllIdentitiesAsync(cancellationToken);
    }

    private sealed class RewardCommitFault(bool afterCommit, bool failReconciliationRead = false)
    {
        internal readonly bool AfterCommit = afterCommit;
        internal readonly bool FailReconciliationRead = failReconciliationRead;
        internal int AttemptCount;
        internal int Committed;
        internal int ArmedReadFailure;
        internal int ReadFailureCount;
        internal int PostCommitSaveCount;
        internal int Attempts => Volatile.Read(ref AttemptCount);
        internal int FailedReads => Volatile.Read(ref ReadFailureCount);
        internal int PostCommitSaveCalls => Volatile.Read(ref PostCommitSaveCount);
        internal CharacterState? StaleAutosave { get; set; }
        internal TaskCompletionSource<bool> StoreEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> DurableCommit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> AcknowledgementRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> ReadFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record OwnedCharacterClient(WorldClient Client, ScenarioConnection Connection, ulong Guid) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Client.DisposeAsync();
    }

    private static CancellationTokenSource TestDeadline() => new(TimeSpan.FromSeconds(40));
}

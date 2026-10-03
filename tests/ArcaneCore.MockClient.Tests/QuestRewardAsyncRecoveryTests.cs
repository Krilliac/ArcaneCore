using System.Collections.Concurrent;
using System.Net.Sockets;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Quests;
using ArcaneCore.Data.Stores;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Spells;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

/// <summary>Delayed real transactions and fresh authenticated logins exercise settlement ownership and durability.</summary>
public sealed class QuestRewardAsyncRecoveryTests
{
    private const string AccountName = "ASYNCRECOVER";
    private const string Password = "PASSWORD";
    private const int MoneyField = 0x0498;
    private const uint RewardSpellId = 990001;
    private const uint LearnedSpellId = 990002;

    [Theory]
    [InlineData(SpellEffectName.LearnSpell)]
    [InlineData(SpellEffectName.CreateItem)]
    public async Task PermanentRewardSpell_IsRefusedBeforeSettlementAndRemainsAvailableAfterDisconnect(SpellEffectName effect)
    {
        using var deadline = TestDeadline();
        CancellationToken token = deadline.Token;
        var control = new RewardControl(holdAfterSave: false);
        await using SyntheticArcaneServer server = await StartAsync(control, token, RewardSpellId);
        await InstallRewardSpellAsync(server, effect, token);
        await using OwnedCharacterClient original = await CreateCompletedQuestAsync(server, token);
        QuestNpcFeature feature = server.Services.GetRequiredService<QuestNpcFeature>();
        bool prepared = await server.World.InvokeAsync(() => feature.Services.TryPrepareReward(
            server.World.FindOnlinePlayer(new ObjectGuid(original.Guid))!,
            new ObjectGuid(SyntheticArcaneServer.NpcGuid), SyntheticArcaneServer.RewardQuestId, 1, out _)).WaitAsync(token);
        Assert.False(prepared); // Baseline fails here without waiting for a missing packet.
        await MockScenarios.ChooseRewardAsync(original.Connection, 1, token);
        await original.Connection.SendAsync(WorldOpcode.CmsgQuestgiverStatusQuery, ScenarioWire.Guid(SyntheticArcaneServer.NpcGuid), token);
        while (true)
        {
            WorldFrame frame = await original.Connection.ReadAsync(token);
            Assert.NotEqual((ushort)WorldOpcode.SmsgQuestgiverQuestComplete, frame.Opcode);
            Assert.NotEqual((ushort)WorldOpcode.SmsgItemPushResult, frame.Opcode);
            Assert.NotEqual((ushort)WorldOpcode.SmsgLearnedSpell, frame.Opcode);
            if (frame.Opcode == (ushort)WorldOpcode.SmsgQuestgiverStatus) break;
        }

        Assert.Equal(0, control.Attempts);
        Assert.Equal(0, feature.PendingSettlementCount);
        await AssertStoredAsync(server, original.Guid, rewarded: false, token);
        await original.Client.DisposeAsync();
        await WaitUntilOfflineAsync(server, token);
        await using WorldClient nextClient = await AuthenticateAsync(server, token);
        var next = new ScenarioConnection(nextClient);
        await next.LoginAsync(original.Guid, token);
        await AssertLiveAsync(server, original.Guid, rewarded: false, token);
        await AssertStoredAsync(server, original.Guid, rewarded: false, token);
        Assert.Equal(0, control.Attempts);
        await using AsyncServiceScope scope = server.Services.CreateAsyncScope();
        Assert.DoesNotContain(LearnedSpellId, await scope.ServiceProvider.GetRequiredService<ICharacterSpellStore>()
            .GetAsync(checked((int)original.Guid), token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransientReward_RetainedSuccessfulCommitDoesNotReplayToAReplacementPlayer(bool disconnect)
    {
        using var deadline = TestDeadline();
        CancellationToken token = deadline.Token;
        var control = new RewardControl(holdAfterSave: false, holdSuccessfulReturn: true);
        await using SyntheticArcaneServer server = await StartAsync(control, token, RewardSpellId);
        await InstallRewardSpellAsync(server, SpellEffectName.Heal, token);
        var targets = new ConcurrentQueue<Unit>();
        await server.World.InvokeAsync(() =>
        {
            server.Services.GetRequiredService<SpellFeature>().System.SpellHitTarget += (_, target, spell) =>
            {
                if (spell == RewardSpellId) targets.Enqueue(target);
            };
            return true;
        }).WaitAsync(token);
        await using OwnedCharacterClient original = await CreateCompletedQuestAsync(server, token);
        QuestNpcFeature feature = server.Services.GetRequiredService<QuestNpcFeature>();
        Player oldPlayer = await FindPlayerAsync(server, original.Guid, token);
        Task<MockLogin>? login = null;
        WorldClient? replacementClient = null;
        try
        {
            await server.World.InvokeAsync(() => { oldPlayer.Health = 1; return true; }).WaitAsync(token);
            await MockScenarios.ChooseRewardAsync(original.Connection, 1, token);
            await control.Committed.Task.WaitAsync(token);
            Assert.Equal(1, feature.PendingSettlementCount);
            await AssertStoredAsync(server, original.Guid, rewarded: true, token, flush: false);
            Assert.Empty(targets);
            ScenarioConnection connection = original.Connection;
            if (disconnect)
            {
                await original.Client.DisposeAsync();
                await WaitUntilOfflineAsync(server, token);
                replacementClient = await AuthenticateAsync(server, token);
                connection = new ScenarioConnection(replacementClient);
                login = connection.LoginAsync(original.Guid, token);
                await Task.Delay(150, token);
                Assert.False(login.IsCompleted);
            }

            control.AcknowledgementRelease.TrySetResult(true);
            await feature.WaitForSettlementAsync(checked((int)original.Guid), token);
            if (disconnect)
            {
                await login!.WaitAsync(token);
                Assert.NotSame(oldPlayer, await FindPlayerAsync(server, original.Guid, token));
                Assert.Empty(targets);
                Assert.Equal(1u, oldPlayer.Health);
            }
            else
            {
                await ReadSuccessfulRewardAsync(connection, original.Guid, token);
                Assert.Same(oldPlayer, Assert.Single(targets));
                Assert.Equal(21u, await server.World.InvokeAsync(() => oldPlayer.Health).WaitAsync(token));
            }

            await AssertLiveAsync(server, original.Guid, rewarded: true, token);
            await AssertStoredAsync(server, original.Guid, rewarded: true, token);
            await MockScenarios.ChooseRewardAsync(connection, 1, token);
            await connection.AssertNoRewardUntilPongAsync(0x90000451, token);
            Assert.Equal(1, control.Attempts);
            Assert.Equal(disconnect ? 0 : 1, targets.Count);
        }
        finally
        {
            control.ReleaseAll();
            await ObserveCompletionAsync(login);
            if (replacementClient is not null) await replacementClient.DisposeAsync();
        }
    }

    private static Task InstallRewardSpellAsync(SyntheticArcaneServer server, SpellEffectName effect, CancellationToken token)
        => server.World.InvokeAsync(() =>
        {
            SpellSystem system = server.Services.GetRequiredService<SpellFeature>().System;
            system.Store = new SpellStore([.. system.Store.All, new SpellInfo
            {
                Id = RewardSpellId, RangeIndex = SpellConstants.RangeIndexSelfOnly,
                Effects = [new SpellEffectInfo
                {
                    Effect = effect, TargetA = SpellImplicitTarget.UnitCaster,
                    TriggerSpell = effect == SpellEffectName.LearnSpell ? LearnedSpellId : 0,
                    ItemType = SyntheticArcaneServer.FixedRewardItem,
                    BasePoints = 19, BaseDice = 1, DieSides = 1,
                }],
            }, new SpellInfo
            {
                Id = LearnedSpellId, Effects = [new SpellEffectInfo { Effect = SpellEffectName.Heal, TargetA = SpellImplicitTarget.UnitCaster }],
            }], [], []);
            // Prove the permanent-grant policy also holds after the later spell branch installs its adapter.
            if (effect == SpellEffectName.CreateItem) system.RegisterEffect(effect, static _ => { });
            return true;
        }).WaitAsync(token);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisconnectDuringDelayedSave_LoginWaitsForCommitOrRollbackAndOldPlayerCannotOverwriteReplacement(bool cancel)
    {
        using var deadline = TestDeadline();
        CancellationToken token = deadline.Token;
        var control = new RewardControl(holdAfterSave: true);
        await using SyntheticArcaneServer server = await StartAsync(control, token);
        await using OwnedCharacterClient original = await CreateCompletedQuestAsync(server, token);
        QuestNpcFeature feature = server.Services.GetRequiredService<QuestNpcFeature>();
        control.ObserveOperation = feature.PendingOperationId;
        Player oldPlayer = await FindPlayerAsync(server, original.Guid, token);
        Task<MockLogin>? login = null;
        try
        {
            await MockScenarios.ChooseRewardAsync(original.Connection, 1, token);
            await control.Saved.Task.WaitAsync(token);
            Guid operation = Assert.IsType<Guid>(feature.PendingOperationId(checked((int)original.Guid)));
            Assert.NotEqual(Guid.Empty, operation);
            Assert.Equal(1, feature.PendingSettlementCount);
            await AssertStoredAsync(server, original.Guid, rewarded: false, token, flush: false);

            // A repeated request with another choice cannot replace the retained plan.
            await MockScenarios.ChooseRewardAsync(original.Connection, 0, token);
            await AssertNoGrantWhilePendingAsync(original.Connection, 0x90000410, token);
            Assert.Equal(operation, feature.PendingOperationId(checked((int)original.Guid)));
            Assert.Equal(1, control.Attempts);
            Assert.Equal(SyntheticArcaneServer.ChosenRewardItem, control.Request!.RewardedQuest.RewardChoice);
            await original.Client.DisposeAsync();
            await WaitUntilOfflineAsync(server, token);

            await using WorldClient nextClient = await AuthenticateAsync(server, token);
            var next = new ScenarioConnection(nextClient);
            Assert.Equal(original.Guid, Assert.Single(await next.EnumerateAsync(token)).Guid);
            login = next.LoginAsync(original.Guid, token);
            Task settlement = feature.WaitForSettlementAsync(checked((int)original.Guid), token);
            await Task.Delay(150, token);
            Assert.False(login.IsCompleted);
            Assert.False(settlement.IsCompleted);
            Assert.Equal(0, server.World.OnlinePlayerCount);

            if (cancel)
            {
                control.CancelCurrentOperation();
            }
            else
            {
                control.SaveRelease.TrySetResult(true);
            }

            await settlement.WaitAsync(token);
            await login.WaitAsync(token);
            Assert.Equal(0, feature.PendingSettlementCount);
            Assert.Null(feature.PendingOperationId(checked((int)original.Guid)));
            Player replacement = await FindPlayerAsync(server, original.Guid, token);
            Assert.NotSame(oldPlayer, replacement);
            bool retained = await server.World.InvokeAsync(() =>
            {
                // These are real callbacks an old session can still have queued.
                server.World.SavePlayer(oldPlayer);
                server.World.RemovePlayer(oldPlayer);
                return ReferenceEquals(server.World.FindOnlinePlayer(new ObjectGuid(original.Guid)), replacement)
                    && oldPlayer.Money == 0 && oldPlayer.Inventory.CreateSnapshot().Items.Count == 0;
            }).WaitAsync(token);
            Assert.True(retained);
            await AssertLiveAsync(server, original.Guid, rewarded: !cancel, token);
            await AssertStoredAsync(server, original.Guid, rewarded: !cancel, token);

            if (cancel)
            {
                await MockScenarios.ChooseRewardAsync(next, 1, token);
                await ReadSuccessfulRewardAsync(next, original.Guid, token);
                await feature.WaitForSettlementAsync(checked((int)original.Guid), token);
                Assert.Equal(2, control.Attempts);
                Guid[] operations = control.Operations.ToArray();
                Assert.Equal(2, operations.Length);
                Assert.Equal(operation, operations[0]);
                Assert.NotEqual(operations[0], operations[1]);
            }

            await MockScenarios.ChooseRewardAsync(next, 1, token);
            await next.AssertNoRewardUntilPongAsync(0x90000411, token);
            Assert.Equal(cancel ? 2 : 1, control.Attempts);
            await AssertLiveAsync(server, original.Guid, rewarded: true, token);
            await AssertStoredAsync(server, original.Guid, rewarded: true, token);
        }
        finally
        {
            control.ReleaseAll();
            await ObserveCompletionAsync(login);
        }
    }

    [Fact]
    public async Task QuestFlushFailureAfterCapture_PreservesDirtyCoreSnapshotAcrossDisconnectAndRetry()
    {
        using var deadline = TestDeadline();
        CancellationToken token = deadline.Token;
        var control = new RewardControl(holdAfterSave: false);
        await using SyntheticArcaneServer server = await StartAsync(control, token);
        await using OwnedCharacterClient original = await CreateCompletedQuestAsync(server, token);
        QuestNpcFeature feature = server.Services.GetRequiredService<QuestNpcFeature>();
        int id = checked((int)original.Guid);
        try
        {
            CharacterQuestStatus completed;
            await using (AsyncServiceScope scope = server.Services.CreateAsyncScope())
            {
                completed = Assert.Single((await new EfCharacterQuestStore(
                    scope.ServiceProvider.GetRequiredService<CharacterDbContext>()).LoadAsync(id, token)).Quests,
                    quest => quest.Quest == SyntheticArcaneServer.RewardQuestId);
            }

            Volatile.Write(ref control.FailQuestWrites, 1);
            await server.World.InvokeAsync(() =>
            {
                Player player = server.World.FindOnlinePlayer(new ObjectGuid(original.Guid))!;
                Assert.True(player.SetActionButton(7, 117));
                // Keep the real, combat-completed row unchanged while its ordered write fails.
                feature.Persistence.SaveQuests(id, [completed]);
                return true;
            }).WaitAsync(token);
            await control.QuestWriteEntered.Task.WaitAsync(token);
            await MockScenarios.ChooseRewardAsync(original.Connection, 1, token);
            await control.CoreSnapshotSaved.Task.WaitAsync(token);
            Assert.NotNull(feature.PendingOperationId(id));
            Assert.Equal(0, control.Attempts);
            await original.Client.DisposeAsync();
            await WaitUntilOfflineAsync(server, token);
            control.QuestWriteRelease.TrySetResult(true);
            await feature.WaitForSettlementAsync(id, token);
            Assert.Equal(0, control.Attempts);
            await AssertStoredAsync(server, original.Guid, rewarded: false, token, flush: false);
            await AssertActionButtonStoredAsync(server, id, token);

            Volatile.Write(ref control.FailQuestWrites, 0);
            await using WorldClient nextClient = await AuthenticateAsync(server, token);
            var next = new ScenarioConnection(nextClient);
            Assert.Equal(original.Guid, Assert.Single(await next.EnumerateAsync(token)).Guid);
            await next.LoginAsync(original.Guid, token);
            uint button = await server.World.InvokeAsync(() => server.World.FindOnlinePlayer(new ObjectGuid(original.Guid))!
                .ActionButtons[7]).WaitAsync(token);
            Assert.Equal(117u, button);
            await MockScenarios.ChooseRewardAsync(next, 1, token);
            await ReadSuccessfulRewardAsync(next, original.Guid, token);
            await feature.WaitForSettlementAsync(id, token);
            Assert.Equal(1, control.Attempts);
            await AssertStoredAsync(server, original.Guid, rewarded: true, token);
            await AssertActionButtonStoredAsync(server, id, token);
        }
        finally
        {
            Volatile.Write(ref control.FailQuestWrites, 0);
            control.ReleaseAll();
        }
    }

    [Fact]
    public async Task CancellationAfterRealSaveChanges_PublishesNoGrantAndLiveRetryUsesANewOperation()
    {
        using var deadline = TestDeadline();
        CancellationToken token = deadline.Token;
        var control = new RewardControl(holdAfterSave: true);
        await using SyntheticArcaneServer server = await StartAsync(control, token);
        await using OwnedCharacterClient owned = await CreateCompletedQuestAsync(server, token);
        QuestNpcFeature feature = server.Services.GetRequiredService<QuestNpcFeature>();
        control.ObserveOperation = feature.PendingOperationId;
        try
        {
            await MockScenarios.ChooseRewardAsync(owned.Connection, 1, token);
            await control.Saved.Task.WaitAsync(token);
            Guid first = Assert.IsType<Guid>(feature.PendingOperationId(checked((int)owned.Guid)));
            await AssertStoredAsync(server, owned.Guid, rewarded: false, token, flush: false);
            control.CancelCurrentOperation();
            await feature.WaitForSettlementAsync(checked((int)owned.Guid), token);
            await owned.Connection.AssertNoRewardUntilPongAsync(0x90000420, token);
            Assert.Equal(1, control.Attempts);
            Assert.Null(feature.PendingOperationId(checked((int)owned.Guid)));
            await AssertLiveAsync(server, owned.Guid, rewarded: false, token);
            await AssertStoredAsync(server, owned.Guid, rewarded: false, token);

            await MockScenarios.ChooseRewardAsync(owned.Connection, 1, token);
            await ReadSuccessfulRewardAsync(owned.Connection, owned.Guid, token);
            await feature.WaitForSettlementAsync(checked((int)owned.Guid), token);
            Guid[] operations = control.Operations.ToArray();
            Assert.Equal(2, operations.Length);
            Assert.Equal(first, operations[0]);
            Assert.NotEqual(first, operations[1]);
            await AssertStoredAsync(server, owned.Guid, rewarded: true, token);
            await MockScenarios.ChooseRewardAsync(owned.Connection, 1, token);
            await owned.Connection.AssertNoRewardUntilPongAsync(0x90000421, token);
            Assert.Equal(2, control.Attempts);
        }
        finally
        {
            control.ReleaseAll();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostAcknowledgement_ReconciledOrUnreadableAfterStateSurvivesDisconnectAndFreshRelog(bool unreadable)
    {
        using var deadline = TestDeadline();
        CancellationToken token = deadline.Token;
        var control = new RewardControl(holdAfterSave: false, loseAcknowledgement: true, unreadable: unreadable);
        await using SyntheticArcaneServer server = await StartAsync(control, token);
        await using OwnedCharacterClient original = await CreateCompletedQuestAsync(server, token);
        QuestNpcFeature feature = server.Services.GetRequiredService<QuestNpcFeature>();
        CharacterSaveQueue saves = server.Services.GetRequiredService<CharacterSaveQueue>();
        control.ObserveOperation = feature.PendingOperationId;
        int id = checked((int)original.Guid);
        Task<MockLogin>? login = null;
        try
        {
            await MockScenarios.ChooseRewardAsync(original.Connection, 1, token);
            await control.Committed.Task.WaitAsync(token);
            Assert.Equal(1, feature.PendingSettlementCount);
            Assert.NotNull(feature.PendingOperationId(id));
            await AssertStoredAsync(server, original.Guid, rewarded: true, token, flush: false);
            RewardObservation before = await MockScenarios.ObserveRewardAsync(server, original.Guid, token);
            Assert.Equal(0u, before.Money);
            Assert.Empty(before.Items);
            await server.World.InvokeAsync(() =>
            {
                server.World.SaveAll(); // Pending live Before must not overwrite the committed After.
                return true;
            }).WaitAsync(token);
            await original.Client.DisposeAsync();
            await WaitUntilOfflineAsync(server, token);
            await saves.FlushCharacterAsync(id, token);
            await AssertStoredAsync(server, original.Guid, rewarded: true, token, flush: false);
            Assert.Equal(0, control.PostCommitSaveCalls);

            await using WorldClient nextClient = await AuthenticateAsync(server, token);
            var next = new ScenarioConnection(nextClient);
            Assert.Equal(original.Guid, Assert.Single(await next.EnumerateAsync(token)).Guid);
            if (!unreadable)
            {
                login = next.LoginAsync(original.Guid, token);
                await Task.Delay(150, token);
                Assert.False(login.IsCompleted);
            }

            control.AcknowledgementRelease.TrySetResult(true);
            await feature.WaitForSettlementAsync(id, token);
            if (unreadable)
            {
                await control.ReadFailed.Task.WaitAsync(token);
                Assert.Equal(1, control.FailedReads);
                Assert.True(saves.IsQuarantined(id));
                // A delayed autosave still owns this real pre-commit snapshot.
                // Unknown outcome must keep it blocked until authoritative login.
                saves.Enqueue(control.Request!.Before);
                await saves.FlushCharacterAsync(id, token);
                await AssertStoredAsync(server, original.Guid, rewarded: true, token, flush: false);
                Assert.Equal(0, control.PostCommitSaveCalls);
                Assert.True(saves.IsQuarantined(id));
                login = next.LoginAsync(original.Guid, token);
            }

            await (login ?? throw new InvalidOperationException("The recovery login was not started.")).WaitAsync(token);
            Assert.False(saves.IsQuarantined(id));
            await AssertLiveAsync(server, original.Guid, rewarded: true, token);
            await server.World.InvokeAsync(() =>
            {
                server.World.SaveAll(); // Fresh authoritative state may now be saved.
                return true;
            }).WaitAsync(token);
            await AssertStoredAsync(server, original.Guid, rewarded: true, token);
            Assert.True(control.PostCommitSaveCalls > 0);
            await MockScenarios.ChooseRewardAsync(next, 1, token);
            await next.AssertNoRewardUntilPongAsync(0x90000430, token);
            Assert.Equal(1, control.Attempts);
            Assert.Equal(unreadable ? 1 : 0, control.FailedReads);
            await AssertStoredAsync(server, original.Guid, rewarded: true, token);
        }
        finally
        {
            control.ReleaseAll();
            await ObserveCompletionAsync(login);
        }
    }

    private static Task<SyntheticArcaneServer> StartAsync(RewardControl control, CancellationToken token, uint rewardSpell = 0)
        => SyntheticArcaneServer.StartAsync(services =>
        {
            services.AddScoped<ICharacterQuestRewardStore>(provider => new ControlledRewardStore(
                provider.GetRequiredService<DbContextOptions<CharacterDbContext>>(), control));
            services.AddScoped<ICharacterStore>(provider => new ReadFaultCharacterStore(
                new EfCharacterStore(provider.GetRequiredService<CharacterDbContext>()), control));
            services.AddScoped<ICharacterQuestStore>(provider => new QuestFlushFaultStore(
                new EfCharacterQuestStore(provider.GetRequiredService<CharacterDbContext>()), control));
            if (rewardSpell != 0)
            {
                services.AddScoped<IQuestContentStore>(provider => new RewardSpellContentStore(
                    new EfQuestContentStore(provider.GetRequiredService<WorldDbContext>()), rewardSpell));
            }
        }, token);

    private sealed class RewardSpellContentStore(IQuestContentStore inner, uint spell) : IQuestContentStore
    {
        public async Task<QuestContent> LoadAsync(CancellationToken cancellationToken = default)
        {
            QuestContent content = await inner.LoadAsync(cancellationToken);
            return content with { Templates = content.Templates.Select(quest => quest.Entry != SyntheticArcaneServer.RewardQuestId
                ? quest : new QuestTemplate
                {
                    Entry = quest.Entry, Method = quest.Method, Type = quest.Type, MinLevel = quest.MinLevel, QuestLevel = quest.QuestLevel,
                    Title = quest.Title, Details = quest.Details, Objectives = quest.Objectives,
                    OfferRewardText = quest.OfferRewardText, RequestItemsText = quest.RequestItemsText,
                    ReqCreatureOrGOId1 = quest.ReqCreatureOrGOId1, ReqCreatureOrGOCount1 = quest.ReqCreatureOrGOCount1,
                    RewOrReqMoney = quest.RewOrReqMoney, RewItemId1 = quest.RewItemId1, RewItemCount1 = quest.RewItemCount1,
                    RewChoiceItemId1 = quest.RewChoiceItemId1, RewChoiceItemCount1 = quest.RewChoiceItemCount1,
                    RewChoiceItemId2 = quest.RewChoiceItemId2, RewChoiceItemCount2 = quest.RewChoiceItemCount2,
                    RewSpell = spell,
                }).ToArray() };
        }
    }

    private static async Task<OwnedCharacterClient> CreateCompletedQuestAsync(SyntheticArcaneServer server, CancellationToken token)
    {
        await server.AddAccountAsync(AccountName, Password, token);
        WorldClient client = await AuthenticateAsync(server, token);
        try
        {
            var connection = new ScenarioConnection(client);
            await connection.CreateCharacterAsync("Asyncquest", token);
            ulong guid = Assert.Single(await connection.EnumerateAsync(token)).Guid;
            await MockScenarios.SeedJournalAsync(server, guid, token);
            await connection.LoginAsync(guid, token);
            await MockScenarios.AcceptRewardQuestAsync(server, connection, guid, token);
            await MockScenarios.KillRewardTargetAsync(server, connection, guid, SyntheticArcaneServer.FirstTargetGuid, 1, token);
            await MockScenarios.KillRewardTargetAsync(server, connection, guid, SyntheticArcaneServer.SecondTargetGuid, 2, token);
            await AssertStoredAsync(server, guid, rewarded: false, token);
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

    private static async Task<Player> FindPlayerAsync(SyntheticArcaneServer server, ulong guid, CancellationToken token)
        => await server.World.InvokeAsync(() => server.World.FindOnlinePlayer(new ObjectGuid(guid))
            ?? throw new InvalidOperationException("The owned recovery player is offline.")).WaitAsync(token);

    private static async Task ReadSuccessfulRewardAsync(ScenarioConnection connection, ulong guid, CancellationToken token)
    {
        MockQuestComplete complete = ScenarioWire.QuestComplete(await connection.ReadUntilAsync(WorldOpcode.SmsgQuestgiverQuestComplete, token));
        Assert.Equal(SyntheticArcaneServer.RewardQuestId, complete.QuestId);
        Assert.Equal(SyntheticArcaneServer.RewardMoney, complete.Money);
        await connection.ReadUntilFieldAsync(guid, MoneyField, SyntheticArcaneServer.RewardMoney, token);
    }

    private static async Task AssertNoGrantWhilePendingAsync(ScenarioConnection connection, uint sequence, CancellationToken token)
    {
        // World requests are intentionally dropped during settlement. Ping is handled by
        // the socket reader, so its reply observes the preceding duplicate's enqueue.
        await connection.SendAsync(WorldOpcode.CmsgPing, ScenarioWire.Ping(sequence, 0), token);
        for (int index = 0; index < 1_000; index++)
        {
            WorldFrame frame = await connection.ReadAsync(token);
            Assert.NotEqual((ushort)WorldOpcode.SmsgQuestgiverQuestComplete, frame.Opcode);
            Assert.NotEqual((ushort)WorldOpcode.SmsgItemPushResult, frame.Opcode);
            if (frame.Opcode == (ushort)WorldOpcode.SmsgPong)
            {
                Assert.Equal(ScenarioWire.UInt32(sequence), frame.Payload);
                // Let the normal map queue discard the duplicate while its gate is held.
                await Task.Delay(150, token);
                return;
            }
        }

        Assert.Fail("The pending operation's owned ping did not receive its pong.");
    }

    private static async Task AssertActionButtonStoredAsync(SyntheticArcaneServer server, int id, CancellationToken token)
    {
        await using AsyncServiceScope scope = server.Services.CreateAsyncScope();
        IReadOnlyList<ActionButton> buttons = await new EfCharacterStore(
            scope.ServiceProvider.GetRequiredService<CharacterDbContext>()).GetActionButtonsAsync(id, token);
        Assert.Single(buttons, button => button.Button == 7 && button.Action == 117 && button.Type == 0);
    }

    private static async Task AssertLiveAsync(SyntheticArcaneServer server, ulong guid, bool rewarded, CancellationToken token)
    {
        RewardObservation live = await MockScenarios.ObserveRewardAsync(server, guid, token);
        MockScenarios.ValidateRewardObservation(live, rewarded);
        Assert.Equal(rewarded ? 0u : 0x01000002u, live.QuestProgress);
    }

    private static async Task AssertStoredAsync(SyntheticArcaneServer server, ulong guid, bool rewarded,
        CancellationToken token, bool flush = true)
    {
        int id = checked((int)guid);
        if (flush)
        {
            await server.FlushCharacterAsync(id, token);
        }

        await using AsyncServiceScope scope = server.Services.CreateAsyncScope();
        CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        CharacterRecord character = Assert.IsType<CharacterRecord>(await new EfCharacterStore(db).GetByIdAsync(id, token));
        Assert.Equal(rewarded ? SyntheticArcaneServer.RewardMoney : 0u, character.Money);
        CharacterQuestData quests = await new EfCharacterQuestStore(db).LoadAsync(id, token);
        CharacterQuestStatus reward = Assert.Single(quests.Quests, quest => quest.Quest == SyntheticArcaneServer.RewardQuestId);
        Assert.Equal((byte)1, reward.Status);
        Assert.Equal(rewarded, reward.Rewarded);
        Assert.Equal(2u, reward.MobCount1);
        Assert.Equal(rewarded ? SyntheticArcaneServer.ChosenRewardItem : 0u, reward.RewardChoice);
        Assert.Equal(0L, reward.Timer);
        Assert.False(reward.Explored);
        Assert.Equal(0u, reward.MobCount2);
        Assert.Equal(0u, reward.MobCount3);
        Assert.Equal(0u, reward.MobCount4);
        Assert.Equal(0u, reward.ItemCount1);
        Assert.Equal(0u, reward.ItemCount2);
        Assert.Equal(0u, reward.ItemCount3);
        Assert.Equal(0u, reward.ItemCount4);
        CharacterQuestStatus journal = Assert.Single(quests.Quests, quest => quest.Quest == SyntheticArcaneServer.JournalQuestId);
        Assert.Equal((byte)3, journal.Status);
        Assert.Equal(1u, journal.MobCount1);
        Assert.False(journal.Rewarded);
        IReadOnlyList<InventoryItemData> items = await new EfItemStore(db).GetInventoryAsync(id, token);
        if (rewarded)
        {
            Assert.Equal(2, items.Count);
            Assert.Single(items, item => item.Item.Entry == SyntheticArcaneServer.FixedRewardItem && item.Item.Count == 1);
            Assert.Single(items, item => item.Item.Entry == SyntheticArcaneServer.ChosenRewardItem && item.Item.Count == 1);
            Assert.DoesNotContain(items, item => item.Item.Entry == SyntheticArcaneServer.UnchosenRewardItem);
        }
        else
        {
            Assert.Empty(items);
        }
    }

    private static async Task WaitUntilOfflineAsync(SyntheticArcaneServer server, CancellationToken token)
    {
        for (int index = 0; index < 200; index++)
        {
            if (await server.World.InvokeAsync(() => server.World.OnlinePlayerCount).WaitAsync(token) == 0)
            {
                return;
            }

            await Task.Delay(10, token);
        }

        Assert.Fail("The owned disconnected recovery player remained online.");
    }

    private static async Task ObserveCompletionAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try { await task; }
        catch { /* Cleanup observes a reader closed by the owned connection's disposal. */ }
    }

    private sealed class ControlledRewardStore(DbContextOptions<CharacterDbContext> options, RewardControl control) : ICharacterQuestRewardStore
    {
        public async Task<QuestRewardCommitResult> CommitAsync(CharacterQuestRewardRequest request, CancellationToken cancellationToken = default)
        {
            int attempt = Interlocked.Increment(ref control.AttemptCount);
            control.Request = request;
            if (control.ObserveOperation?.Invoke(request.Before.Id) is { } operation)
            {
                control.Operations.Enqueue(operation);
            }

            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            control.CancelOperation = cancellation.Cancel;
            await using var db = new CharacterDbContext(new DbContextOptionsBuilder<CharacterDbContext>(options)
                .AddInterceptors(new ControlledSaveGate(control, attempt)).Options);
            QuestRewardCommitResult result = await new EfCharacterQuestRewardStore(db).CommitAsync(request, cancellation.Token);
            if (attempt == 1 && result == QuestRewardCommitResult.Committed)
            {
                Volatile.Write(ref control.Durable, 1);
                control.Committed.TrySetResult(true);
                if (control.HoldSuccessfulReturn)
                {
                    await control.AcknowledgementRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                if (control.LoseAcknowledgement)
                {
                    await control.AcknowledgementRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (control.Unreadable)
                    {
                        Interlocked.Exchange(ref control.ArmedReadFailure, 1);
                    }

                    throw new IOException("Synthetic asynchronous acknowledgement lost after the real SQLite commit.");
                }
            }

            return result;
        }
    }

    private sealed class ControlledSaveGate(RewardControl control, int attempt) : SaveChangesInterceptor
    {
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (attempt == 1 && eventData.Context?.ChangeTracker.Entries<CharacterQuestStatusRow>().Any(entry => entry.Entity.Rewarded) == true)
            {
                control.Saved.TrySetResult(true);
                if (control.HoldAfterSave)
                {
                    await control.SaveRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            return result;
        }
    }

    private sealed class ReadFaultCharacterStore(ICharacterStore inner, RewardControl control) : ICharacterStore
    {
        public Task<CharacterRecord?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref control.ArmedReadFailure, 0) == 1)
            {
                Interlocked.Increment(ref control.ReadFailureCount);
                control.ReadFailed.TrySetResult(true);
                throw new IOException("Synthetic first asynchronous reconciliation read failed after durable commit.");
            }

            return inner.GetByIdAsync(id, cancellationToken);
        }

        public async Task SaveStateAsync(CharacterState state, CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref control.Durable) == 1)
            {
                Interlocked.Increment(ref control.PostCommitSaveCount);
            }

            await inner.SaveStateAsync(state, cancellationToken);
            if (state.ActionButtons?.Any(button => button.Button == 7 && button.Action == 117 && button.Type == 0) == true)
            {
                control.CoreSnapshotSaved.TrySetResult(true);
            }
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

    private sealed class QuestFlushFaultStore(ICharacterQuestStore inner, RewardControl control) : ICharacterQuestStore
    {
        public Task<CharacterQuestData> LoadAsync(int characterId, CancellationToken cancellationToken = default)
            => inner.LoadAsync(characterId, cancellationToken);

        public async Task SaveQuestsAsync(int characterId, IReadOnlyList<CharacterQuestStatus> upserts,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref control.FailQuestWrites) == 1)
            {
                control.QuestWriteEntered.TrySetResult(true);
                await control.QuestWriteRelease.Task.WaitAsync(cancellationToken);
                throw new IOException("Synthetic ordered quest write and barrier recovery failed before reward transaction.");
            }

            await inner.SaveQuestsAsync(characterId, upserts, cancellationToken);
        }

        public Task SaveTaxiMaskAsync(int characterId, IReadOnlyList<uint> mask, CancellationToken cancellationToken = default)
            => inner.SaveTaxiMaskAsync(characterId, mask, cancellationToken);
    }

    private sealed class RewardControl(bool holdAfterSave, bool loseAcknowledgement = false, bool unreadable = false,
        bool holdSuccessfulReturn = false)
    {
        internal readonly bool HoldAfterSave = holdAfterSave;
        internal readonly bool LoseAcknowledgement = loseAcknowledgement;
        internal readonly bool Unreadable = unreadable;
        internal readonly bool HoldSuccessfulReturn = holdSuccessfulReturn;
        internal int AttemptCount;
        internal int Durable;
        internal int ArmedReadFailure;
        internal int ReadFailureCount;
        internal int PostCommitSaveCount;
        internal int FailQuestWrites;
        internal int Attempts => Volatile.Read(ref AttemptCount);
        internal int FailedReads => Volatile.Read(ref ReadFailureCount);
        internal int PostCommitSaveCalls => Volatile.Read(ref PostCommitSaveCount);
        internal Func<int, Guid?>? ObserveOperation { get; set; }
        internal CharacterQuestRewardRequest? Request { get; set; }
        internal Action? CancelOperation { get; set; }
        internal ConcurrentQueue<Guid> Operations { get; } = new();
        internal TaskCompletionSource<bool> Saved { get; } = Signal();
        internal TaskCompletionSource<bool> SaveRelease { get; } = Signal();
        internal TaskCompletionSource<bool> Committed { get; } = Signal();
        internal TaskCompletionSource<bool> AcknowledgementRelease { get; } = Signal();
        internal TaskCompletionSource<bool> ReadFailed { get; } = Signal();
        internal TaskCompletionSource<bool> QuestWriteEntered { get; } = Signal();
        internal TaskCompletionSource<bool> QuestWriteRelease { get; } = Signal();
        internal TaskCompletionSource<bool> CoreSnapshotSaved { get; } = Signal();

        internal void CancelCurrentOperation() => (CancelOperation
            ?? throw new InvalidOperationException("The reward transaction has not started."))();

        internal void ReleaseAll()
        {
            SaveRelease.TrySetResult(true);
            AcknowledgementRelease.TrySetResult(true);
            QuestWriteRelease.TrySetResult(true);
        }
    }

    private sealed record OwnedCharacterClient(WorldClient Client, ScenarioConnection Connection, ulong Guid) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Client.DisposeAsync();
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static CancellationTokenSource TestDeadline() => new(TimeSpan.FromSeconds(45));
}

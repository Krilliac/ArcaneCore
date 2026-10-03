using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Quests;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.MockClient.Tests;

/// <summary>A held real reward operation must leave the simulation and another authenticated player responsive.</summary>
public sealed class QuestSettlementResponsivenessTests(ITestOutputHelper output)
{
    private const int RewardQuestField = 0x00C9;
    private const int RewardProgressField = 0x00CA;
    private const int MoneyField = 0x0498;
    private static readonly TimeSpan ResponseBudget = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan IntentionalPause = TimeSpan.FromMilliseconds(650);

    [Fact]
    public async Task SettlementCapacity_EightHeldRealTurnInsLeaveTheNinthPlayerActiveAndAbleToRetry()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        CancellationToken token = deadline.Token;
        var hold = new CapacityHold();
        var clients = new List<OwnedClient>();
        var rewardReaders = new List<Task<MockQuestComplete>>();
        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(services =>
        {
            services.AddScoped<ICreatureDataStore>(provider => new CapacityCreatureSource(
                new EfCreatureDataStore(provider.GetRequiredService<WorldDbContext>())));
            services.AddScoped<ICharacterQuestRewardStore>(provider => new CapacityRewardStore(
                new EfCharacterQuestRewardStore(provider.GetRequiredService<CharacterDbContext>()), hold));
        }, token);
        try
        {
            // Every journal is completed before holding any settlement, so the operation's
            // real five-second budget is reserved for the capacity observation and release.
            for (int index = 0; index <= QuestNpcFeature.MaxConcurrentSettlements; index++)
            {
                OwnedClient client = await CreateClientAsync(server, $"CAPREWARD{index}", $"Caphero{(char)('a' + index)}", token);
                clients.Add(client);
                await MockScenarios.AcceptRewardQuestAsync(server, client.Connection, client.Guid, token);
                await MockScenarios.KillRewardTargetAsync(server, client.Connection, client.Guid, CapacityTargetGuid(index, 0), 1, token);
                await MockScenarios.KillRewardTargetAsync(server, client.Connection, client.Guid, CapacityTargetGuid(index, 1), 2, token);
                await MockScenarios.ValidatePersistedRewardAsync(server, client.Guid, rewarded: false, count: 2, token);
                if (index < QuestNpcFeature.MaxConcurrentSettlements)
                {
                    // This connection now owns a continuous reader while later players
                    // prepare their journals and broadcast normal combat/visibility packets.
                    rewardReaders.Add(ReadCapacityRewardAsync(client.Connection, client.Guid, hold, token));
                }
            }

            QuestNpcFeature feature = server.Services.GetRequiredService<QuestNpcFeature>();
            foreach (OwnedClient client in clients.Take(QuestNpcFeature.MaxConcurrentSettlements))
            {
                await MockScenarios.ChooseRewardAsync(client.Connection, 1, token);
            }

            await hold.EightEntered.Task.WaitAsync(token);
            Assert.Equal(QuestNpcFeature.MaxConcurrentSettlements, feature.PendingSettlementCount);
            Guid[] operationIds = clients.Take(QuestNpcFeature.MaxConcurrentSettlements).Select(client =>
                feature.PendingOperationId(checked((int)client.Guid)) ?? throw new InvalidOperationException("A held capacity turn-in lost its operation."))
                .ToArray();
            Assert.Equal(QuestNpcFeature.MaxConcurrentSettlements, operationIds.Distinct().Count());
            OwnedClient ninth = clients[^1];
            await MockScenarios.ChooseRewardAsync(ninth.Connection, 1, token);
            var responseSamples = new List<double>();
            await MeasureAsync(async responseToken =>
            {
                await ninth.Connection.SendAsync(WorldOpcode.CmsgQuestgiverStatusQuery, ScenarioWire.Guid(SyntheticArcaneServer.NpcGuid), responseToken);
                MockQuestgiverStatus status = ScenarioWire.QuestgiverStatus(await ninth.Connection.ReadUntilAsync(WorldOpcode.SmsgQuestgiverStatus, responseToken));
                Assert.Equal(SyntheticArcaneServer.NpcGuid, status.Guid);
            }, responseSamples, token);
            Assert.Equal(QuestNpcFeature.MaxConcurrentSettlements, feature.PendingSettlementCount);
            Assert.Equal(QuestNpcFeature.MaxConcurrentSettlements, hold.Attempts);
            Assert.Null(feature.PendingOperationId(checked((int)ninth.Guid)));
            Assert.False(server.Services.GetRequiredService<CharacterSaveQueue>().IsHeld(checked((int)ninth.Guid)));
            Assert.False(server.Services.GetRequiredService<CharacterSaveQueue>().IsQuarantined(checked((int)ninth.Guid)));
            Assert.False(await server.World.InvokeAsync(() => server.World.FindOnlinePlayer(new ObjectGuid(ninth.Guid))!.IsQuestSettlementPending).WaitAsync(token));
            await AssertUnsettledAsync(server, ninth.Guid, token);
            output.WriteLine(JsonSerializer.Serialize(new
            {
                capacity = QuestNpcFeature.MaxConcurrentSettlements,
                heldSettlements = feature.PendingSettlementCount,
                distinctOperationIds = operationIds.Length,
                ninthPlayerMapResponseMilliseconds = responseSamples.Single(),
                storeAttemptsBeforeRelease = hold.Attempts,
            }));

            hold.Release();
            for (int index = 0; index < QuestNpcFeature.MaxConcurrentSettlements; index++)
            {
                OwnedClient client = clients[index];
                await feature.WaitForSettlementAsync(checked((int)client.Guid), token);
                ValidateCapacityReward(await rewardReaders[index].WaitAsync(token));
                await MockScenarios.ValidatePersistedRewardAsync(server, client.Guid, rewarded: true, count: 2, token);
            }

            Assert.Equal(0, feature.PendingSettlementCount);
            Task<MockQuestComplete> ninthReader = ReadCapacityRewardAsync(ninth.Connection, ninth.Guid, hold, token);
            rewardReaders.Add(ninthReader);
            await MockScenarios.ChooseRewardAsync(ninth.Connection, 1, token);
            ValidateCapacityReward(await ninthReader.WaitAsync(token));
            await MockScenarios.ValidatePersistedRewardAsync(server, ninth.Guid, rewarded: true, count: 2, token);
            Assert.Equal(QuestNpcFeature.MaxConcurrentSettlements + 1, hold.Attempts);
            Assert.Equal(0, feature.PendingSettlementCount);
        }
        finally
        {
            hold.Release();
            deadline.Cancel();
            foreach (OwnedClient client in clients)
            {
                await client.DisposeAsync();
            }

            try
            {
                await Task.WhenAll(rewardReaders).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                output.WriteLine($"Capacity reader cleanup: {exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    private static void ValidateCapacityReward(MockQuestComplete complete)
    {
        Assert.Equal(SyntheticArcaneServer.RewardQuestId, complete.QuestId);
        Assert.Equal(3u, complete.Type);
        Assert.Equal(0u, complete.Experience);
        Assert.Equal(SyntheticArcaneServer.RewardMoney, complete.Money);
        Assert.Equal(new MockQuestCompletedReward(SyntheticArcaneServer.FixedRewardItem, 1), Assert.Single(complete.Rewards));
    }

    private static async Task<MockQuestComplete> ReadCapacityRewardAsync(ScenarioConnection connection, ulong guid,
        CapacityHold hold, CancellationToken token)
    {
        // One deadline-bound reader continuously consumes ambient map traffic for this
        // connection; packet count is not a stand-in for elapsed settlement time. A held
        // connection is legitimately silent for as long as the other players take to prepare,
        // which under load exceeds a single read's five-second deadline (that timeout closes the
        // connection), so wait for traffic first and bound only the frame itself.
        while (true)
        {
            await connection.WaitForTrafficAsync(token);
            WorldFrame frame = await connection.ReadAsync(token);
            if (!hold.IsReleased)
            {
                Assert.NotEqual((ushort)WorldOpcode.SmsgItemPushResult, frame.Opcode);
                Assert.NotEqual((ushort)WorldOpcode.SmsgQuestgiverQuestComplete, frame.Opcode);
                Assert.Equal(0u, connection.FieldsOf(guid).GetValueOrDefault(MoneyField));
            }

            if (frame.Opcode == (ushort)WorldOpcode.SmsgQuestgiverQuestComplete)
            {
                return ScenarioWire.QuestComplete(frame.Payload);
            }
        }
    }

    private static ulong CapacityTargetGuid(int character, int target)
        => ((ulong)0xF130 << 48) | ((ulong)SyntheticArcaneServer.TargetEntry << 24) | (910000u + (uint)(character * 2 + target));

    [Fact]
    public async Task BlockedRewardStore_WorldTicksCommandsAndAnotherPlayersQuestPacketsProgressBeforeRelease()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        CancellationToken token = deadline.Token;
        var hold = new RewardStoreHold();
        var probe = new MapTickProbe();
        var invokeSamples = new List<double>();
        var npcSamples = new List<double>();
        double? acceptMilliseconds = null;
        double? heldMilliseconds = null;
        TickMeasurement? tickMeasurement = null;
        Task<MockQuestComplete>? completion = null;

        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(services =>
            services.AddScoped<ICharacterQuestRewardStore>(provider => new HeldRewardStore(
                new EfCharacterQuestRewardStore(provider.GetRequiredService<CharacterDbContext>()), hold)), token);
        try
        {
            await server.World.InvokeAsync(() =>
            {
                server.World.GetMap(0).AddUpdater(probe);
                return true;
            }).WaitAsync(token);
            await using OwnedClient slow = await CreateClientAsync(server, "SLOWREWARD", "Slowhero", token);
            await MockScenarios.AcceptRewardQuestAsync(server, slow.Connection, slow.Guid, token);
            await MockScenarios.KillRewardTargetAsync(server, slow.Connection, slow.Guid, SyntheticArcaneServer.FirstTargetGuid, 1, token);
            await MockScenarios.KillRewardTargetAsync(server, slow.Connection, slow.Guid, SyntheticArcaneServer.SecondTargetGuid, 2, token);
            await server.FlushCharacterAsync(checked((int)slow.Guid), token);
            await using OwnedClient other = await CreateClientAsync(server, "OTHERREWARD", "Otherhero", token);
            // Warm the actual map handler before recording latency; realm, login and JIT
            // setup are outside the held-operation observation window.
            await QueryNpcAsync(other.Connection, token);

            QuestNpcFeature feature = server.Services.GetRequiredService<QuestNpcFeature>();
            await MockScenarios.ChooseRewardAsync(slow.Connection, 1, token);
            CharacterQuestRewardRequest request = await hold.Entered.Task.WaitAsync(token);
            completion = ReadRewardAfterReleaseAsync(slow.Connection, slow.Guid, request, hold, token);
            probe.Begin();
            Assert.Equal(1, feature.PendingSettlementCount);
            Assert.NotNull(feature.PendingOperationId(checked((int)slow.Guid)));

            // These packets would abandon the completed quest or select it again if they
            // could mutate the held Player. The store remains blocked during the checks.
            await slow.Connection.SendAsync(WorldOpcode.CmsgQuestlogRemoveQuest, [1], token);
            await MockScenarios.ChooseRewardAsync(slow.Connection, 0, token);

            while (Stopwatch.GetElapsedTime(hold.EnteredTimestamp) < IntentionalPause)
            {
                await MeasureAsync(async responseToken =>
                {
                    Assert.Equal(2, await server.World.InvokeAsync(() => server.World.OnlinePlayerCount).WaitAsync(responseToken));
                }, invokeSamples, token);
                await MeasureAsync(responseToken => QueryNpcAsync(other.Connection, responseToken), npcSamples, token);
                await AssertUnsettledAsync(server, slow.Guid, token);
                Assert.False(completion.IsCompleted, "Reward completion reached the client while its real store operation was held.");
                Assert.Equal(1, hold.Attempts);
                await Task.Delay(20, token);
            }

            var acceptSamples = new List<double>();
            await MeasureAsync(async responseToken =>
            {
                await other.Connection.SendAsync(WorldOpcode.CmsgQuestgiverAcceptQuest,
                    ScenarioWire.GuidQuest(SyntheticArcaneServer.NpcGuid, SyntheticArcaneServer.NpcQuestId), responseToken);
                Assert.Empty(await other.Connection.ReadUntilAsync(WorldOpcode.SmsgGossipComplete, responseToken));
                await other.Connection.ReadUntilFieldAsync(other.Guid, RewardQuestField, SyntheticArcaneServer.NpcQuestId, responseToken);
            }, acceptSamples, token);
            acceptMilliseconds = acceptSamples.Single();
            Assert.Equal(SyntheticArcaneServer.NpcQuestId,
                other.Connection.FieldsOf(other.Guid).GetValueOrDefault(RewardQuestField));
            await AssertUnsettledAsync(server, slow.Guid, token);
            Assert.False(completion.IsCompleted, "Another player's actual quest acceptance caused a premature held reward.");
            Assert.Equal(1, hold.Attempts);
            Assert.Equal(1, feature.PendingSettlementCount);
            heldMilliseconds = Stopwatch.GetElapsedTime(hold.EnteredTimestamp).TotalMilliseconds;
            tickMeasurement = probe.End();
            Assert.True(heldMilliseconds >= IntentionalPause.TotalMilliseconds, "Store hold ended before the intentional pause.");
            Assert.True(tickMeasurement.TickCount >= 10, $"Only {tickMeasurement.TickCount} actual map ticks ran during the held reward.");
            Assert.True(tickMeasurement.MaximumGapMilliseconds < ResponseBudget.TotalMilliseconds,
                $"Actual map tick gap {tickMeasurement.MaximumGapMilliseconds:F2} ms exceeded {ResponseBudget.TotalMilliseconds} ms.");

            hold.Release();
            MockQuestComplete rewarded = await completion.WaitAsync(token);
            Assert.Equal(SyntheticArcaneServer.RewardQuestId, rewarded.QuestId);
            Assert.Equal(3u, rewarded.Type);
            Assert.Equal(0u, rewarded.Experience);
            Assert.Equal(SyntheticArcaneServer.RewardMoney, rewarded.Money);
            Assert.Equal(new MockQuestCompletedReward(SyntheticArcaneServer.FixedRewardItem, 1), Assert.Single(rewarded.Rewards));
            await slow.Connection.ReadUntilFieldAsync(slow.Guid, MoneyField, SyntheticArcaneServer.RewardMoney, token);
            await feature.WaitForSettlementAsync(checked((int)slow.Guid), token);
            Assert.Equal(0, feature.PendingSettlementCount);
            Assert.Null(feature.PendingOperationId(checked((int)slow.Guid)));
            MockScenarios.ValidateRewardObservation(await MockScenarios.ObserveRewardAsync(server, slow.Guid, token), rewarded: true);
            await MockScenarios.ValidatePersistedRewardAsync(server, slow.Guid, rewarded: true, count: 2, token);
            await MockScenarios.ChooseRewardAsync(slow.Connection, 1, token);
            await slow.Connection.AssertNoRewardUntilPongAsync(0x90000371, token);
            Assert.Equal(1, hold.Attempts);
            await MockScenarios.WaitForCombatExitAsync(server, slow.Connection, slow.Guid, token);
            await slow.Connection.LogoutAsync(token);
            await slow.Client.DisposeAsync();
            await using OwnedClient relog = await ReconnectAsync(server, "SLOWREWARD", slow.Guid, token);
            Assert.Equal(0u, relog.Connection.FieldsOf(slow.Guid).GetValueOrDefault(RewardQuestField));
            Assert.Equal(0u, relog.Connection.FieldsOf(slow.Guid).GetValueOrDefault(RewardProgressField));
            Assert.Equal(SyntheticArcaneServer.RewardMoney, relog.Connection.FieldsOf(slow.Guid).GetValueOrDefault(MoneyField));
            MockScenarios.ValidateRewardObservation(await MockScenarios.ObserveRewardAsync(server, slow.Guid, token), rewarded: true);
            await MockScenarios.ValidatePersistedRewardAsync(server, slow.Guid, rewarded: true, count: 2, token);
        }
        finally
        {
            heldMilliseconds ??= hold.EnteredTimestamp == 0 ? null : Stopwatch.GetElapsedTime(hold.EnteredTimestamp).TotalMilliseconds;
            tickMeasurement ??= probe.End();
            hold.Release();
            if (completion is not null)
            {
                try
                {
                    await completion.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    output.WriteLine($"Reward reader cleanup: {exception.GetType().Name}: {exception.Message}");
                }
            }

            output.WriteLine(JsonSerializer.Serialize(new
            {
                responseBudgetMilliseconds = ResponseBudget.TotalMilliseconds,
                intentionalPauseMilliseconds = IntentionalPause.TotalMilliseconds,
                heldMilliseconds,
                actualMapTicks = tickMeasurement.TickCount,
                tickObservationMilliseconds = tickMeasurement.DurationMilliseconds,
                maximumTickGapMilliseconds = tickMeasurement.MaximumGapMilliseconds,
                p95TickGapMilliseconds = tickMeasurement.P95GapMilliseconds,
                invoke = Samples(invokeSamples),
                npcStatus = Samples(npcSamples),
                acceptMilliseconds,
                rewardStoreAttempts = hold.Attempts,
            }));
        }
    }

    private static async Task MeasureAsync(Func<CancellationToken, Task> action, List<double> samples, CancellationToken token)
    {
        using var response = CancellationTokenSource.CreateLinkedTokenSource(token);
        response.CancelAfter(ResponseBudget);
        long start = Stopwatch.GetTimestamp();
        try
        {
            await action(response.Token);
        }
        finally
        {
            samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        Assert.True(samples[^1] < ResponseBudget.TotalMilliseconds,
            $"Actual map response latency {samples[^1]:F2} ms exceeded {ResponseBudget.TotalMilliseconds} ms.");
    }

    private static async Task QueryNpcAsync(ScenarioConnection connection, CancellationToken token)
    {
        await connection.SendAsync(WorldOpcode.CmsgQuestgiverStatusQuery, ScenarioWire.Guid(SyntheticArcaneServer.NpcGuid), token);
        MockQuestgiverStatus status = ScenarioWire.QuestgiverStatus(await connection.ReadUntilAsync(WorldOpcode.SmsgQuestgiverStatus, token));
        Assert.Equal(SyntheticArcaneServer.NpcGuid, status.Guid);
        Assert.Equal(5u, status.Status);
    }

    private static async Task AssertUnsettledAsync(SyntheticArcaneServer server, ulong guid, CancellationToken token)
    {
        RewardObservation live = await MockScenarios.ObserveRewardAsync(server, guid, token);
        MockScenarios.ValidateRewardObservation(live, rewarded: false);
        Assert.Equal(0x01000002u, live.QuestProgress);
        // Deliberately read raw stores here: the fixture flush correctly waits for the
        // pending settlement and is used only after the held operation is released.
        await using AsyncServiceScope scope = server.Services.CreateAsyncScope();
        int characterId = checked((int)guid);
        CharacterRecord? character = await scope.ServiceProvider.GetRequiredService<ICharacterStore>().GetByIdAsync(characterId, token);
        Assert.NotNull(character);
        Assert.Equal(0u, character.Money);
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<IItemStore>().GetInventoryAsync(characterId, token));
        CharacterQuestData quests = await scope.ServiceProvider.GetRequiredService<ICharacterQuestStore>().LoadAsync(characterId, token);
        CharacterQuestStatus row = Assert.Single(quests.Quests, quest => quest.Quest == SyntheticArcaneServer.RewardQuestId);
        Assert.Equal((byte)1, row.Status);
        Assert.Equal(2u, row.MobCount1);
        Assert.False(row.Rewarded);
        Assert.Equal(0u, row.RewardChoice);
    }

    private static async Task<MockQuestComplete> ReadRewardAfterReleaseAsync(ScenarioConnection connection, ulong guid,
        CharacterQuestRewardRequest request, RewardStoreHold hold, CancellationToken token)
    {
        for (int index = 0; index < 128; index++)
        {
            WorldFrame frame = await connection.ReadAsync(token);
            if (!hold.IsReleased)
            {
                Assert.NotEqual((ushort)WorldOpcode.SmsgItemPushResult, frame.Opcode);
                Assert.NotEqual((ushort)WorldOpcode.SmsgQuestgiverQuestComplete, frame.Opcode);
                Assert.Equal(0u, connection.FieldsOf(guid).GetValueOrDefault(MoneyField));
                foreach (InventoryItemData item in request.After.Inventory!.Items)
                {
                    ulong itemGuid = ((ulong)0x4000 << 48) | item.Item.Guid;
                    Assert.Equal(0u, connection.FieldsOf(itemGuid).GetValueOrDefault(0x0003));
                }
            }

            if (frame.Opcode == (ushort)WorldOpcode.SmsgQuestgiverQuestComplete)
            {
                return ScenarioWire.QuestComplete(frame.Payload);
            }
        }

        throw new MockProtocolException("Held reward completion did not arrive within 128 frames.");
    }

    private static async Task<OwnedClient> CreateClientAsync(SyntheticArcaneServer server, string account, string name, CancellationToken token)
    {
        await server.AddAccountAsync(account, "PASSWORD", token);
        WorldClient client = await AuthenticateAsync(server, account, token);
        try
        {
            var connection = new ScenarioConnection(client);
            await connection.CreateCharacterAsync(name, token);
            ulong guid = Assert.Single(await connection.EnumerateAsync(token)).Guid;
            await MockScenarios.SeedJournalAsync(server, guid, token);
            await connection.LoginAsync(guid, token);
            return new OwnedClient(client, connection, guid);
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    private static async Task<OwnedClient> ReconnectAsync(SyntheticArcaneServer server, string account, ulong guid, CancellationToken token)
    {
        WorldClient client = await AuthenticateAsync(server, account, token);
        try
        {
            var connection = new ScenarioConnection(client);
            Assert.Equal(guid, Assert.Single(await connection.EnumerateAsync(token)).Guid);
            await connection.LoginAsync(guid, token);
            return new OwnedClient(client, connection, guid);
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    private static async Task<WorldClient> AuthenticateAsync(SyntheticArcaneServer server, string account, CancellationToken token)
    {
        LogonResult logon = await LogonClient.AuthenticateAsync(server.RealmEndpoint, account, "PASSWORD", token);
        WorldClient client = await WorldClient.ConnectAsync(Assert.Single(logon.Realms).GetLoopbackEndpoint(), token);
        try
        {
            Assert.Equal((byte)0x0C, await client.AuthenticateAsync(account, logon.SessionKey, token));
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    private static object Samples(IReadOnlyList<double> samples) => new
    {
        count = samples.Count,
        maximumMilliseconds = samples.Count == 0 ? 0 : samples.Max(),
        p95Milliseconds = Percentile95(samples),
        milliseconds = samples.ToArray(),
    };

    private static double Percentile95(IReadOnlyList<double> samples)
        => samples.Count == 0 ? 0 : samples.Order().ElementAt(Math.Clamp((int)Math.Ceiling(samples.Count * 0.95) - 1, 0, samples.Count - 1));

    private sealed class HeldRewardStore(ICharacterQuestRewardStore inner, RewardStoreHold hold) : ICharacterQuestRewardStore
    {
        public async Task<QuestRewardCommitResult> CommitAsync(CharacterQuestRewardRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref hold.AttemptCount);
            hold.EnteredTimestamp = Stopwatch.GetTimestamp();
            hold.Entered.TrySetResult(request);
            // This owned collaborator suspends before the real EF transaction. It never
            // posts or awaits the world thread, and the scope remains alive until release.
            await hold.Released.Task.WaitAsync(cancellationToken);
            return await inner.CommitAsync(request, cancellationToken);
        }
    }

    private sealed class RewardStoreHold
    {
        internal int AttemptCount;
        internal long EnteredTimestamp;
        internal int Attempts => Volatile.Read(ref AttemptCount);
        internal bool IsReleased => Released.Task.IsCompleted;
        internal TaskCompletionSource<CharacterQuestRewardRequest> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Release() => Released.TrySetResult();
    }

    /// <summary>Additional owned spawns retain the real database templates and normal creature lifecycle.</summary>
    private sealed class CapacityCreatureSource(ICreatureDataStore inner) : ICreatureDataStore
    {
        public async Task<CreatureContent> LoadAsync(CancellationToken cancellationToken = default)
        {
            CreatureContent content = await inner.LoadAsync(cancellationToken);
            CreatureSpawn[] original = content.MapsWithSpawns.SelectMany(content.GetSpawns).ToArray();
            CreatureSpawn[] targets = Enumerable.Range(0, (QuestNpcFeature.MaxConcurrentSettlements + 1) * 2).Select(index => new CreatureSpawn
            {
                Guid = 910000u + (uint)index,
                Entry = SyntheticArcaneServer.TargetEntry,
                MapId = 0,
                X = -8949.95f + (index % 2 == 0 ? 0.5f : -0.5f),
                Y = -132.493f,
                Z = 83.5312f,
                SpawnTimeMinSeconds = 3600,
                SpawnTimeMaxSeconds = 3600,
            }).ToArray();
            return new CreatureContent([
                content.FindTemplate(SyntheticArcaneServer.NpcEntry) ?? throw new InvalidOperationException("Synthetic guide template is missing."),
                content.FindTemplate(SyntheticArcaneServer.TargetEntry) ?? throw new InvalidOperationException("Synthetic target template is missing."),
            ], original.Concat(targets),
                original.SelectMany(spawn => content.GetWaypoints(spawn.Guid).Select(point => (spawn.Guid, point))),
                [content.FindModel(900012) ?? throw new InvalidOperationException("Synthetic target model is missing.")],
                original.Select(spawn => content.FindAddon(spawn.Guid)).OfType<CreatureAddon>());
        }
    }

    private sealed class CapacityHold
    {
        private readonly ConcurrentDictionary<int, CharacterQuestRewardRequest> _entered = new();
        private int _attempts;
        internal int Attempts => Volatile.Read(ref _attempts);
        internal bool IsReleased => Released.Task.IsCompleted;
        internal TaskCompletionSource EightEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal async Task WaitAsync(CharacterQuestRewardRequest request, CancellationToken token)
        {
            Interlocked.Increment(ref _attempts);
            _entered.TryAdd(request.Before.Id, request);
            if (_entered.Count == QuestNpcFeature.MaxConcurrentSettlements)
            {
                EightEntered.TrySetResult();
            }

            await Released.Task.WaitAsync(token);
        }

        internal void Release() => Released.TrySetResult();
    }

    private sealed class CapacityRewardStore(ICharacterQuestRewardStore inner, CapacityHold hold) : ICharacterQuestRewardStore
    {
        public async Task<QuestRewardCommitResult> CommitAsync(CharacterQuestRewardRequest request, CancellationToken cancellationToken = default)
        {
            await hold.WaitAsync(request, cancellationToken);
            return await inner.CommitAsync(request, cancellationToken);
        }
    }

    private sealed class MapTickProbe : IMapUpdater
    {
        private readonly object _gate = new();
        private readonly List<double> _gaps = [];
        private bool _measuring;
        private long _lastTick;
        private long _started;

        public void Update(Map map, uint diffMs)
        {
            lock (_gate)
            {
                if (_measuring)
                {
                    _gaps.Add(Stopwatch.GetElapsedTime(_lastTick).TotalMilliseconds);
                    _lastTick = Stopwatch.GetTimestamp();
                }
            }
        }

        public void OnPlayerRemoved(Map map, Player player)
        {
        }

        internal void Begin()
        {
            lock (_gate)
            {
                _gaps.Clear();
                _started = _lastTick = Stopwatch.GetTimestamp();
                _measuring = true;
            }
        }

        internal TickMeasurement End()
        {
            lock (_gate)
            {
                if (!_measuring)
                {
                    return new TickMeasurement(0, 0, 0, 0);
                }

                _measuring = false;
                int tickCount = _gaps.Count;
                _gaps.Add(Stopwatch.GetElapsedTime(_lastTick).TotalMilliseconds);
                return new TickMeasurement(tickCount, Stopwatch.GetElapsedTime(_started).TotalMilliseconds,
                    _gaps.Max(), Percentile95(_gaps));
            }
        }
    }

    private sealed record TickMeasurement(int TickCount, double DurationMilliseconds,
        double MaximumGapMilliseconds, double P95GapMilliseconds);

    private sealed record OwnedClient(WorldClient Client, ScenarioConnection Connection, ulong Guid) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Client.DisposeAsync();
    }
}

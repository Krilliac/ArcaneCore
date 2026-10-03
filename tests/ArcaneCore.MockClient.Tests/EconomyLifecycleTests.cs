using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Economy;
using ArcaneCore.Data.Quests;
using ArcaneCore.Data.Stores;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

/// <summary>Actual mail opcodes, owned sessions and SQLite commits exercise the shared settlement lifecycle.</summary>
public sealed class EconomyLifecycleTests
{
    private const string AccountName = "ECONLIFECYCLE";
    private const string Password = "PASSWORD";
    private const uint MailId = 1;
    private const uint MailMoney = 100;
    private static readonly ObjectGuid Mailbox = ObjectGuid.WithEntry(HighGuid.GameObject, 900080, 1);

    [Fact]
    public async Task HeldMailCommit_DisconnectAndFreshLoginWaitForEconomyBeforeReadsAndCoreSaves()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        CancellationToken token = deadline.Token;
        var control = new Control(holdCommit: true);
        await using SyntheticArcaneServer server = await StartAsync(control, token, cashQuest: true);
        await using OwnedClient original = await CreateClientAsync(server, token);
        EconomyFeature economy = server.Services.GetRequiredService<EconomyFeature>();
        CharacterSaveQueue saves = server.Services.GetRequiredService<CharacterSaveQueue>();
        int id = checked((int)original.Guid);
        Player oldPlayer = await PlayerAsync(server, original.Guid, token);
        QuestNpcFeature quests = server.Services.GetRequiredService<QuestNpcFeature>();
        await PrepareCashQuestAsync(server, original, oldPlayer, token);
        Task<MockLogin>? login = null;
        try
        {
            await TakeMoneyAsync(original.Connection, token);
            EconomyCommitRequest request = await control.CommitEntered.Task.WaitAsync(token);
            Assert.Equal(id, Assert.Single(request.Participants).Before.Id);
            Assert.Equal(0u, request.Participants[0].Before.Money);
            Assert.Equal(MailMoney, request.Participants[0].After.Money);
            Assert.Equal(1, economy.Settlements.PendingCount);
            Assert.True(saves.IsHeld(id));
            Assert.True(saves.IsQuarantined(id));
            await AssertStoredAsync(server, id, money: 0, mailMoney: MailMoney, token);

            await original.Client.DisposeAsync();
            await WaitUntilOfflineAsync(server, token);
            await using WorldClient replacementClient = await AuthenticateAsync(server, token);
            var next = new ScenarioConnection(replacementClient);
            Assert.Equal(original.Guid, Assert.Single(await next.EnumerateAsync(token)).Guid);
            Volatile.Write(ref control.ObserveLogin, 1);
            login = next.LoginAsync(original.Guid, token);
            await control.OwnershipRead.Task.WaitAsync(token);
            // The first row establishes ownership; the authoritative reread and loading
            // hooks must remain behind the actual economy operation, not just quest rewards.
            await Task.Delay(150, token);
            Assert.False(login.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref control.LoginReads));
            Assert.Equal(0, server.World.OnlinePlayerCount);
            Assert.True(saves.IsHeld(id));

            control.Release.TrySetResult(true);
            await economy.WaitForSettlementAsync(id, token);
            await login.WaitAsync(token);
            Assert.Equal(2, Volatile.Read(ref control.LoginReads));
            Player replacement = await PlayerAsync(server, original.Guid, token);
            Assert.NotSame(oldPlayer, replacement);
            Assert.False(saves.IsHeld(id));
            Assert.False(saves.IsQuarantined(id));
            await server.World.InvokeAsync(() =>
            {
                Assert.Equal(MailMoney, replacement.Money);
                Assert.Equal(0u, oldPlayer.Money);
                Assert.Equal(QuestStatus.Complete, quests.Services.StateOf(replacement)!.Quests.GetStatus(SyntheticArcaneServer.RewardQuestId));
                Assert.True(quests.Services.TryPrepareReward(replacement, new ObjectGuid(SyntheticArcaneServer.NpcGuid),
                    SyntheticArcaneServer.RewardQuestId, 0, out _));
                // Late old-session callbacks cannot remove or save the replacement.
                server.World.SavePlayer(oldPlayer);
                server.World.RemovePlayer(oldPlayer);
                Assert.Same(replacement, server.World.FindOnlinePlayer(new ObjectGuid(original.Guid)));
                server.World.SavePlayer(replacement);
                return true;
            }).WaitAsync(token);
            await saves.FlushCharacterAsync(id, token);
            await quests.Persistence.FlushCharacterAsync(id).WaitAsync(token);
            await AssertStoredAsync(server, id, money: MailMoney, mailMoney: 0, token);
            await using AsyncServiceScope scope = server.Services.CreateAsyncScope();
            CharacterQuestStatus stored = Assert.Single((await new EfCharacterQuestStore(
                scope.ServiceProvider.GetRequiredService<CharacterDbContext>()).LoadAsync(id, token)).Quests,
                row => row.Quest == SyntheticArcaneServer.RewardQuestId);
            Assert.Equal((byte)QuestStatus.Complete, stored.Status);
            Assert.False(stored.Rewarded);
        }
        finally
        {
            control.Release.TrySetResult(true);
            if (login is not null)
            {
                try { await login.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException) { }
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoneyOnlyMailSettlement_RefreshesCashQuestOnlyAfterCommittedPublication(bool refuseCommit)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        CancellationToken token = deadline.Token;
        var control = new Control(holdCommit: false, refuseCommit);
        await using SyntheticArcaneServer server = await StartAsync(control, token, cashQuest: true);
        await using OwnedClient owned = await CreateClientAsync(server, token);
        Player player = await PlayerAsync(server, owned.Guid, token);
        QuestNpcFeature quests = server.Services.GetRequiredService<QuestNpcFeature>();
        EconomyFeature economy = server.Services.GetRequiredService<EconomyFeature>();
        int id = checked((int)owned.Guid);

        await PrepareCashQuestAsync(server, owned, player, token);

        await TakeMoneyAsync(owned.Connection, token);
        AssertMailResult(await owned.Connection.ReadUntilAsync(WorldOpcode.SmsgSendMailResult, token), refuseCommit);
        await economy.WaitForSettlementAsync(id, token);
        await server.World.InvokeAsync(() =>
        {
            Assert.False(player.IsQuestSettlementPending);
            Assert.Equal(refuseCommit ? 0u : MailMoney, player.Money);
            QuestStatusData data = quests.Services.StateOf(player)!.Quests.Get(SyntheticArcaneServer.RewardQuestId)!;
            Assert.Equal(2u, data.CreatureOrGOCount[0]);
            Assert.Equal(refuseCommit ? QuestStatus.Incomplete : QuestStatus.Complete, data.Status);
            Assert.Equal(!refuseCommit, quests.Services.TryPrepareReward(player, new ObjectGuid(SyntheticArcaneServer.NpcGuid),
                SyntheticArcaneServer.RewardQuestId, 0, out _));
            return true;
        }).WaitAsync(token);
        await quests.Persistence.FlushCharacterAsync(id).WaitAsync(token);
        await AssertStoredAsync(server, id, refuseCommit ? 0u : MailMoney, refuseCommit ? MailMoney : 0u, token);
        await using AsyncServiceScope scope = server.Services.CreateAsyncScope();
        CharacterQuestStatus stored = Assert.Single((await new EfCharacterQuestStore(
            scope.ServiceProvider.GetRequiredService<CharacterDbContext>()).LoadAsync(id, token)).Quests,
            row => row.Quest == SyntheticArcaneServer.RewardQuestId);
        Assert.Equal((byte)(refuseCommit ? QuestStatus.Incomplete : QuestStatus.Complete), stored.Status);
        Assert.False(stored.Rewarded);
    }

    private static Task<SyntheticArcaneServer> StartAsync(Control control, CancellationToken token, bool cashQuest = false)
        => SyntheticArcaneServer.StartAsync(services =>
        {
            services.AddSingleton<IMailboxAccess>(new EconomyMailSendParityTests.AnyMailbox());
            services.AddScoped<IEconomyStore>(provider => new ControlledEconomyStore(
                new EfEconomyStore(provider.GetRequiredService<CharacterDbContext>()), control));
            services.AddScoped<ICharacterStore>(provider => new ObservedCharacterStore(
                new EfCharacterStore(provider.GetRequiredService<CharacterDbContext>()), control));
            if (cashQuest)
            {
                services.AddScoped<IQuestContentStore>(provider => new CashQuestContent(
                    new EfQuestContentStore(provider.GetRequiredService<WorldDbContext>())));
            }
        }, token);

    private static async Task PrepareCashQuestAsync(SyntheticArcaneServer server, OwnedClient owned, Player player, CancellationToken token)
    {
        QuestNpcFeature quests = server.Services.GetRequiredService<QuestNpcFeature>();
        await owned.Connection.SendAsync(WorldOpcode.CmsgQuestgiverAcceptQuest,
            ScenarioWire.GuidQuest(SyntheticArcaneServer.NpcGuid, SyntheticArcaneServer.RewardQuestId), token);
        Assert.Empty(await owned.Connection.ReadUntilAsync(WorldOpcode.SmsgGossipComplete, token));
        await server.World.InvokeAsync(() =>
        {
            Assert.Equal(QuestStatus.Incomplete, quests.Services.StateOf(player)!.Quests.GetStatus(SyntheticArcaneServer.RewardQuestId));
            // Real authoritative deaths invoke the attached quest objective adapter.
            Map map = Assert.IsType<Map>(player.Map);
            foreach (ulong guid in new[] { SyntheticArcaneServer.FirstTargetGuid, SyntheticArcaneServer.SecondTargetGuid })
            {
                Unit target = Assert.IsAssignableFrom<Unit>(map.FindObject(new ObjectGuid(guid)));
                map.Combat.Kill(player, target);
                Assert.False(target.IsAlive);
            }

            QuestStatusData data = quests.Services.StateOf(player)!.Quests.Get(SyntheticArcaneServer.RewardQuestId)!;
            Assert.Equal(2u, data.CreatureOrGOCount[0]);
            Assert.Equal(QuestStatus.Incomplete, data.Status);
            Assert.Equal(0u, player.Money);
            return true;
        }).WaitAsync(token);
        await quests.Persistence.FlushCharacterAsync(checked((int)owned.Guid)).WaitAsync(token);
    }

    private static async Task<OwnedClient> CreateClientAsync(SyntheticArcaneServer server, CancellationToken token)
    {
        await server.AddAccountAsync(AccountName, Password, token);
        WorldClient client = await AuthenticateAsync(server, token);
        try
        {
            var connection = new ScenarioConnection(client);
            await connection.CreateCharacterAsync("Econhero", token);
            ulong guid = Assert.Single(await connection.EnumerateAsync(token)).Guid;
            await using (AsyncServiceScope scope = server.Services.CreateAsyncScope())
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var letter = new MailRecord
                {
                    Id = MailId, MessageType = MailMessageType.Auction, SenderId = 1, ReceiverId = checked((int)guid),
                    Money = MailMoney, Subject = "Owned lifecycle cash", DeliverTime = now - 1, ExpireTime = now + 86400,
                };
                Assert.Equal(EconomyCommitResult.Committed, await new EfEconomyStore(
                    scope.ServiceProvider.GetRequiredService<CharacterDbContext>()).CommitAsync(
                    new EconomyCommitRequest(Guid.NewGuid(), [], [new InsertMail(letter, null)]), token));
            }

            await connection.LoginAsync(guid, token);
            await connection.SendAsync(WorldOpcode.CmsgGetMailList, ScenarioWire.Guid(Mailbox.Value), token);
            await connection.ReadUntilAsync(WorldOpcode.SmsgMailListResult, token);
            await server.World.InvokeAsync(() =>
            {
                Assert.Single(server.Services.GetRequiredService<EconomyFeature>().CachedMail(checked((int)guid))!);
                return true;
            }).WaitAsync(token);
            return new OwnedClient(client, connection, guid);
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
        WorldClient client = await WorldClient.ConnectAsync(Assert.Single(logon.Realms).GetLoopbackEndpoint(), token);
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

    private static Task TakeMoneyAsync(ScenarioConnection connection, CancellationToken token)
        => connection.SendAsync(WorldOpcode.CmsgMailTakeMoney, ScenarioWire.GuidQuest(Mailbox.Value, MailId), token);

    private static void AssertMailResult(byte[] payload, bool refused)
    {
        var reader = new PacketReader(payload);
        Assert.Equal(MailId, reader.ReadUInt32());
        reader.ReadUInt32(); // action
        uint result = reader.ReadUInt32();
        Assert.Equal(!refused, result == 0);
    }

    private static Task<Player> PlayerAsync(SyntheticArcaneServer server, ulong guid, CancellationToken token)
        => server.World.InvokeAsync(() => server.World.FindOnlinePlayer(new ObjectGuid(guid))
            ?? throw new InvalidOperationException("The owned lifecycle player is offline.")).WaitAsync(token);

    private static async Task WaitUntilOfflineAsync(SyntheticArcaneServer server, CancellationToken token)
    {
        while (await server.World.InvokeAsync(() => server.World.OnlinePlayerCount).WaitAsync(token) != 0)
        {
            await Task.Delay(10, token);
        }
    }

    private static async Task AssertStoredAsync(SyntheticArcaneServer server, int id, uint money, uint mailMoney, CancellationToken token)
    {
        await using AsyncServiceScope scope = server.Services.CreateAsyncScope();
        CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        Assert.Equal(money, (await new EfCharacterStore(db).GetByIdAsync(id, token))!.Money);
        Assert.Equal(mailMoney, Assert.Single(await new EfEconomyStore(db).GetMailsAsync(id, token)).Money);
    }

    private sealed class CashQuestContent(IQuestContentStore inner) : IQuestContentStore
    {
        public async Task<QuestContent> LoadAsync(CancellationToken cancellationToken = default)
        {
            QuestContent content = await inner.LoadAsync(cancellationToken);
            return content with
            {
                Templates = content.Templates.Select(quest => quest.Entry == SyntheticArcaneServer.RewardQuestId
                    ? new QuestTemplate
                    {
                        Entry = quest.Entry, Method = 2, Type = 0, MinLevel = 1, QuestLevel = 1, Title = "Cash objective",
                        ReqCreatureOrGOId1 = (int)SyntheticArcaneServer.TargetEntry, ReqCreatureOrGOCount1 = 2,
                        RewOrReqMoney = -(int)MailMoney,
                    } : quest).ToArray(),
            };
        }
    }

    private sealed class Control(bool holdCommit, bool refuseCommit = false)
    {
        public bool HoldCommit { get; } = holdCommit;
        public bool RefuseCommit { get; } = refuseCommit;
        public int ObserveLogin;
        public int LoginReads;
        public TaskCompletionSource<EconomyCommitRequest> CommitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> OwnershipRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ControlledEconomyStore(IEconomyStore inner, Control control) : IEconomyStore
    {
        public async Task<EconomyCommitResult> CommitAsync(EconomyCommitRequest request, CancellationToken cancellationToken = default)
        {
            control.CommitEntered.TrySetResult(request);
            if (control.HoldCommit)
            {
                await control.Release.Task.WaitAsync(cancellationToken);
            }

            return control.RefuseCommit ? EconomyCommitResult.Conflict : await inner.CommitAsync(request, cancellationToken);
        }

        public Task<bool> IsCommittedAsync(Guid operationId, CancellationToken cancellationToken = default) => inner.IsCommittedAsync(operationId, cancellationToken);
        public Task<IReadOnlyList<MailRecord>> GetMailsAsync(int receiverId, CancellationToken cancellationToken = default) => inner.GetMailsAsync(receiverId, cancellationToken);
        public Task<IReadOnlyList<MailRecord>> GetExpiredMailsAsync(long now, int max, CancellationToken cancellationToken = default) => inner.GetExpiredMailsAsync(now, max, cancellationToken);
        public Task<IReadOnlyList<MailRecord>> GetMailsInvolvingAsync(int characterId, CancellationToken cancellationToken = default) => inner.GetMailsInvolvingAsync(characterId, cancellationToken);
        public Task<string?> GetItemTextAsync(uint itemTextId, CancellationToken cancellationToken = default) => inner.GetItemTextAsync(itemTextId, cancellationToken);
        public Task<IReadOnlyList<AuctionRecord>> GetAuctionsAsync(CancellationToken cancellationToken = default) => inner.GetAuctionsAsync(cancellationToken);
        public Task<IReadOnlyDictionary<uint, ItemInstanceData>> GetEscrowItemsAsync(IReadOnlyCollection<uint> itemGuids, CancellationToken cancellationToken = default) => inner.GetEscrowItemsAsync(itemGuids, cancellationToken);
        public Task<AuctionSnapshot> GetAuctionSnapshotAsync(AuctionSnapshotFilter filter, CancellationToken cancellationToken = default) => inner.GetAuctionSnapshotAsync(filter, cancellationToken);
        public Task<EconomyIdSeed> GetIdSeedAsync(CancellationToken cancellationToken = default) => inner.GetIdSeedAsync(cancellationToken);
    }

    private sealed class ObservedCharacterStore(ICharacterStore inner, Control control) : ICharacterStore
    {
        public async Task<CharacterRecord?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
        {
            CharacterRecord? result = await inner.GetByIdAsync(id, cancellationToken);
            if (Volatile.Read(ref control.ObserveLogin) != 0)
            {
                Interlocked.Increment(ref control.LoginReads);
                control.OwnershipRead.TrySetResult(true);
            }

            return result;
        }

        public Task<IReadOnlyList<CharacterRecord>> GetByAccountAsync(int accountId, CancellationToken cancellationToken = default) => inner.GetByAccountAsync(accountId, cancellationToken);
        public Task<bool> IsNameTakenAsync(string name, CancellationToken cancellationToken = default) => inner.IsNameTakenAsync(name, cancellationToken);
        public Task<int> CountByAccountAsync(int accountId, CancellationToken cancellationToken = default) => inner.CountByAccountAsync(accountId, cancellationToken);
        public Task<CharacterRecord> CreateAsync(CharacterRecord character, CancellationToken cancellationToken = default) => inner.CreateAsync(character, cancellationToken);
        public Task<bool> DeleteAsync(int id, int accountId, CancellationToken cancellationToken = default) => inner.DeleteAsync(id, accountId, cancellationToken);
        public Task SaveStateAsync(CharacterState state, CancellationToken cancellationToken = default) => inner.SaveStateAsync(state, cancellationToken);
        public Task<IReadOnlyList<ActionButton>> GetActionButtonsAsync(int characterId, CancellationToken cancellationToken = default) => inner.GetActionButtonsAsync(characterId, cancellationToken);
        public Task<IReadOnlyList<CharacterIdentity>> GetAllIdentitiesAsync(CancellationToken cancellationToken = default) => inner.GetAllIdentitiesAsync(cancellationToken);
    }

    private sealed record OwnedClient(WorldClient Client, ScenarioConnection Connection, ulong Guid) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Client.DisposeAsync();
    }
}

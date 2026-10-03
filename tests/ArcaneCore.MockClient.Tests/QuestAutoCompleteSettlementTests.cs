using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Quests;
using ArcaneCore.Data.Stores;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using ArcaneCore.World.Npc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

/// <summary>
/// An autocomplete quest (Method 0) turned in through the real socket, world thread and SQLite reward transaction with
/// no journal entry at all (vmangos Player::CanRewardQuest, Player.cpp:12682-12700): the durable row is inserted by the
/// settlement, the live row is adopted, a repeat claim is refused and the row survives a fresh login. Every wait is on
/// the settlement's own completion frame or the persistence barrier, never on a fixed delay.
/// SQLite only on this machine; the MariaDB and PostgreSQL provider semantics of the insert are covered by the
/// provider theories in ArcaneCore.Data.Tests (QuestAutoCompleteRewardStoreTests) on hosted CI.
/// </summary>
public sealed class QuestAutoCompleteSettlementTests
{
    private const string AccountName = "AUTOCOMPLETE";
    private const string Password = "PASSWORD";
    private const uint AutoQuestId = 900200;
    private const uint AutoMoney = 777;

    [Fact]
    public async Task MethodZeroQuest_IsTurnedInWithoutAJournalEntry_PersistsOnce_AndSurvivesALoginAgain()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        CancellationToken token = deadline.Token;
        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(services =>
            services.AddScoped<IQuestContentStore>(provider => new AutoQuestContent(
                new EfQuestContentStore(provider.GetRequiredService<WorldDbContext>()))), token);
        await server.AddAccountAsync(AccountName, Password, token);
        QuestNpcFeature feature = server.Services.GetRequiredService<QuestNpcFeature>();

        WorldClient client = await AuthenticateAsync(server, token);
        ulong guid;
        await using (client)
        {
            var connection = new ScenarioConnection(client);
            await connection.CreateCharacterAsync("Autoquest", token);
            guid = Assert.Single(await connection.EnumerateAsync(token)).Guid;
            await connection.LoginAsync(guid, token);

            Player player = await server.World.InvokeAsync(() => server.World.FindOnlinePlayer(new ObjectGuid(guid))!).WaitAsync(token);
            Assert.True(await server.World.InvokeAsync(() => feature.Services.StateOf(player)!.Quests.Get(AutoQuestId) is null).WaitAsync(token));
            Assert.Equal(0u, await server.World.InvokeAsync(() => player.Money).WaitAsync(token));

            await connection.SendAsync(WorldOpcode.CmsgQuestgiverChooseReward,
                ScenarioWire.GuidQuestChoice(SyntheticArcaneServer.NpcGuid, AutoQuestId, 0), token);
            await connection.ReadUntilAsync(WorldOpcode.SmsgQuestgiverQuestComplete, token);

            await server.World.InvokeAsync(() =>
            {
                QuestStatusData live = feature.Services.StateOf(player)!.Quests.Get(AutoQuestId)!;
                Assert.Equal((QuestStatus.Complete, true), (live.Status, live.Rewarded));
                Assert.Equal(AutoMoney, player.Money);
                return true;
            }).WaitAsync(token);
            await AssertStoredAsync(server, guid, token);

            // A second claim is refused by the live guard and nothing more is paid.
            await connection.SendAsync(WorldOpcode.CmsgQuestgiverChooseReward,
                ScenarioWire.GuidQuestChoice(SyntheticArcaneServer.NpcGuid, AutoQuestId, 0), token);
            await connection.AssertNoRewardUntilPongAsync(0x90000510, token);
            Assert.Equal(AutoMoney, await server.World.InvokeAsync(() => player.Money).WaitAsync(token));
        }

        // A fresh login loads the durable row and still refuses a repeat.
        await server.FlushCharacterAsync(checked((int)guid), token);
        await using WorldClient again = await AuthenticateAsync(server, token);
        var second = new ScenarioConnection(again);
        Assert.Equal(guid, Assert.Single(await second.EnumerateAsync(token)).Guid);
        await second.LoginAsync(guid, token);
        await second.SendAsync(WorldOpcode.CmsgQuestgiverChooseReward,
            ScenarioWire.GuidQuestChoice(SyntheticArcaneServer.NpcGuid, AutoQuestId, 0), token);
        await second.AssertNoRewardUntilPongAsync(0x90000511, token);
        Player replacement = await server.World.InvokeAsync(() => server.World.FindOnlinePlayer(new ObjectGuid(guid))!).WaitAsync(token);
        await server.World.InvokeAsync(() =>
        {
            Assert.True(feature.Services.StateOf(replacement)!.Quests.Get(AutoQuestId)!.Rewarded);
            Assert.Equal(AutoMoney, replacement.Money);
            return true;
        }).WaitAsync(token);
        await AssertStoredAsync(server, guid, token);
    }

    private static async Task AssertStoredAsync(SyntheticArcaneServer server, ulong guid, CancellationToken token)
    {
        int id = checked((int)guid);
        await server.FlushCharacterAsync(id, token);
        await using AsyncServiceScope scope = server.Services.CreateAsyncScope();
        CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        CharacterRecord character = Assert.IsType<CharacterRecord>(await new EfCharacterStore(db).GetByIdAsync(id, token));
        Assert.Equal(AutoMoney, character.Money);
        CharacterQuestStatus row = Assert.Single((await new EfCharacterQuestStore(db).LoadAsync(id, token)).Quests, q => q.Quest == AutoQuestId);
        Assert.Equal(((byte)1, true, 0L), (row.Status, row.Rewarded, row.Timer));
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

    /// <summary>The synthetic content plus one Method 0 quest started and ended by the synthetic guide.</summary>
    private sealed class AutoQuestContent(IQuestContentStore inner) : IQuestContentStore
    {
        public async Task<QuestContent> LoadAsync(CancellationToken cancellationToken = default)
        {
            QuestContent content = await inner.LoadAsync(cancellationToken);
            var relation = new CreatureQuestRelation { Id = SyntheticArcaneServer.NpcEntry, Quest = AutoQuestId };
            return content with
            {
                Templates = [.. content.Templates, new QuestTemplate
                {
                    Entry = AutoQuestId, Method = 0, MinLevel = 1, QuestLevel = 1, Title = "Autocomplete", RequestItemsText = "Ready?",
                    OfferRewardText = "Take it.", RewOrReqMoney = (int)AutoMoney,
                }],
                Starters = [.. content.Starters, relation],
                Enders = [.. content.Enders, relation],
            };
        }
    }
}

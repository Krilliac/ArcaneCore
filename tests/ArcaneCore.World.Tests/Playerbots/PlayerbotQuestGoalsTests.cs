using ArcaneCore.Game;
using ArcaneCore.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Game.Quests;
using ArcaneCore.Protocol;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Npc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotQuestGoalsTests
{
    [Fact]
    public async Task AutonomousQuestStages_RespectBudgetThenAcceptAndRewardOnlyCompletedProgress()
    {
        var fixture = new QuestInteractionFixture();
        string databasePath = Path.Combine(Path.GetTempPath(), "arcane-playerbot-quest-" + Guid.NewGuid().ToString("N") + ".db");
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite", ["Database:ConnectionString"] = "Data Source=" + databasePath,
        }).Build();
        await using (ServiceProvider bootstrap = new ServiceCollection().AddLogging().AddCharacterDatabase(configuration).BuildServiceProvider())
            await bootstrap.GetRequiredService<CharacterDbInitializer>().InitializeAsync();
        QuestInteractionTestServices.Current.Value = fixture;
        WorldTestHost host;
        try { host = WorldTestHost.Start(configureServices: services => services.AddCharacterDatabase(configuration)); }
        finally { QuestInteractionTestServices.Current.Value = null; }
        await using (host)
        {
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(QuestInteractionFixture.Guid), "managed questgiver visibility");
                var goals = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true });
                QuestNpcFeature feature = host.WorldServices.GetRequiredService<QuestNpcFeature>();
                await host.World.InvokeAsync(() =>
                {
                    feature.Options.OrdinaryRewardQuestIds = [QuestInteractionFixture.QuestId];
                    var player = session.Player!;
                    session.ManagedBudget = new ManagedActionBudget(0);
                    goals.Update(player, 500);
                    Assert.Null(feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId));
                    for (int i = 0; i < 2; i++)
                    { session.ManagedBudget = new ManagedActionBudget(1); goals.Update(player, 500); }
                    Assert.Equal(QuestStatus.Incomplete, feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId)!.Status);
                    session.ManagedBudget = new ManagedActionBudget(1); goals.Update(player, 500);
                    Assert.Equal(90u, goals.PreferredCreatureEntry);
                    Assert.False(feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId)!.Rewarded);
                    // A producer event supplies completed progress; the bot never fabricates it.
                    feature.Services.KilledMonsterCredit(player, 90, ObjectGuid.WithEntry(HighGuid.Unit, 90, 42));
                    for (int i = 0; i < 3; i++)
                    { session.ManagedBudget = new ManagedActionBudget(1); goals.Update(player, 500); }
                    Assert.True(feature.PendingSettlementCount > 0 || feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId)!.Rewarded,
                        "Reward stage=" + goals.StageName + "; packets=" + string.Join(',', session.DrainManagedPackets().Select(p => p.Opcode)));
                    return true;
                });
                await feature.WaitForSettlementAsync((int)session.Player!.Guid.Low);
                await host.WaitForWorldAsync(() => feature.Services.StateOf(session.Player!)!.Quests.Get(QuestInteractionFixture.QuestId)!.Rewarded,
                    "managed durable quest reward");
                await using var persisted = host.WorldServices.CreateAsyncScope();
                CharacterQuestData data = await persisted.ServiceProvider.GetRequiredService<ICharacterQuestStore>()
                    .LoadAsync((int)session.Player!.Guid.Low);
                Assert.True(data.Quests.Single(q => q.Quest == QuestInteractionFixture.QuestId).Rewarded);
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
    }
}

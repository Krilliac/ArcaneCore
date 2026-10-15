using ArcaneCore.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Items;
using ArcaneCore.World.Tests.Npc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// H1 hardening: <c>bot-quest/accept-refused</c>, <c>giver-not-found</c> and <c>objective-no-progress</c> from <see cref="PlayerbotQuestGoals"/>
/// (<c>turnin-refused</c> is covered in <see cref="PlayerbotQuestGoalsTests"/>). Real quest handlers and the synthetic questgiver of
/// <see cref="QuestInteractionFixture"/>; the server is made to refuse by its own rules (a source item that does not fit, a dead giver).
/// </summary>
public sealed class PlayerbotQuestAnomalyTests
{
    private static readonly string AcceptRefused = PlayerbotAnomalies.Key("quest", "accept-refused");
    private static readonly string GiverNotFound = PlayerbotAnomalies.Key("quest", "giver-not-found");
    private static readonly string NoProgress = PlayerbotAnomalies.Key("quest", "objective-no-progress");

    private static bool AnyQuestAnomaly(AnomalyCapture c) => c.Any(AcceptRefused) || c.Any(GiverNotFound) || c.Any(NoProgress);

    private static void Think(WorldSession session, PlayerbotQuestGoals goals)
    {
        session.ManagedBudget = new ManagedActionBudget(1);
        goals.Update(session.Player!, 500);
    }

    [Fact]
    public async Task AcceptTheServerRefuses_Fires_OnceWithTheDiagnosedCode_AndIsNotRepeatedWhileSetAside()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = 9501, Name = "Quest crate", Class = (uint)ItemClass.Quest, Stackable = 1 });
        items.Templates.Templates.Add(new ItemTemplate { Entry = 9502, Name = "Filler", Class = (uint)ItemClass.TradeGoods, Quality = 1, Stackable = 1 });
        using IDisposable itemScope = items.Use();
        await using QuestHost quest = await QuestHost.StartAsync(new QuestInteractionFixture { SourceItem = 9501 }, manualClock: true);
        WorldSession session = quest.Session;
        var goals = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true });
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            while (PlayerbotTownGoals.FreeBagSlots(player) > 0)
                Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(9502, 1, out _));
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);
            Assert.True(goals.HasCandidate(player));
            for (int think = 0; think < 6; think++) Think(session, goals);

            Assert.Equal(1, capture.Count(AcceptRefused));
            AnomalyNotice notice = capture.Notices.Single(n => n.Key == AcceptRefused);
            Assert.Equal(AnomalySeverity.Warning, notice.Severity);
            Assert.Contains($"quest={QuestInteractionFixture.QuestId}", notice.Detail, StringComparison.Ordinal);
            Assert.Contains("code=InventoryFull", notice.Detail, StringComparison.Ordinal);
            Assert.False(capture.Any(GiverNotFound) || capture.Any(NoProgress), capture.Describe());
            return true;
        });
    }

    [Fact]
    public async Task AGiverThatIsGoneBeforeItAnswers_FiresGiverNotFound_Once()
    {
        await using QuestHost quest = await QuestHost.StartAsync(manualClock: true);
        WorldSession session = quest.Session;
        var goals = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true });
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);
            Assert.True(goals.HasCandidate(player));
            Think(session, goals); // the query goes out
            _ = session.DrainManagedPackets(); // its answer is lost
            ((Creature)player.Map!.FindObject(QuestInteractionFixture.Guid)!).Health = 0;
            for (int think = 0; think < 4; think++) Think(session, goals);

            Assert.Equal(1, capture.Count(GiverNotFound));
            AnomalyNotice notice = capture.Notices.Single(n => n.Key == GiverNotFound);
            Assert.Contains("reason=giver-despawned-or-dead", notice.Detail, StringComparison.Ordinal);
            Assert.Contains($"quest={QuestInteractionFixture.QuestId}", notice.Detail, StringComparison.Ordinal);
            return true;
        });
    }

    [Fact]
    public async Task AQuestTakenAndHandedInNormally_ReportsNothing()
    {
        await using QuestHost quest = await QuestHost.StartAsync();
        WorldSession session = quest.Session;
        var goals = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true });
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);
            for (int think = 0; think < 6; think++) Think(session, goals);
            Assert.NotNull(quest.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId));
            quest.Feature.Services.KilledMonsterCredit(player, 90, ObjectGuid.WithEntry(HighGuid.Unit, 90, 42));
            for (int think = 0; think < 6; think++) Think(session, goals);

            Assert.False(AnyQuestAnomaly(capture), capture.Describe());
            return true;
        });
    }

    [Fact]
    public async Task AnObjectiveWhoseSpawnsAreAllVisited_FiresNoProgress_Once_AndAnUnvisitedOneIsSilent()
    {
        await using QuestHost quest = await QuestHost.StartAsync();
        WorldSession session = quest.Session;
        var goals = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true });
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            for (int think = 0; think < 4; think++) Think(session, goals);
            Assert.NotNull(quest.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId));
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);

            // Not visited: the bot still has somewhere to look, nothing to report.
            goals.AllSpawnsVisited = (_, _, _) => false;
            for (int think = 0; think < 4; think++) Think(session, goals);
            Assert.False(capture.Any(NoProgress), capture.Describe());

            // Every spawn of the creature has been walked: the objective cannot be progressed from here.
            goals.AllSpawnsVisited = (_, _, _) => true;
            for (int think = 0; think < 4; think++) Think(session, goals);
            Assert.Equal(1, capture.Count(NoProgress));
            AnomalyNotice notice = capture.Notices.Single(n => n.Key == NoProgress);
            Assert.Contains($"quest={QuestInteractionFixture.QuestId} entry=90 progress=0 reason=all-spawns-visited", notice.Detail, StringComparison.Ordinal);
            return true;
        });
    }

    private sealed class QuestHost : IAsyncDisposable
    {
        private QuestHost(WorldTestHost host, WorldSession session)
        {
            Host = host;
            Session = session;
        }

        public WorldTestHost Host { get; }

        public WorldSession Session { get; }

        public QuestNpcFeature Feature => Host.WorldServices.GetRequiredService<QuestNpcFeature>();

        public static async Task<QuestHost> StartAsync(QuestInteractionFixture? questFixture = null, bool manualClock = false)
        {
            QuestInteractionFixture fixture = questFixture ?? new QuestInteractionFixture();
            string databasePath = Path.Combine(Path.GetTempPath(), "arcane-playerbot-quest-" + Guid.NewGuid().ToString("N") + ".db");
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite", ["Database:ConnectionString"] = "Data Source=" + databasePath,
            }).Build();
            await using (ServiceProvider bootstrap = new ServiceCollection().AddLogging().AddCharacterDatabase(configuration).BuildServiceProvider())
                await bootstrap.GetRequiredService<CharacterDbInitializer>().InitializeAsync();
            QuestInteractionTestServices.Current.Value = fixture;
            WorldTestHost host;
            try
            {
                host = WorldTestHost.Start(configureServices: services =>
                {
                    services.AddCharacterDatabase(configuration);
                    if (manualClock) services.AddSingleton<IWorldFeature, ManualClock>();
                });
            }
            finally { QuestInteractionTestServices.Current.Value = null; }
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(QuestInteractionFixture.Guid), "managed questgiver visibility");
            return new QuestHost(host, session);
        }

        private sealed class ManualClock : IWorldFeature
        {
            public void Attach(WorldRuntime world) => world.UseManualClock();
        }

        public async ValueTask DisposeAsync()
        {
            Session.Kick();
            await Session.ManagedClosed;
            await Host.DisposeAsync();
        }
    }
}

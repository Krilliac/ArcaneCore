using System.Numerics;
using ArcaneCore.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Npc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotQuestReturnTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedQuestTravelsToOffscreenEnderBeforeNearbyMob(bool alreadyAttacking)
    {
        await using var fixture = await ReturnFixture.CreateAsync(farGiver: true);
        WorldTestHost host = fixture.Host;
        WorldSession session = fixture.Session;
        await fixture.AcceptAndCompleteAsync();
        await host.World.InvokeAsync(() =>
        {
            var player = session.Player!;
            var giver = (Creature)player.Map!.FindObject(QuestInteractionFixture.Guid)!;
            giver.SetPosition(player.X + 150, player.Y, player.Z, 0);
            WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(player.Z));
            return true;
        });
        await host.WaitForWorldAsync(() => !session.Player!.VisibleObjects.Contains(QuestInteractionFixture.Guid), "offscreen return giver");
        Creature mob = await fixture.AddNearbyMobAsync();
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true });
        try
        {
            await host.World.InvokeAsync(() =>
            {
                var player = session.Player!;
                Assert.Same(mob, PlayerbotBrain.FindTarget(player));
                float before = player.X;
                if (alreadyAttacking)
                {
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.True(session.TryManagedAction(WorldOpcode.CmsgAttackswing, PlayerbotNavigation.GuidPayload(mob.Guid.Value)));
                    Assert.Same(mob, player.Combat.Victim);
                    player.Map!.Combat.DealDamage(mob, player, 1);
                    Assert.True(player.Combat.IsInCombat);
                    session.ManagedBudget = new ManagedActionBudget(1);
                    brain.Update(500);
                    Assert.Null(player.Combat.Victim);
                    Assert.True(player.Combat.IsInCombat); // Stop does not grant an out-of-combat state.
                }
                session.ManagedBudget = new ManagedActionBudget(0);
                brain.Update(500);
                Assert.Equal(before, player.X);
                Assert.Null(player.Combat.Victim);
                session.ManagedBudget = new ManagedActionBudget(1);
                brain.Update(500);
                Assert.Equal(before, player.X);
                PlayerbotMotion.ElapseForTests(player, 500);
                session.ManagedBudget = new ManagedActionBudget(1);
                brain.Update(500);
                Assert.True(player.X > before);
                Assert.Equal(PlayerbotGoalKind.Quest, brain.Goal);
                Assert.Equal(QuestInteractionFixture.QuestId, brain.QuestId);
                Assert.Null(player.Combat.Victim);
                Assert.False(fixture.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId)!.Rewarded);
                return true;
            });
        }
        finally { brain.Stop(); }
    }

    [Fact]
    public async Task VisibleCompletedQuestRewardsNormallyBeforeFreshAttackAndPersists()
    {
        await using var fixture = await ReturnFixture.CreateAsync(farGiver: false);
        await fixture.AcceptAndCompleteAsync();
        await fixture.AddNearbyMobAsync();
        var brain = new PlayerbotBrain(fixture.Session, new PlayerbotOptions { Enabled = true });
        try
        {
            await fixture.Host.World.InvokeAsync(() =>
            {
                for (int i = 0; i < 5 && fixture.Feature.PendingSettlementCount == 0; i++)
                {
                    fixture.Session.ManagedBudget = new ManagedActionBudget(1);
                    brain.Update(500);
                    Assert.Null(fixture.Session.Player!.Combat.Victim);
                }
                Assert.True(fixture.Feature.PendingSettlementCount > 0
                    || fixture.Feature.Services.StateOf(fixture.Session.Player!)!.Quests.Get(QuestInteractionFixture.QuestId)!.Rewarded);
                return true;
            });
            await fixture.Feature.WaitForSettlementAsync((int)fixture.Session.Player!.Guid.Low);
            await fixture.Host.WaitForWorldAsync(() => fixture.Feature.Services.StateOf(fixture.Session.Player!)!
                .Quests.Get(QuestInteractionFixture.QuestId)!.Rewarded, "ordinary return reward");
            await using var scope = fixture.Host.WorldServices.CreateAsyncScope();
            CharacterQuestData saved = await scope.ServiceProvider.GetRequiredService<ICharacterQuestStore>()
                .LoadAsync((int)fixture.Session.Player!.Guid.Low);
            Assert.True(saved.Quests.Single(q => q.Quest == QuestInteractionFixture.QuestId).Rewarded);
        }
        finally { brain.Stop(); }
    }

    [Fact]
    public async Task IncompleteOrUnapprovedQuestDoesNotCreateReturnPriority()
    {
        await using var fixture = await ReturnFixture.CreateAsync(farGiver: false);
        await fixture.AcceptAndCompleteAsync(complete: false);
        await fixture.Host.World.InvokeAsync(() =>
        {
            var goals = new PlayerbotQuestGoals(fixture.Session, new PlayerbotOptions { Enabled = true });
            Assert.Equal(0u, goals.CompletedReturnQuest(fixture.Session.Player!));
            fixture.Feature.Services.KilledMonsterCredit(fixture.Session.Player!, 90, ObjectGuid.WithEntry(HighGuid.Unit, 90, 42));
            Assert.Equal(QuestInteractionFixture.QuestId, goals.CompletedReturnQuest(fixture.Session.Player!));
            fixture.Feature.Options.OrdinaryRewardQuestIds = [];
            Assert.Equal(0u, goals.CompletedReturnQuest(fixture.Session.Player!));
            return true;
        });
    }

    private sealed class ReturnFixture(WorldTestHost host, WorldSession session) : IAsyncDisposable
    {
        internal WorldTestHost Host => host;
        internal WorldSession Session => session;
        internal QuestNpcFeature Feature => host.WorldServices.GetRequiredService<QuestNpcFeature>();

        internal static async Task<ReturnFixture> CreateAsync(bool farGiver)
        {
            var original = new QuestInteractionFixture();
            string db = Path.Combine(Path.GetTempPath(), "arcane-return-" + Guid.NewGuid().ToString("N") + ".db");
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Database:Provider"] = "Sqlite", ["Database:ConnectionString"] = "Data Source=" + db }).Build();
            await using (ServiceProvider bootstrap = new ServiceCollection().AddLogging().AddCharacterDatabase(configuration).BuildServiceProvider())
                await bootstrap.GetRequiredService<CharacterDbInitializer>().InitializeAsync();
            QuestInteractionTestServices.Current.Value = original;
            WorldTestHost host;
            try
            {
                host = WorldTestHost.Start(configureServices: services =>
                {
                    services.AddCharacterDatabase(configuration);
                    if (farGiver) services.AddSingleton<ICreatureDataStore>(new FarGiverStore(original));
                });
            }
            finally { QuestInteractionTestServices.Current.Value = null; }
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            await host.World.InvokeAsync(() =>
            {
                var giver = (Creature)session.Player!.Map!.FindObject(QuestInteractionFixture.Guid)!;
                giver.SetPosition(session.Player.X + 1, session.Player.Y, session.Player.Z, 0);
                host.WorldServices.GetRequiredService<QuestNpcFeature>().Options.OrdinaryRewardQuestIds = [QuestInteractionFixture.QuestId];
                return true;
            });
            await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(QuestInteractionFixture.Guid), "return fixture giver visibility");
            return new ReturnFixture(host, session);
        }

        internal async Task AcceptAndCompleteAsync(bool complete = true)
        {
            await host.World.InvokeAsync(() =>
            {
                var goals = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true });
                for (int i = 0; i < 2; i++)
                { session.ManagedBudget = new ManagedActionBudget(1); goals.Update(session.Player!, 500); }
                Assert.Equal(QuestStatus.Incomplete, Feature.Services.StateOf(session.Player!)!.Quests.Get(QuestInteractionFixture.QuestId)!.Status);
                if (complete) Feature.Services.KilledMonsterCredit(session.Player!, 90, ObjectGuid.WithEntry(HighGuid.Unit, 90, 42));
                return true;
            });
        }

        internal async Task<Creature> AddNearbyMobAsync()
        {
            Creature? mob = null;
            await host.World.InvokeAsync(() =>
            {
                var player = session.Player!;
                mob = new Creature(900030, new CreatureTemplate { Entry = 90, Name = "nearby hostile", Faction = 900012,
                    MinLevelHealth = 20, MaxLevelHealth = 20, DisplayIds = [49] }, null, CreatureContent.Empty, new Random(1));
                mob.SetPosition(player.X - 2, player.Y, player.Z, 0);
                player.Map!.AddObject(mob);
                return true;
            });
            await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(mob!.Guid), "nearby hostile visibility");
            return mob!;
        }

        public async ValueTask DisposeAsync()
        { session.Kick(); await session.ManagedClosed; await host.DisposeAsync(); }
    }

    private sealed class FarGiverStore(QuestInteractionFixture original) : ICreatureDataStore
    {
        public async Task<CreatureContent> LoadAsync(CancellationToken cancellationToken = default)
        {
            CreatureContent content = await ((ICreatureDataStore)original).LoadAsync(cancellationToken);
            return new CreatureContent(content.Templates, content.GetSpawns(0).Select(spawn => spawn with { X = -8799.95f }), [], [], []);
        }
    }

    private sealed class FlatFloor(float height) : ILineOfSight
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;
        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        { hit = to; return false; }
        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => height;
        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        { info = default; return false; }
    }
}

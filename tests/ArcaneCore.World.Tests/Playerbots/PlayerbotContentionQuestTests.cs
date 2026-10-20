using ArcaneCore.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Items;
using ArcaneCore.World.Tests.Npc;
using ArcaneCore.World.Tests.Playerbots.Combat;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Contention with a real quest log (docs/areas/playbots.md, Contention): an item objective is hunted at the creatures that drop the
/// item, and a mob another player tagged for the same quest raises a group request instead of only being left alone.
/// </summary>
public sealed class PlayerbotContentionQuestTests
{
    private const uint Wolf = 299, Meat = 750;

    [Fact]
    public async Task AnItemObjective_IsHuntedAtTheCreaturesThatDropTheItem()
    {
        // The bot hunts an item objective only while its bags can store the item (CanStoreObjectiveItem): the item needs a template.
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = Meat, Name = "Tough Wolf Meat", Class = (uint)ItemClass.Quest, Stackable = 10 });
        using IDisposable itemScope = items.Use();
        await using Quest quest = await Quest.StartAsync(new QuestInteractionFixture { RepeatableItem = Meat, RepeatableItemCount = 3 });
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = quest.Bot.Player!;
            quest.Accept(quest.Bot);
            quest.Host.WorldServices.GetRequiredService<GameObjectLootFeature>().ReplaceLootContent(new LootContent(
                [(LootTableKind.Creature, new LootStoreRow(500, Meat, 50f, 0, 1, 1))], [new CreatureLootInfo(Wolf, 500, 0, 0, 0)]));
            quest.Host.WorldServices.GetRequiredService<CreatureWorldFeature>().Install(new CreatureContent(
                [new CreatureTemplate { Entry = Wolf, Name = "Prowler", MinLevel = 1, MaxLevel = 1, DisplayIds = [49] }],
                [new CreatureSpawn { Guid = 910001, Entry = Wolf, MapId = player.MapId, X = player.X + 20, Y = player.Y, Z = player.Z }],
                [], [], []));
            var goals = new PlayerbotQuestGoals(quest.Bot, new PlayerbotOptions { Enabled = true });
            quest.Bot.ManagedBudget = new ManagedActionBudget(1);
            goals.Update(player, 500);
            Assert.Equal(Wolf, goals.PreferredCreatureEntry);
            return true;
        });
    }

    [Fact]
    public async Task AMobTaggedByAnUngroupedPlayerOnTheSameKillQuest_RaisesAContentionGroupSignal()
    {
        await using Quest quest = await Quest.StartAsync(new QuestInteractionFixture());
        WorldSession stranger = await CombatTestSessions.EnterAsync(quest.Host, "CONTQSTR", "Contqstr");
        var options = new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500, Risk = { Enabled = false } };
        options.Groups.Enabled = true;
        var brain = new PlayerbotBrain(quest.Bot, options);
        try
        {
            Creature mob = null!;
            await quest.Host.OnWorldAsync(() =>
            {
                quest.Accept(quest.Bot);
                quest.Accept(stranger);
                mob = new Creature(995101, new CreatureTemplate { Entry = 90, Name = "quest mob", CreatureType = 1, MinLevel = 1, MaxLevel = 1,
                    MinLevelHealth = 50, MaxLevelHealth = 50 }, null, CreatureContent.Empty, new Random(5));
                mob.Relocate(quest.Bot.Player!.X + 4, quest.Bot.Player.Y, quest.Bot.Player.Z, 0, quest.Host.World.NowMs);
                quest.Bot.Player.Map!.AddObject(mob);
                return true;
            });
            await quest.Host.WaitForWorldAsync(() => quest.Bot.Player!.VisibleObjects.Contains(mob.Guid), "quest mob");
            await quest.Host.OnWorldAsync(() =>
            {
                quest.Bot.Services.GetRequiredService<SpellFeature>().System.Store = new SpellStore([], [], []);
                for (int i = 0; i < 2; i++)
                {
                    quest.Bot.ManagedBudget = new ManagedActionBudget(1);
                    brain.Update(500);
                }

                Assert.Same(mob, brain.InspectionTarget);
                quest.Bot.Player!.Map!.Combat.DealDamage(stranger.Player!, mob, 1); // the other player tags the quest mob
                for (int i = 0; i < 4; i++)
                {
                    quest.Bot.ManagedBudget = new ManagedActionBudget(1);
                    brain.Update(500);
                }

                Assert.NotNull(brain.Risk.GroupSignal);
                Assert.Equal(PlayerbotGroupSignal.ContentionSource, brain.Risk.GroupSignal!.Value.Source);
                Assert.Equal(90u, brain.Risk.GroupSignal.Value.Entry);
                return true;
            });
        }
        finally
        {
            brain.Stop();
            stranger.Kick(); await stranger.ManagedClosed;
        }
    }

    public enum Field { AllTagged, OneUntagged, OtherEntryTagged, NothingInSight, FreeButUnreachable }

    /// <summary>
    /// The bot hunts the kill quest's creature (entry 90) while <paramref name="field"/> says which creatures stand about; a stranger tags
    /// the ones named. Returns the contested hunting time after each of six thinks 120 ms apart, and whether the bot holds a target.
    /// </summary>
    private static async Task<(uint[] Contested, bool HasTarget)> HuntAsync(Field field, bool vanishAfterSixThinks = false)
    {
        await using Quest quest = await Quest.StartAsync(new QuestInteractionFixture());
        WorldSession stranger = await CombatTestSessions.EnterAsync(quest.Host, "CONTASTR", "Contastr");
        var options = new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500, Risk = { Enabled = false } };
        var brain = new PlayerbotBrain(quest.Bot, options);
        try
        {
            var mobs = new List<Creature>();
            var tag = new List<Creature>();
            await quest.Host.OnWorldAsync(() =>
            {
                quest.Accept(quest.Bot);
                Player near = quest.Bot.Player!;
                Creature Add(uint low, uint entry, float offset)
                {
                    var creature = new Creature(low, new CreatureTemplate { Entry = entry, Name = "quest mob " + entry, CreatureType = 1, MinLevel = 1,
                        MaxLevel = 1, MinLevelHealth = 50, MaxLevelHealth = 50 }, null, CreatureContent.Empty, new Random((int)low));
                    creature.Relocate(near.X + offset, near.Y, near.Z, 0, quest.Host.World.NowMs);
                    near.Map!.AddObject(creature);
                    mobs.Add(creature);
                    return creature;
                }

                switch (field)
                {
                    case Field.AllTagged: tag.Add(Add(995201, 90, 4f)); tag.Add(Add(995202, 90, 6f)); break;
                    // The free mob sits in melee range (4 yd): the test world has no navigation data, so a route to a farther one is refused and the
                    // mob is skipped as unreachable for 30 s (Field.FreeButUnreachable relies on exactly that).
                    case Field.OneUntagged: tag.Add(Add(995201, 90, 6f)); Add(995202, 90, 4f); break;
                    case Field.FreeButUnreachable: tag.Add(Add(995201, 90, 4f)); Add(995202, 90, 6f); break;
                    case Field.OtherEntryTagged: tag.Add(Add(995201, 91, 6f)); Add(995202, 90, 4f); break;
                }

                return true;
            });
            await quest.Host.WaitForWorldAsync(() => mobs.All(m => quest.Bot.Player!.VisibleObjects.Contains(m.Guid)), "quest mobs");
            await quest.Host.OnWorldAsync(() =>
            {
                quest.Bot.Services.GetRequiredService<SpellFeature>().System.Store = new SpellStore([], [], []);
                foreach (Creature mob in tag) quest.Bot.Player!.Map!.Combat.DealDamage(stranger.Player!, mob, 1);
                return true;
            });
            bool everHeld = false;
            var contested = new uint[vanishAfterSixThinks ? 10 : 6];
            for (int i = 0; i < contested.Length; i++)
            {
                await quest.Host.OnWorldAsync(() =>
                {
                    quest.Bot.ManagedBudget = new ManagedActionBudget(1);
                    brain.Update(500);
                    contested[i] = brain.Contention.ContestedMs;
                    everHeld |= brain.InspectionTarget is not null;
                    return true;
                });
                await Task.Delay(120);
                if (i == 5 && vanishAfterSixThinks)
                {
                    // Every creature leaves the map: the bot sees nothing, and a stale "taken by others" count must not keep the clock running.
                    await quest.Host.OnWorldAsync(() => { foreach (Creature mob in mobs) mob.Map!.RemoveObject(mob); return true; });
                    await quest.Host.WaitForWorldAsync(() => mobs.All(m => !quest.Bot.Player!.VisibleObjects.Contains(m.Guid)), "mobs gone");
                }
            }

            bool hasTarget = everHeld;
            return (contested, hasTarget);
        }
        finally
        {
            brain.Stop();
            stranger.Kick(); await stranger.ManagedClosed;
        }
    }

    [Fact]
    public async Task EveryCreatureOfTheQuestEntryTaggedByAStranger_GrowsTheContestedClock()
    {
        (uint[] contested, bool hasTarget) = await HuntAsync(Field.AllTagged);
        Assert.False(hasTarget);
        Assert.True(contested[^1] > contested[2], string.Join(",", contested)); // it keeps growing across thinks
        Assert.True(contested[^1] > 0);
    }

    [Theory]
    [InlineData(Field.OneUntagged, true)]
    [InlineData(Field.OtherEntryTagged, true)]
    [InlineData(Field.NothingInSight, false)]
    public async Task AnUntaggedCreatureOfTheEntry_OrNoneInSight_LeavesTheContestedClockAtZero(Field field, bool hasTargetExpected)
    {
        (uint[] contested, bool hasTarget) = await HuntAsync(field);
        Assert.Equal(hasTargetExpected, hasTarget);
        Assert.All(contested, ms => Assert.Equal(0u, ms));
    }

    [Fact]
    public async Task AFreeCreatureThatIsSkipped_IsNotContention_SoTheClockStaysAtZero()
    {
        // The tagged mob is blocked, the free one is dropped as unreachable (no navigation data): the bot is not contended for it.
        (uint[] contested, _) = await HuntAsync(Field.FreeButUnreachable);
        Assert.All(contested, ms => Assert.Equal(0u, ms));
    }

    [Fact]
    public async Task WhenTheTakenCreaturesLeaveSight_TheClockStopsInsteadOfRunningOnAStaleCount()
    {
        (uint[] contested, _) = await HuntAsync(Field.AllTagged, vanishAfterSixThinks: true);
        Assert.True(contested[5] > 0);
        // Four more thinks, all inside the 5 s gap: nothing is contested any more.
        for (int i = 6; i < contested.Length; i++) Assert.Equal(contested[5], contested[i]);
    }

    private sealed class Quest : IAsyncDisposable
    {
        private Quest(WorldTestHost host, WorldSession bot)
        {
            Host = host;
            Bot = bot;
        }

        public WorldTestHost Host { get; }

        public WorldSession Bot { get; }

        public void Accept(WorldSession session)
        {
            var body = new PacketWriter(12);
            body.WriteUInt64(QuestInteractionFixture.Guid.Value);
            body.WriteUInt32(QuestInteractionFixture.QuestId);
            session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(session.TryManagedAction(WorldOpcode.CmsgQuestgiverAcceptQuest, body.ToArray()));
        }

        public static async Task<Quest> StartAsync(QuestInteractionFixture fixture)
        {
            string databasePath = Path.Combine(Path.GetTempPath(), "arcane-playerbot-contention-" + Guid.NewGuid().ToString("N") + ".db");
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
            WorldSession bot = await PlayerbotMovementControlTests.EnterAsync(host);
            await host.WaitForWorldAsync(() => bot.Player!.VisibleObjects.Contains(QuestInteractionFixture.Guid), "questgiver visibility");
            return new Quest(host, bot);
        }

        public async ValueTask DisposeAsync()
        {
            Bot.Kick();
            await Bot.ManagedClosed;
            await Host.DisposeAsync();
        }
    }
}

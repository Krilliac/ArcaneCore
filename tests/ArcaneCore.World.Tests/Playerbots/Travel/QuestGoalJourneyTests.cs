using System.Numerics;
using ArcaneCore.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Features;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Teleport;
using ArcaneCore.World.Tests.Npc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Travel;

/// <summary>
/// Quest goals by authentic travel (<see cref="PlayerbotWorldDestinations"/>, docs/areas/playbots.md, Travel): a hunted creature with no
/// spawn on the bot's map, or a quest goal across Teldrassil's water on this map, begins a journey through the travel seam
/// (<see cref="IPlayerbotTravel.TryBegin"/>); a walkable spawn on the bot's side wins over a nearer one across the water, and the tram's
/// level gate holds. A goal whose journey could not begin waits the transit backoff before the next try, doubling while it keeps failing,
/// and only that goal waits. With travel off, or for a completed quest's ender on another map, no journey begins.
/// </summary>
public sealed class QuestGoalJourneyTests
{
    /// <summary>A travel seam that records every journey asked of it and begins one or refuses, as told (world thread only).</summary>
    private sealed class ScriptedTravel(bool begins) : IPlayerbotTravel
    {
        public List<(uint Map, Vector3 To)> Begun { get; } = [];

        // A journey begun stays under way until a test ends it (the brain would drive it; these tests ask only whether one began).
        public bool Traveling { get; set; }

        public bool CanReach(WorldSession session, Player player, uint toMap, Vector3 to) => begins;

        public bool TryBegin(WorldSession session, Player player, uint toMap, Vector3 to, out string? why)
        {
            Begun.Add((toMap, to));
            why = begins ? null : "no route (scripted)";
            Traveling |= begins;
            return begins;
        }

        public bool IsTraveling(Player player) => Traveling;
    }

    private const uint Hunted = QuestInteractionFixture.RemoteEnder;

    private const float Floor = 83.53f; // FlatFloor's height

    private static readonly CreatureSpawn OtherMapSpawn = new() { Guid = 900021, Entry = Hunted, MapId = 1, X = -441.8f, Y = -2596.08f, Z = 96.2f };

    private static readonly CreatureSpawn TeldrassilSpawn = new() { Guid = 900021, Entry = Hunted, MapId = 1, X = 8100f, Y = 800f, Z = Floor };

    /// <summary>On the bot's side of the water, farther from it than <see cref="TeldrassilSpawn"/>.</summary>
    private static readonly CreatureSpawn DarkshoreSpawn = new() { Guid = 900022, Entry = Hunted, MapId = 1, X = 6450f, Y = 2900f, Z = Floor };

    private static readonly Vector3 Darkshore = new(6450f, 800f, Floor);

    private static Vector3 At(CreatureSpawn spawn) => new(spawn.X, spawn.Y, spawn.Z);

    /// <summary>Travel on, and the transit gate open for the level 1 unhomed test bot (<see cref="PlayerbotWorldDestinations.TransitAllowed"/>).</summary>
    private static PlayerbotOptions Options()
    {
        var options = new PlayerbotOptions { Enabled = true, MaxRouteYards = 20_000 };
        options.Population.TramMinLevel = 0;
        return options;
    }

    [Fact]
    public async Task AHuntedCreature_OnAnotherMapOnly_BeginsAJourneyThere()
    {
        var travel = new ScriptedTravel(begins: true);
        await using JourneyHost host = await JourneyHost.StartAsync(travel, OtherMapSpawn);
        var destinations = await host.OnWorldAsync(() => new PlayerbotWorldDestinations(host.Session, Options()));

        Assert.True(await host.OnWorldAsync(() => destinations.Update(host.Player, Hunted, 100, preferredQuestId: QuestInteractionFixture.QuestId)),
            "no journey to the hunted creature");
        Assert.Equal([(1u, At(OtherMapSpawn))], travel.Begun);
        Assert.Equal(Hunted, destinations.TargetEntry);
        Assert.Equal(QuestInteractionFixture.QuestId, destinations.QuestId);
    }

    /// <summary>
    /// A completed quest's ender on another map is no journey: the quest goals return only to an ender on the bot's map and abandon a
    /// cross-continent delivery (docs/areas/playbots.md, "Intercontinent quest handoffs").
    /// </summary>
    [Fact]
    public async Task ACompletedQuestsEnder_OnAnotherMap_BeginsNoJourney()
    {
        var travel = new ScriptedTravel(begins: true);
        await using JourneyHost host = await JourneyHost.StartAsync(travel, OtherMapSpawn);
        await host.CompleteQuestAsync();
        var destinations = await host.OnWorldAsync(() => new PlayerbotWorldDestinations(host.Session, Options()));

        Assert.False(await host.OnWorldAsync(() => destinations.Update(host.Player, 0, 100, QuestInteractionFixture.QuestId)));
        Assert.Empty(travel.Begun);
    }

    [Fact]
    public async Task WithTravelOff_AHuntedCreatureOnAnotherMap_BeginsNoJourney()
    {
        var travel = new ScriptedTravel(begins: true);
        await using JourneyHost host = await JourneyHost.StartAsync(travel, OtherMapSpawn);
        PlayerbotOptions options = Options();
        options.Travel.Enabled = false;
        var destinations = await host.OnWorldAsync(() => new PlayerbotWorldDestinations(host.Session, options));

        Assert.False(await host.OnWorldAsync(() => destinations.Update(host.Player, Hunted, 100)));
        Assert.Empty(travel.Begun);
    }

    /// <summary>
    /// A journey that cannot begin is not asked again at once: the goal waits the transit backoff (60 s), then twice that after the second
    /// failure. Another goal (the same creature hunted for another quest) is not held by that wait.
    /// </summary>
    [Fact]
    public async Task AGoalWhoseJourneyCannotBegin_WaitsTheTransitBackoff_DoublingPerFailure_AndOnlyThatGoalWaits()
    {
        var travel = new ScriptedTravel(begins: false);
        await using JourneyHost host = await JourneyHost.StartAsync(travel, manualClock: true, OtherMapSpawn);
        PlayerbotOptions options = Options();
        var destinations = await host.OnWorldAsync(() => new PlayerbotWorldDestinations(host.Session, options));
        uint first = options.Travel.BackoffMs(1), second = options.Travel.BackoffMs(2);
        Assert.Equal(2 * first, second);
        Task<bool> Hunt() => host.OnWorldAsync(() => destinations.Update(host.Player, Hunted, 100));

        Assert.False(await Hunt());
        Assert.Single(travel.Begun);
        Assert.False(await Hunt());
        Assert.Single(travel.Begun); // waiting: not asked again

        // The same creature hunted for a quest is another goal: its journey is asked for at once.
        Assert.False(await host.OnWorldAsync(() => destinations.Update(host.Player, Hunted, 100, preferredQuestId: QuestInteractionFixture.QuestId)));
        Assert.Equal(2, travel.Begun.Count);

        await host.Host.World.AdvanceClockAsync(first - 1_000);
        Assert.False(await Hunt());
        Assert.Equal(2, travel.Begun.Count); // still waiting

        await host.Host.World.AdvanceClockAsync(2_000);
        Assert.False(await Hunt());
        Assert.Equal(3, travel.Begun.Count); // the first wait is over: asked again, and refused again

        await host.Host.World.AdvanceClockAsync(first + 1_000);
        Assert.False(await Hunt());
        Assert.Equal(3, travel.Begun.Count); // the second wait is twice the first

        await host.Host.World.AdvanceClockAsync(second - first);
        Assert.False(await Hunt());
        Assert.Equal(4, travel.Begun.Count);
    }

    /// <summary>The ender of a completed quest on Teldrassil, seen from Darkshore on the same map: across the water, a journey there.</summary>
    [Fact]
    public async Task ACompletedQuestsEnder_AcrossTeldrassilsWater_BeginsAJourneyThere()
    {
        var travel = new ScriptedTravel(begins: true);
        await using JourneyHost host = await JourneyHost.StartAsync(travel, TeldrassilSpawn);
        await host.CompleteQuestAsync();
        await host.PlaceAsync(1, Darkshore);
        var destinations = await host.OnWorldAsync(() => new PlayerbotWorldDestinations(host.Session, Options()));

        Assert.True(await host.OnWorldAsync(() => destinations.Update(host.Player, 0, 100, QuestInteractionFixture.QuestId)), "no journey to the ender");
        Assert.Equal([(1u, At(TeldrassilSpawn))], travel.Begun);
        Assert.Equal(Hunted, destinations.TargetEntry);
        Assert.Equal(QuestInteractionFixture.QuestId, destinations.QuestId);
    }

    /// <summary>The tram's gate holds for the boat too: a level 1 bot with no home (below TramMinLevel, 10) begins no journey across the water.</summary>
    [Fact]
    public async Task ALowLevelBotWithNoHome_BeginsNoJourneyAcrossTheWater()
    {
        var travel = new ScriptedTravel(begins: true);
        await using JourneyHost host = await JourneyHost.StartAsync(travel, TeldrassilSpawn);
        await host.CompleteQuestAsync();
        await host.PlaceAsync(1, Darkshore);
        PlayerbotOptions options = Options();
        options.Population.TramMinLevel = 10;
        var destinations = await host.OnWorldAsync(() => new PlayerbotWorldDestinations(host.Session, options));

        Assert.Equal(1u, await host.OnWorldAsync(() => host.Player.Level));
        await host.OnWorldAsync(() => destinations.Update(host.Player, 0, 100, QuestInteractionFixture.QuestId));
        Assert.Empty(travel.Begun);
    }

    /// <summary>
    /// A goal across the water whose journey could not begin is not passed over while it waits: the next think judges it by the rules from
    /// before the journeys (here the walk the open pathfinder grants; on real data the flight path, or the unreachable skip that sets it
    /// aside), so the goal is never held without a decision for the whole backoff.
    /// </summary>
    [Fact]
    public async Task AWaterGoalWhoseJourneyFailed_IsJudgedByTheWalkRules_WhileItWaits()
    {
        var travel = new ScriptedTravel(begins: false);
        await using JourneyHost host = await JourneyHost.StartAsync(travel, TeldrassilSpawn);
        await host.PlaceAsync(1, Darkshore);
        await host.Host.OnWorldAsync(() => WorldCollision.Of(host.Host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder()));
        var destinations = await host.OnWorldAsync(() => new PlayerbotWorldDestinations(host.Session, Options()));

        Assert.False(await host.OnWorldAsync(() => destinations.Update(host.Player, Hunted, 100)), "the refused journey owned the think");
        Assert.Equal([(1u, At(TeldrassilSpawn))], travel.Begun);
        Assert.False(await host.OnWorldAsync(() => PlayerbotMotion.IsActive(host.Player)), "walked in the think the journey was asked for");

        Assert.True(await host.OnWorldAsync(() => destinations.Update(host.Player, Hunted, 100)), "the waiting goal was passed over undecided");
        Assert.Single(travel.Begun); // waiting: no second journey asked for
        Assert.True(await host.OnWorldAsync(() => PlayerbotMotion.IsActive(host.Player)), "the walk rules did not take the waiting goal");
    }

    /// <summary>
    /// A spawn on the bot's own side is walked to although one across the water is nearer: the water is no shortcut, so the journey waits
    /// until nothing on this side is left.
    /// </summary>
    [Fact]
    public async Task AWalkableSpawnOnThisSide_IsChosenOverANearerOneAcrossTheWater()
    {
        var travel = new ScriptedTravel(begins: true);
        await using JourneyHost host = await JourneyHost.StartAsync(travel, TeldrassilSpawn, DarkshoreSpawn);
        await host.PlaceAsync(1, Darkshore);
        await host.Host.OnWorldAsync(() => WorldCollision.Of(host.Host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder()));
        var destinations = await host.OnWorldAsync(() => new PlayerbotWorldDestinations(host.Session, Options()));
        Assert.True(Vector3.Distance(Darkshore, At(TeldrassilSpawn)) < Vector3.Distance(Darkshore, At(DarkshoreSpawn)), "the fixture lost its point");

        Assert.True(await host.OnWorldAsync(() => destinations.Update(host.Player, Hunted, 100)), "no walk to the spawn on this side");
        Assert.Empty(travel.Begun);
        Assert.Equal(Hunted, destinations.TargetEntry);
        Assert.True(await host.OnWorldAsync(() => PlayerbotMotion.IsActive(host.Player)), "not walking towards the spawn on this side");
    }

    private sealed class JourneyHost : IAsyncDisposable
    {
        private JourneyHost(WorldTestHost host, WorldSession session)
        {
            this.Host = host;
            Session = session;
        }

        public WorldTestHost Host { get; }

        public WorldSession Session { get; }

        public Player Player => Session.Player!;

        public static Task<JourneyHost> StartAsync(IPlayerbotTravel travel, params CreatureSpawn[] enders) => StartAsync(travel, false, enders);

        public static async Task<JourneyHost> StartAsync(IPlayerbotTravel travel, bool manualClock, params CreatureSpawn[] enders)
        {
            var fixture = new QuestInteractionFixture { EnderSpawns = enders };
            string databasePath = Path.Combine(Path.GetTempPath(), "arcane-playerbot-quest-journey-" + Guid.NewGuid().ToString("N") + ".db");
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
                    services.AddSingleton(travel);
                    if (manualClock) services.AddSingleton<IWorldFeature, ManualClock>();
                });
            }
            finally { QuestInteractionTestServices.Current.Value = null; }
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(QuestInteractionFixture.Guid), "managed questgiver visibility");
            return new JourneyHost(host, session);
        }

        public Task<T> OnWorldAsync<T>(Func<T> action) => Host.OnWorldAsync(action);

        /// <summary>Accept the synthetic quest from its giver and credit its kill: complete, its ender not yet visited.</summary>
        public Task CompleteQuestAsync() => Host.OnWorldAsync(() =>
        {
            QuestNpcFeature feature = Host.WorldServices.GetRequiredService<QuestNpcFeature>();
            var body = new PacketWriter(12);
            body.WriteUInt64(QuestInteractionFixture.Guid.Value);
            body.WriteUInt32(QuestInteractionFixture.QuestId);
            Session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(Session.TryManagedAction(WorldOpcode.CmsgQuestgiverAcceptQuest, body.ToArray()));
            feature.Services.KilledMonsterCredit(Player, 90, ObjectGuid.WithEntry(HighGuid.Unit, 90, 42));
            Assert.Equal(QuestStatus.Complete, feature.Services.StateOf(Player)!.Quests.Get(QuestInteractionFixture.QuestId)?.Status);
        });

        /// <summary>Teleport the bot and acknowledge like a client until it stands there.</summary>
        public async Task PlaceAsync(uint mapId, Vector3 at)
        {
            TeleportService teleports = Host.WorldServices.GetRequiredService<TeleportFeature>().Teleports;
            Assert.True(await Host.OnWorldAsync(() => teleports.TeleportTo(Player, mapId, at.X, at.Y, at.Z, 0)), "teleport refused");
            string seen = "";
            try
            {
                await Host.WaitForWorldAsync(() =>
                {
                    Session.ManagedBudget = new ManagedActionBudget(4); // the worldport acknowledgement is a managed action
                    PlayerbotMovementControl.Update(Session, Player);
                    seen = $"in world {Player.IsInWorld} stage {teleports.StageOf(Player)} map {Player.Map?.MapId} ({Player.MapId}) at {Player.X},{Player.Y},{Player.Z}";
                    return Player.IsInWorld && teleports.StageOf(Player) is null && Player.Map?.MapId == mapId;
                }, $"the bot on map {mapId}");
            }
            catch (TimeoutException) { throw new Xunit.Sdk.XunitException("not placed: " + seen); }
            Vector2 stands = await Host.OnWorldAsync(() => new Vector2(Player.X, Player.Y));
            Assert.True(Vector2.Distance(stands, new(at.X, at.Y)) < 5f, $"the bot stands at {stands}, not at {at}");
        }

        public async ValueTask DisposeAsync()
        {
            Session.Kick();
            await Session.ManagedClosed;
            await Host.DisposeAsync();
        }

        private sealed class ManualClock : IWorldFeature
        {
            public void Attach(WorldRuntime world) => world.UseManualClock();
        }
    }
}

using System.Numerics;
using ArcaneCore.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
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
/// Quest goals by authentic travel (<see cref="PlayerbotWorldDestinations"/>, docs/areas/playbots.md, Travel): a quest goal on another map
/// (a completed quest's ender, a hunted creature) or across Teldrassil's water on this map begins a journey through the travel seam
/// (<see cref="IPlayerbotTravel.TryBegin"/>). A goal whose journey could not begin waits the transit backoff before the next try, doubling
/// while it keeps failing, and only that goal waits. With travel off no journey begins.
/// </summary>
public sealed class QuestGoalJourneyTests
{
    /// <summary>A travel seam that records every journey asked of it and begins one or refuses, as told (world thread only).</summary>
    private sealed class ScriptedTravel(bool begins) : IPlayerbotTravel
    {
        public List<(uint Map, Vector3 To)> Begun { get; } = [];

        public bool CanReach(WorldSession session, Player player, uint toMap, Vector3 to) => begins;

        public bool TryBegin(WorldSession session, Player player, uint toMap, Vector3 to, out string? why)
        {
            Begun.Add((toMap, to));
            why = begins ? null : "no route (scripted)";
            return begins;
        }

        // The journey is the brain's to drive; these tests ask only whether one was begun.
        public bool IsTraveling(Player player) => false;
    }

    private static readonly CreatureSpawn OtherMapEnder = new()
    {
        Guid = 900021, Entry = QuestInteractionFixture.RemoteEnder, MapId = 1, X = -441.8f, Y = -2596.08f, Z = 96.2f,
    };

    private static readonly CreatureSpawn TeldrassilEnder = new()
    {
        Guid = 900021, Entry = QuestInteractionFixture.RemoteEnder, MapId = 1, X = 9900f, Y = 2400f, Z = 1320f,
    };

    private static readonly Vector3 Darkshore = new(6450f, 800f, 20f);

    [Fact]
    public async Task ACompletedQuestsEnder_OnAnotherMap_BeginsAJourneyThere()
    {
        var travel = new ScriptedTravel(begins: true);
        await using JourneyHost host = await JourneyHost.StartAsync(OtherMapEnder, travel);
        await host.CompleteQuestAsync();
        var destinations = await host.OnWorldAsync(() => new PlayerbotWorldDestinations(host.Session, new PlayerbotOptions { Enabled = true }));

        Assert.True(await host.OnWorldAsync(() => destinations.Update(host.Player, 0, 100, QuestInteractionFixture.QuestId)), "no journey to the ender");
        Assert.Equal([(1u, new Vector3(OtherMapEnder.X, OtherMapEnder.Y, OtherMapEnder.Z))], travel.Begun);
        Assert.Equal(QuestInteractionFixture.RemoteEnder, destinations.TargetEntry);
        Assert.Equal(QuestInteractionFixture.QuestId, destinations.QuestId);
    }

    [Fact]
    public async Task WithTravelOff_AnEnderOnAnotherMap_BeginsNoJourney()
    {
        var travel = new ScriptedTravel(begins: true);
        await using JourneyHost host = await JourneyHost.StartAsync(OtherMapEnder, travel);
        await host.CompleteQuestAsync();
        var options = new PlayerbotOptions { Enabled = true };
        options.Travel.Enabled = false;
        var destinations = await host.OnWorldAsync(() => new PlayerbotWorldDestinations(host.Session, options));

        Assert.False(await host.OnWorldAsync(() => destinations.Update(host.Player, 0, 100, QuestInteractionFixture.QuestId)));
        Assert.Empty(travel.Begun);
    }

    /// <summary>
    /// A journey that cannot begin is not asked again at once: the goal waits the transit backoff (60 s), then twice that after the second
    /// failure. Another goal (the same creature hunted, not returned to) is not held by that wait.
    /// </summary>
    [Fact]
    public async Task AGoalWhoseJourneyCannotBegin_WaitsTheTransitBackoff_DoublingPerFailure_AndOnlyThatGoalWaits()
    {
        var travel = new ScriptedTravel(begins: false);
        await using JourneyHost host = await JourneyHost.StartAsync(OtherMapEnder, travel, manualClock: true);
        await host.CompleteQuestAsync();
        var options = new PlayerbotOptions { Enabled = true };
        var destinations = await host.OnWorldAsync(() => new PlayerbotWorldDestinations(host.Session, options));
        uint first = options.Travel.BackoffMs(1), second = options.Travel.BackoffMs(2);
        Assert.Equal(2 * first, second);
        Task<bool> Return() => host.OnWorldAsync(() => destinations.Update(host.Player, 0, 100, QuestInteractionFixture.QuestId));

        Assert.False(await Return());
        Assert.Single(travel.Begun);
        Assert.False(await Return());
        Assert.Single(travel.Begun); // waiting: not asked again

        // The same creature as a hunted goal is another goal: its journey is asked for at once.
        Assert.False(await host.OnWorldAsync(() => destinations.Update(host.Player, QuestInteractionFixture.RemoteEnder, 100)));
        Assert.Equal(2, travel.Begun.Count);

        await host.Host.World.AdvanceClockAsync(first - 1_000);
        Assert.False(await Return());
        Assert.Equal(2, travel.Begun.Count); // still waiting

        await host.Host.World.AdvanceClockAsync(2_000);
        Assert.False(await Return());
        Assert.Equal(3, travel.Begun.Count); // the first wait is over: asked again, and refused again

        await host.Host.World.AdvanceClockAsync(first + 1_000);
        Assert.False(await Return());
        Assert.Equal(3, travel.Begun.Count); // the second wait is twice the first

        await host.Host.World.AdvanceClockAsync(second - first);
        Assert.False(await Return());
        Assert.Equal(4, travel.Begun.Count);
    }

    /// <summary>The ender on Teldrassil, seen from Darkshore on the same map: across the water, a journey there.</summary>
    [Fact]
    public async Task ACompletedQuestsEnder_AcrossTeldrassilsWater_BeginsAJourneyThere()
    {
        var travel = new ScriptedTravel(begins: true);
        await using JourneyHost host = await JourneyHost.StartAsync(TeldrassilEnder, travel);
        await host.CompleteQuestAsync();
        await host.PlaceAsync(1, Darkshore);
        var destinations = await host.OnWorldAsync(() => new PlayerbotWorldDestinations(host.Session, new PlayerbotOptions { Enabled = true }));

        Assert.True(await host.OnWorldAsync(() => destinations.Update(host.Player, 0, 100, QuestInteractionFixture.QuestId)), "no journey to the ender");
        Assert.Equal([(1u, new Vector3(TeldrassilEnder.X, TeldrassilEnder.Y, TeldrassilEnder.Z))], travel.Begun);
        Assert.Equal(QuestInteractionFixture.RemoteEnder, destinations.TargetEntry);
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

        public static async Task<JourneyHost> StartAsync(CreatureSpawn ender, IPlayerbotTravel travel, bool manualClock = false)
        {
            var fixture = new QuestInteractionFixture { EnderSpawn = ender };
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

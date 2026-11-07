using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Bot QA #1487 (level 60 bots without a home stalled in the Valley of Trials): a new character is bound at its start position, so the
/// stall point lies inside <see cref="PlayerbotBrain.BindArrivedYards"/> of the bind point and the bind point holds only level 1 to 5
/// creatures; the hearthstone escape (bind point has creatures of the bot's level, far away) never applies. Such a bot is sent by the travel
/// seam to the nearest ground the explore atlas lists for its level. The rig stands the bot on map 0 (the world test host has no map 1);
/// only the shape matters: a level 60 bot, no home, a bind point beside it, level 3 creatures near it and level 60 creatures far off.
/// </summary>
public sealed class PlayerbotStallBarrenGroundTests
{
    private const uint LowMob = 960301, HighMob = 960302, FriendlyMob = 960303;
    private const float X = -8949.95f, Y = -132.49f, Z = 83.53f;

    private static readonly Vector3 NearestFit = new(X + 2000f, Y, Z);
    private static readonly Vector3 FriendlyFit = new(X + 1000f, Y, Z);
    private static readonly Vector3 FartherFit = new(X - 3500f, Y, Z);

    /// <summary>A travel seam that records every journey asked of it and begins one or refuses, as told (world thread only).</summary>
    private sealed class ScriptedTravel(bool begins) : IPlayerbotTravel
    {
        public List<(uint Map, Vector3 To)> Asked { get; } = [];

        public bool CanReach(WorldSession session, Player player, uint toMap, Vector3 to) => begins;

        public bool TryBegin(WorldSession session, Player player, uint toMap, Vector3 to, out string? why)
        {
            Asked.Add((toMap, to));
            why = begins ? null : "no route (scripted)";
            return begins;
        }

        public bool IsTraveling(Player player) => begins && Asked.Count > 0;
    }

    private sealed class TestContent(CreatureContent content) : ICreatureDataStore
    {
        public Task<CreatureContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(content);
    }

    private static CreatureContent Content(bool fitBesideTheBot, bool friendlyNearer)
    {
        var templates = new List<CreatureTemplate>
        {
            new() { Entry = LowMob, Name = "valley mob", MinLevel = 3, MaxLevel = 3, Faction = 14 },
            new() { Entry = HighMob, Name = "high mob", MinLevel = 60, MaxLevel = 60, Faction = 14 },
        };
        var spawns = new List<CreatureSpawn>
        {
            new() { Guid = 960311, Entry = LowMob, MapId = 0, X = X + 300f, Y = Y, Z = Z },
            new() { Guid = 960312, Entry = HighMob, MapId = 0, X = NearestFit.X, Y = NearestFit.Y, Z = NearestFit.Z },
            new() { Guid = 960313, Entry = HighMob, MapId = 0, X = FartherFit.X, Y = FartherFit.Y, Z = FartherFit.Z },
        };
        if (friendlyNearer)
        {
            templates.Add(new() { Entry = FriendlyMob, Name = "friendly mob", MinLevel = 60, MaxLevel = 60, Faction = 15 });
            spawns.Add(new() { Guid = 960315, Entry = FriendlyMob, MapId = 0, X = FriendlyFit.X, Y = FriendlyFit.Y, Z = FriendlyFit.Z });
        }
        if (fitBesideTheBot) spawns.Add(new() { Guid = 960314, Entry = HighMob, MapId = 0, X = X - 300f, Y = Y, Z = Z });
        return new CreatureContent(templates, spawns, [], [], []);
    }

    private static async Task<(WorldTestHost Host, WorldSession Session, PlayerbotBrain Brain, ScriptedTravel Travel)> StartAsync(
        bool travelBegins, bool fitBesideTheBot = false, bool homed = false, bool friendlyNearer = false)
    {
        var travel = new ScriptedTravel(travelBegins);
        WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<ICreatureDataStore>(new TestContent(Content(fitBesideTheBot, friendlyNearer)));
            services.AddSingleton(new FactionTemplateCatalog([
                new FactionTemplateRecord(1, 1, 0, 1, 0, 0),
                new FactionTemplateRecord(14, 0, 0, 8, 0, 1),
                new FactionTemplateRecord(15, 0, 0, 2, 1, 0)]));
            services.AddSingleton<IPlayerbotTravel>(travel);
        });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, Risk = { Enabled = false } });
        await host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            player.Level = 60;
            player.SetPosition(X, Y, Z, 0);
            // Bound at the start position, as a new character is (CharacterHandlers): 20 yards from the stall point.
            player.Home = new HomeBind(0, 12, X + 20f, Y, Z);
            if (homed) brain.Home = new PlayerbotHomeArea(0, new Vector3(X, Y, Z), 750f, "StallHome");
            return 0;
        });
        return (host, session, brain, travel);
    }

    private static async Task StopAsync(WorldTestHost host, WorldSession session, PlayerbotBrain brain)
    {
        brain.Stop();
        session.Kick();
        await session.ManagedClosed;
        await host.DisposeAsync();
    }

    /// <summary>
    /// The #1487 shape: bound beside the stall point, nothing of level 60 within three rings, a level 60 creature 2000 yards one way and another
    /// 3500 yards the other. The given-up Explore goal sends the bot on a journey to the nearer one, and it does not use the bind point.
    /// </summary>
    [Fact]
    public async Task GivenUpExplore_OfAnOverLevelledBotBoundBesideIt_TravelsToTheNearestGroundForItsLevel()
    {
        (WorldTestHost host, WorldSession session, PlayerbotBrain brain, ScriptedTravel travel) = await StartAsync(travelBegins: true);
        try
        {
            await host.OnWorldAsync(() =>
            {
                brain.HoldGoalForTests(PlayerbotGoalKind.Explore, 0, 0);
                brain.GiveUpStalledGoal(session.Player!, shareable: false);
                (uint map, Vector3 to) = Assert.Single(travel.Asked);
                Assert.Equal(0u, map);
                Assert.True(Vector3.Distance(NearestFit, to) < 1f, $"travelled to {to}, not to the nearest ground for its level {NearestFit}");
                Assert.False(brain.BindTeleported);
                return 0;
            });
        }
        finally { await StopAsync(host, session, brain); }
    }

    /// <summary>
    /// The journey applies the explore search's eligibility: a level 60 cell 1000 yards away whose creatures are friendly to the bot (faction
    /// it cannot attack) is nearer than the 2000-yard hostile one, and is passed over for it.
    /// </summary>
    [Fact]
    public async Task GivenUpExplore_WhereTheNearestCellHoldsOnlyCreaturesTheBotCannotAttack_TravelsToTheNextCell()
    {
        (WorldTestHost host, WorldSession session, PlayerbotBrain brain, ScriptedTravel travel) = await StartAsync(travelBegins: true, friendlyNearer: true);
        try
        {
            await host.OnWorldAsync(() =>
            {
                brain.HoldGoalForTests(PlayerbotGoalKind.Explore, 0, 0);
                brain.GiveUpStalledGoal(session.Player!, shareable: false);
                (uint map, Vector3 to) = Assert.Single(travel.Asked);
                Assert.Equal(0u, map);
                Assert.True(Vector3.Distance(NearestFit, to) < 1f, $"travelled to {to}, not to the nearest attackable ground {NearestFit} (friendly cell at {FriendlyFit})");
                return 0;
            });
        }
        finally { await StopAsync(host, session, brain); }
    }

    /// <summary>The Vendor/0 errand of #1487 given up leaves the same way.</summary>
    [Fact]
    public async Task GivenUpVendorErrand_OfAnOverLevelledBotBoundBesideIt_TravelsToGroundForItsLevel()
    {
        (WorldTestHost host, WorldSession session, PlayerbotBrain brain, ScriptedTravel travel) = await StartAsync(travelBegins: true);
        try
        {
            await host.OnWorldAsync(() =>
            {
                brain.HoldGoalForTests(PlayerbotGoalKind.Vendor, 0, 0);
                brain.GiveUpStalledGoal(session.Player!, shareable: false);
                Assert.NotEqual(PlayerbotGoalKind.Vendor, brain.Goal);
                Assert.Single(travel.Asked);
                return 0;
            });
        }
        finally { await StopAsync(host, session, brain); }
    }

    /// <summary>Control: a creature of the bot's level 300 yards away means the area fits it, and the give-up sends it nowhere.</summary>
    [Fact]
    public async Task GivenUpExplore_WhereCreaturesOfTheBotsLevelAreNear_BeginsNoJourney()
    {
        (WorldTestHost host, WorldSession session, PlayerbotBrain brain, ScriptedTravel travel) = await StartAsync(travelBegins: true, fitBesideTheBot: true);
        try
        {
            await host.OnWorldAsync(() =>
            {
                brain.HoldGoalForTests(PlayerbotGoalKind.Explore, 0, 0);
                brain.GiveUpStalledGoal(session.Player!, shareable: false);
                Assert.Empty(travel.Asked);
                return 0;
            });
        }
        finally { await StopAsync(host, session, brain); }
    }

    /// <summary>Control: a bot with a home is the home's to keep, not sent travelling.</summary>
    [Fact]
    public async Task GivenUpExplore_OfAHomedBot_BeginsNoJourney()
    {
        (WorldTestHost host, WorldSession session, PlayerbotBrain brain, ScriptedTravel travel) = await StartAsync(travelBegins: true, homed: true);
        try
        {
            await host.OnWorldAsync(() =>
            {
                brain.HoldGoalForTests(PlayerbotGoalKind.Explore, 0, 0);
                brain.GiveUpStalledGoal(session.Player!, shareable: false);
                Assert.Empty(travel.Asked);
                return 0;
            });
        }
        finally { await StopAsync(host, session, brain); }
    }

    /// <summary>
    /// A journey the planner refuses leaves the bot where it is, sets the refused cell aside and waits the transit backoff: a second give-up in
    /// the same moment asks nothing more.
    /// </summary>
    [Fact]
    public async Task ARefusedJourney_IsNotAskedAgainBeforeTheBackoffEnds_AndSetsTheRefusedCellAside()
    {
        (WorldTestHost host, WorldSession session, PlayerbotBrain brain, ScriptedTravel travel) = await StartAsync(travelBegins: false);
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                brain.HoldGoalForTests(PlayerbotGoalKind.Explore, 0, 0);
                brain.GiveUpStalledGoal(player, shareable: false);
                Assert.Single(travel.Asked);
                Assert.True(brain.IsExploreCellBlocked(0, NearestFit.X, NearestFit.Y), "the refused cell was not set aside");
                brain.HoldGoalForTests(PlayerbotGoalKind.Explore, 0, 0);
                brain.GiveUpStalledGoal(player, shareable: false);
                Assert.Single(travel.Asked);
                return 0;
            });
        }
        finally { await StopAsync(host, session, brain); }
    }
}

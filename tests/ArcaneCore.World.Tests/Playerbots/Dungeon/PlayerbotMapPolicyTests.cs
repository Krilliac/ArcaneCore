using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Dungeon;

/// <summary>
/// <see cref="PlayerbotMapPolicy"/>: AllowedMaps limits open-world travel; the dungeon or raid instance a bot is in is always
/// walkable; a map the registry does not know gets the AllowedMaps answer alone.
/// </summary>
public sealed class PlayerbotMapPolicyTests
{
    private static readonly MapTemplate Continent = new(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", "");
    private static readonly MapTemplate Dungeon = new(36, 0, MapType.Instance, 1581, 10, 0, 0, -11208.4f, 1672.3f, "The Deadmines", "");
    private static readonly MapTemplate Raid = new(409, 230, MapType.Raid, 2717, 40, 0, 230, 0, 0, "Molten Core", "");
    private static readonly MapTemplate Battleground = new(489, 0, MapType.Battleground, 3277, 20, 0, -1, 0, 0, "Warsong Gulch", "");

    public static TheoryData<uint[], uint, MapTemplate?, bool, bool> Matrix => new()
    {
        // A continent in or out of AllowedMaps.
        { [0, 1], 0, Continent, true, true },
        { [1], 0, Continent, true, false },
        // The dungeon or raid the bot is in, not listed.
        { [0, 1], 36, Dungeon, true, true },
        { [0, 1], 409, Raid, true, true },
        // A dungeon that is not the bot's current map is open-world travel: AllowedMaps decides.
        { [0, 1], 36, Dungeon, false, false },
        { [0, 1, 36], 36, Dungeon, false, true },
        // A battleground is not a dungeon (its own systems decide who is there).
        { [0, 1], 489, Battleground, true, false },
        // A map the registry does not know: AllowedMaps alone, as before.
        { [0, 1], 36, null, true, false },
        { [0, 1, 36], 36, null, true, true },
        // An empty list allows every map (PlayerbotNavigation's rule).
        { [], 36, null, true, true },
        // A template for another map id never counts.
        { [0, 1], 37, Dungeon, true, false },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Allows(uint[] allowed, uint mapId, MapTemplate? template, bool current, bool expected)
        => Assert.Equal(expected, PlayerbotMapPolicy.Allows(allowed, mapId, template, current));

    public static TheoryData<uint[]?, uint?, bool, bool, bool> Triggers => new()
    {
        // allowed maps, teleport target (null: none), controller opted in, ghost's body behind it -> reported
        { [0, 1], null, false, false, true },   // a tavern or quest exploration trigger
        { [0, 1], 0u, false, false, true },     // a teleport within the allowed continents
        { [0, 1], 1u, false, false, true },
        { [0, 1], 369u, false, false, false },  // the Deeprun Tram: neither listed nor a dungeon
        { [0, 1], 36u, false, false, false },   // a dungeon entrance
        { [0, 1, 36], 36u, false, false, true },
        { [0, 1], 36u, true, false, true },     // a controller brings the bot back
        { [0, 1], 36u, false, true, true },     // a ghost walking to its body
        { [], 0u, false, false, false },        // the login gate is AllowedMaps.Contains: an empty list admits nothing
        { null, 0u, false, false, false },      // no route started yet: no list known
    };

    [Theory]
    [MemberData(nameof(Triggers))]
    public void AllowsTrigger(uint[]? allowed, uint? target, bool optedIn, bool leadsToCorpse, bool expected)
        => Assert.Equal(expected, PlayerbotMapPolicy.AllowsTrigger(allowed, target, optedIn, leadsToCorpse));

    [Theory]
    [InlineData(36u, 36u, true)]    // the body's own dungeon
    [InlineData(409u, 230u, true)]  // Molten Core is nested in Blackrock Depths
    [InlineData(409u, 409u, true)]
    [InlineData(230u, 409u, false)] // not the other way round
    [InlineData(36u, 0u, false)]    // a parent of 0 is none (GhostEntryRules)
    [InlineData(0u, 0u, true)]      // a body on the continent the teleport leads to
    [InlineData(0u, 36u, false)]
    [InlineData(999u, 999u, true)]  // unknown to the registry: only the map itself
    [InlineData(999u, 0u, false)]
    public void LeadsTo(uint corpseMap, uint target, bool expected)
    {
        var registry = new MapRegistry(
        [
            Dungeon, Raid,
            new MapTemplate(230, 0, MapType.Instance, 1584, 5, 0, 0, 0, 0, "Blackrock Depths", ""),
        ]);
        Assert.Equal(expected, PlayerbotMapPolicy.LeadsTo(registry, corpseMap, target));
    }

    /// <summary>
    /// A bot standing in The Deadmines instance with the default AllowedMaps [0, 1] plans and follows a route there, and may stay
    /// there. Before, TryPlan refused every map outside AllowedMaps.
    /// </summary>
    [Fact]
    public async Task ABotInTheDeadminesInstance_PlansAndAdvances_WithTheDefaultAllowedMaps()
    {
        await using DungeonBotHost host = await DungeonBotHost.StartAsync();
        await host.TeleportAsync(DungeonEntryScenario.Deadmines, -16.4f, -383.07f, 61.78f, 1.9f);
        var options = new PlayerbotOptions { Enabled = true };
        Assert.Equal([0u, 1u], options.AllowedMaps);

        await host.OnWorldAsync(() =>
        {
            Assert.NotEqual(0u, host.Player.Map!.InstanceId);
            Assert.True(PlayerbotMapPolicy.MayMoveOn(host.Player, options));
            Assert.True(PlayerbotMapPolicy.MayStayOnMap(host.Player, options));
            Vector3 destination = new(-16.4f, -363.07f, 61.78f);
            Assert.True(PlayerbotNavigation.TryPlan(host.Player, destination, options, out PlayerbotRoute? route));
            host.Session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(PlayerbotNavigation.TryAdvance(host.Session, route!, options, 500, host.Host.World.NowMs));
            Assert.True(PlayerbotMotion.IsActive(host.Player));
            PlayerbotMovementControl.Stop(host.Session, host.Player);
        });
    }

    [Fact]
    public async Task ABotOnAContinentOutsideAllowedMaps_StillCannotPlanOrStay()
    {
        await using DungeonBotHost host = await DungeonBotHost.StartAsync();
        var options = new PlayerbotOptions { Enabled = true, AllowedMaps = [1] };

        await host.OnWorldAsync(() =>
        {
            Assert.Equal(0u, host.Player.MapId);
            Assert.False(PlayerbotMapPolicy.MayStayOnMap(host.Player, options));
            Vector3 destination = new(host.Player.X + 10, host.Player.Y, host.Player.Z);
            Assert.False(PlayerbotNavigation.TryPlan(host.Player, destination, options, out _));
            Assert.True(PlayerbotNavigation.TryPlan(host.Player, destination, new PlayerbotOptions { Enabled = true }, out _));
        });
    }
}

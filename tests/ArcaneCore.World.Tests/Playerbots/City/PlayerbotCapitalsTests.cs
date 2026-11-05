using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.City;

/// <summary>
/// <see cref="PlayerbotCapitals"/>: the background build, its result taken on the world thread, a failed build, and the discard of a build
/// that a newer one superseded. The world is stepped by hand (<see cref="WorldRuntime.RunTick(uint)"/> runs what the build posted).
/// Unproven until the integration gate.
/// </summary>
public sealed class PlayerbotCapitalsTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private static WorldRuntime NewWorld()
        => new(new WorldRuntimeOptions { AutosaveIntervalMs = 0, CommandTimeBudgetMs = 0 }, new NullSaveQueue(), NullLogger<WorldRuntime>.Instance);

    private static PlayerbotCapitals.Sources Sources()
    {
        AreaTemplate[] areas = [new(1519, 0, 0, 100, (uint)AreaFlags.Capital, 0, "Stormwind City", AreaTeams.Ally, 0)];
        var registry = new MapRegistry([new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", "")]);
        CreatureTemplate[] templates =
        [
            new() { Entry = 1, Name = "Auctioneer", Faction = 11, NpcFlags = (uint)NpcFlags.Auctioneer, DisplayIds = [49] },
            new() { Entry = 2, Name = "Banker", Faction = 11, NpcFlags = (uint)NpcFlags.Banker, DisplayIds = [49] },
        ];
        CreatureSpawn[] spawns =
        [
            new() { Guid = 1, Entry = 1, MapId = 0, X = -8800, Y = 600, Z = 10 },
            new() { Guid = 2, Entry = 2, MapId = 0, X = -8900, Y = 620, Z = 10 },
        ];
        var content = new CreatureContent(templates, spawns, [], [], []);
        return new PlayerbotCapitals.Sources(content, content.DefinitionsVersion, new AreaTable(areas, registry), null, null);
    }

    private static void Tick(WorldRuntime world) => world.RunTick(50);

    private static void TickUntil(WorldRuntime world, Func<bool> done)
    {
        for (DateTime deadline = DateTime.UtcNow + Wait; !done() && DateTime.UtcNow < deadline;)
        {
            Tick(world);
            Thread.Sleep(10);
        }

        Assert.True(done(), "the condition was not reached in time");
    }

    private static (uint, uint) InStormwind(uint map, float x, float y, float z) => (1519u, 1519u);

    [Fact]
    public void Build_RunsInTheBackground_AndItsCatalogIsTakenOnTheWorldThread()
    {
        using WorldRuntime world = NewWorld();
        PlayerbotCapitals capitals = PlayerbotCapitals.Of(world);
        capitals.Locator = InStormwind;
        Assert.False(capitals.Building);

        capitals.Start(world, null, Sources(), new TerrainManager(null), null);
        Assert.True(capitals.Building);
        Assert.Null(capitals.Built); // nothing is taken before the world thread runs the posted result

        TickUntil(world, () => capitals.Built is not null);
        Assert.False(capitals.Building);
        Assert.Equal([1519u], capitals.Built!.Capitals.Select(c => c.ZoneId));
    }

    [Fact]
    public void Build_AFailingLocator_LeavesNoCatalog_AndEndsTheBuild()
    {
        using WorldRuntime world = NewWorld();
        PlayerbotCapitals capitals = PlayerbotCapitals.Of(world);
        capitals.Locator = (_, _, _, _) => throw new InvalidDataException("bad tile");

        capitals.Start(world, null, Sources(), new TerrainManager(null), null);
        TickUntil(world, () => !capitals.Building);
        Assert.Null(capitals.Built);
    }

    [Fact]
    public void Build_AStaleBuildThatFinishesAfterANewerOne_IsDiscarded()
    {
        using WorldRuntime world = NewWorld();
        PlayerbotCapitals capitals = PlayerbotCapitals.Of(world);
        using var release = new ManualResetEventSlim(false);
        using var firstAsked = new ManualResetEventSlim(false);

        // The first build is held in its first lookup and, once released, finds no capital (an empty catalog).
        capitals.Locator = (_, _, _, _) =>
        {
            firstAsked.Set();
            Assert.True(release.Wait(Wait));
            return (0u, 0u);
        };
        capitals.Start(world, null, Sources(), new TerrainManager(null), null);
        Assert.True(firstAsked.Wait(Wait));

        // The second build supersedes it and finds the capital.
        capitals.Locator = InStormwind;
        capitals.Start(world, null, Sources(), new TerrainManager(null), null);
        TickUntil(world, () => capitals.Built is not null);
        PlayerbotCapitalCatalog second = capitals.Built!;
        Assert.Single(second.Capitals);

        // Let the first finish and post its (empty) result; the world must keep the second.
        release.Set();
        for (int i = 0; i < 40; i++)
        {
            Tick(world);
            Thread.Sleep(10);
        }

        Assert.Same(second, capitals.Built);
        Assert.False(capitals.Building);
    }

    [Fact]
    public void Of_IsOneInstancePerWorld_AndStartsWithoutABuild()
    {
        using WorldRuntime world = NewWorld();
        PlayerbotCapitals capitals = PlayerbotCapitals.Of(world);
        Assert.Same(capitals, PlayerbotCapitals.Of(world)); // one instance per world
        Assert.False(capitals.Building);
        Assert.Null(capitals.Built);
    }
}

using System.Numerics;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Bots;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The explore atlas find in slices (<see cref="PlayerbotExploreAtlas.FindStep"/>): each slice yields within its budget, the next one
/// resumes where the last stopped, and the answer is the one the unbounded <see cref="PlayerbotExploreAtlas.Find"/> gives (audit cid 43:
/// the unbounded find took 635 ms in one think). The dead-end pin's reasons (audit cid 44) are pinned here too.
/// </summary>
public sealed class PlayerbotExploreBudgetTests
{
    private static readonly Vector3 Origin = new(0, 0, 20);

    private static CreatureTemplate Template(uint entry) => new()
    { Entry = entry, Name = $"creature {entry}", MinLevel = 8, MaxLevel = 8, Faction = 14 };

    /// <summary>One spawn in each of <paramref name="cells"/> explore cells in a row east of the origin (one occupied cell per ring).</summary>
    private static PlayerbotExploreAtlas RowAtlas(int cells)
    {
        var spawns = new List<CreatureSpawn>();
        for (int i = 0; i < cells; i++)
            spawns.Add(new CreatureSpawn { Guid = (uint)(i + 1), Entry = 1, MapId = 0, X = (PlayerbotExploreAtlas.CellYards * i) + 80f, Y = 0, Z = 20 });
        return new PlayerbotExploreAtlas(new CreatureContent([Template(1)], [.. spawns], [], [], []));
    }

    [Fact]
    public void ASliceExaminesAtMostItsBudget_AndTheNextSliceResumes()
    {
        PlayerbotExploreAtlas atlas = RowAtlas(12);
        var cursor = new PlayerbotExploreAtlas.FindCursor();
        int slices = 0, total = 0;
        PlayerbotExploreAtlas.FindStatus status;
        PlayerbotExploreAtlas.Candidate? found;
        do
        {
            slices++;
            status = atlas.FindStep(cursor, 0, 8, Origin, c => c.Spawn.Guid == 9, (_, _) => false, 2, null, out found, out int examined);
            Assert.True(examined <= 2, $"slice {slices} examined {examined} spawns (budget 2, one spawn per cell)");
            total += examined;
            Assert.True(slices < 100, "the search never finished");
        }
        while (status == PlayerbotExploreAtlas.FindStatus.Pending);

        Assert.Equal(PlayerbotExploreAtlas.FindStatus.Found, status);
        Assert.Equal(9u, found!.Value.Spawn.Guid);
        Assert.True(slices > 1, "a budget of 2 spawns must split a search that visits 9 occupied cells");

        // Resuming repeats no work: the slices together examine what the unbounded search examines.
        PlayerbotExploreAtlas.Candidate? whole = atlas.Find(0, 8, Origin, c => c.Spawn.Guid == 9, (_, _) => false, out int wholeExamined);
        Assert.Equal(9u, whole!.Value.Spawn.Guid);
        Assert.Equal(wholeExamined, total);
    }

    [Fact]
    public void ASliceAnswersWhatTheUnboundedFindAnswers()
    {
        PlayerbotExploreAtlas atlas = RowAtlas(12);
        // Everything eligible: the nearest spawn.
        PlayerbotExploreAtlas.Candidate? whole = atlas.Find(0, 8, Origin, _ => true, (_, _) => false, out _);
        var cursor = new PlayerbotExploreAtlas.FindCursor();
        PlayerbotExploreAtlas.Candidate? found;
        PlayerbotExploreAtlas.FindStatus status;
        int guard = 0;
        do { status = atlas.FindStep(cursor, 0, 8, Origin, _ => true, (_, _) => false, 1, null, out found, out _); }
        while (status == PlayerbotExploreAtlas.FindStatus.Pending && ++guard < 100);
        Assert.Equal(PlayerbotExploreAtlas.FindStatus.Found, status);
        Assert.Equal(whole!.Value.Spawn.Guid, found!.Value.Spawn.Guid);
    }

    [Fact]
    public void AnExhaustedSearch_ReportsExhaustedAfterResuming()
    {
        PlayerbotExploreAtlas atlas = RowAtlas(5);
        var cursor = new PlayerbotExploreAtlas.FindCursor();
        PlayerbotExploreAtlas.FindStatus status;
        int guard = 0;
        do { status = atlas.FindStep(cursor, 0, 8, Origin, _ => false, (_, _) => false, 1, null, out _, out _); }
        while (status == PlayerbotExploreAtlas.FindStatus.Pending && ++guard < 100);
        Assert.Equal(PlayerbotExploreAtlas.FindStatus.Exhausted, status);
        Assert.Null(atlas.Find(0, 8, Origin, _ => false, (_, _) => false, out _));
    }

    [Fact]
    public void ATimeBudgetThatIsAlwaysSpent_StillMakesProgressAndFinishes()
    {
        PlayerbotExploreAtlas atlas = RowAtlas(8);
        var cursor = new PlayerbotExploreAtlas.FindCursor();
        PlayerbotExploreAtlas.FindStatus status;
        PlayerbotExploreAtlas.Candidate? found;
        int slices = 0;
        do
        {
            slices++;
            status = atlas.FindStep(cursor, 0, 8, Origin, c => c.Spawn.Guid == 4, (_, _) => false, int.MaxValue, () => true, out found, out int examined);
            Assert.True(examined <= 1, "a spent clock ends the slice after the occupied cell it is in");
            Assert.True(slices < 100, "the search never finished");
        }
        while (status == PlayerbotExploreAtlas.FindStatus.Pending);
        Assert.Equal(4u, found!.Value.Spawn.Guid);
        Assert.True(slices >= 4);
    }

    [Fact]
    public void ACellBlockedBetweenSlices_IsSkippedWhenTheSearchReachesIt()
    {
        PlayerbotExploreAtlas atlas = RowAtlas(6);
        var cursor = new PlayerbotExploreAtlas.FindCursor();
        var blockedCells = new HashSet<int>();
        PlayerbotExploreAtlas.FindStatus first = atlas.FindStep(cursor, 0, 8, Origin, _ => true, (x, _) => blockedCells.Contains(x), 1, null, out _, out _);
        Assert.Equal(PlayerbotExploreAtlas.FindStatus.Pending, first);
        blockedCells.Add(1); // the cell the next slice would reach: now set aside
        PlayerbotExploreAtlas.FindStatus status;
        PlayerbotExploreAtlas.Candidate? found;
        int guard = 0;
        do { status = atlas.FindStep(cursor, 0, 8, Origin, _ => true, (x, _) => blockedCells.Contains(x), 1, null, out found, out _); }
        while (status == PlayerbotExploreAtlas.FindStatus.Pending && ++guard < 100);
        Assert.Equal(PlayerbotExploreAtlas.FindStatus.Found, status);
        // The first slice already took cell 0's spawn as the ring's best; later spawns are farther, and the blocked cell is never searched.
        Assert.Equal(1u, found!.Value.Spawn.Guid);
    }

    [Fact]
    public void ASearchKeepsTheMapLevelAndPointOfItsFirstSlice()
    {
        PlayerbotExploreAtlas atlas = RowAtlas(4);
        var cursor = new PlayerbotExploreAtlas.FindCursor();
        atlas.FindStep(cursor, 0, 8, Origin, _ => false, (_, _) => false, 1, null, out _, out _);
        Assert.Equal(0u, cursor.SearchMap);
        Assert.Equal(8u, cursor.SearchLevel);
        Assert.Equal(Origin, cursor.SearchFrom);
    }

    [Fact]
    public void TheBrainsSliceBudgetsAreBounded()
    {
        Assert.InRange(PlayerbotBrain.ExploreFindSliceChecks, 1, 256);
        Assert.InRange(PlayerbotBrain.ExploreFindSliceMs, 0.5, 8.0);
    }

    private const uint OpenZone = 12;

    private static (MapTemplate Template, AreaTable Areas) Areas(MapType type = MapType.Common)
    {
        var template = new MapTemplate(0, 0, type, 0, 0, 0, -1, 0, 0, "map", "");
        var areas = new AreaTable([
            new AreaTemplate(OpenZone, 0, 0, 12, 0, 1, "open", 0, 0),
            new AreaTemplate(1519, 0, 0, 1519, (uint)ArcaneCore.Game.WorldState.Zones.AreaFlags.Capital, 1, "capital", 0, 0),
            new AreaTemplate(88, 0, OpenZone, 88, (uint)ArcaneCore.Game.WorldState.Zones.AreaFlags.City, 1, "town", 0, 0)], new MapRegistry([template]));
        return (template, areas);
    }

    [Fact]
    public void DeadEndPinAreaBlock_NamesTheRuleThatRefusedTheShare()
    {
        (MapTemplate template, AreaTable areas) = Areas();
        Assert.Null(PlayerbotBrain.DeadEndPinAreaBlock(template, areas, OpenZone, 0));
        Assert.Equal("no map template", PlayerbotBrain.DeadEndPinAreaBlock(null, areas, OpenZone, 0));
        Assert.Equal("no zone", PlayerbotBrain.DeadEndPinAreaBlock(template, areas, 0, 0));
        Assert.Equal("zone missing from the area table", PlayerbotBrain.DeadEndPinAreaBlock(template, areas, 999_999, 0));
        Assert.Equal("zone flagged as a capital or city", PlayerbotBrain.DeadEndPinAreaBlock(template, areas, 1519, 0));
        Assert.Equal("sub-area flagged as a capital, city or town", PlayerbotBrain.DeadEndPinAreaBlock(template, areas, OpenZone, 88));
        (MapTemplate battleground, AreaTable bgAreas) = Areas(MapType.Battleground);
        Assert.Equal("instanced map", PlayerbotBrain.DeadEndPinAreaBlock(battleground, bgAreas, OpenZone, 0));
    }

    [Fact]
    public void DeadEndPinAreaBlock_AgreesWithCanShareDeadEndPin()
    {
        (MapTemplate template, AreaTable areas) = Areas();
        foreach ((uint zone, uint area) in new[] { (OpenZone, 0u), (OpenZone, 88u), (1519u, 0u), (0u, 0u), (999_999u, 0u) })
            Assert.Equal(PlayerbotBrain.DeadEndPinAreaBlock(template, areas, zone, area) is null,
                PlayerbotBrain.CanShareDeadEndPin(template, areas, zone, area));
    }
}

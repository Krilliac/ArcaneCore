using ArcaneCore.Game.Graveyards;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.WorldData;
using Xunit;

namespace ArcaneCore.Game.Tests.Graveyards;

/// <summary>
/// The graveyard choice (vmangos ObjectMgr::GetClosestGraveYard and GetClosestGraveYardForArea, ObjectMgr.cpp:7512-7645) over
/// synthetic catalogs; nothing here depends on a real database or on D:\refs.
/// </summary>
public sealed class GraveyardSelectorTests
{
    private const uint Alliance = GraveyardCatalog.TeamAlliance;
    private const uint Horde = GraveyardCatalog.TeamHorde;

    // Map 0 and 1 are continents; 33 is a dungeon whose entrance is on map 0 at (-230, 1570); 34 has no entrance data.
    private static readonly MapRegistry Maps = new(
    [
        new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
        new MapTemplate(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Kalimdor", ""),
        new MapTemplate(33, 0, MapType.Instance, 0, 10, 0, 0, -230f, 1570f, "Shadowfang Keep", ""),
        new MapTemplate(34, 0, MapType.Instance, 0, 10, 0, -1, 0, 0, "Stockade", ""),
    ]);

    private static WorldSafeLoc Loc(uint id, uint map, float x, float y, float z) => new(id, map, x, y, z, 0f, "loc" + id);

    private static GraveyardCatalog Catalog(WorldSafeLoc[] locs, params (uint Loc, uint Zone, uint Team)[] links)
        => GraveyardCatalog.Build(new GraveyardContent(locs, [.. links.Select(l => new GraveyardLink(l.Loc, l.Zone, l.Team))]), null, out _);

    private static uint? Pick(GraveyardCatalog catalog, uint map, float x, float y, float z, uint zone, uint area, uint team)
        => GraveyardSelector.FindClosest(catalog, Maps, map, x, y, z, zone, area, team)?.Id;

    [Fact]
    public void AnAreaLink_BeatsTheZoneLink_EvenWhenTheZoneGraveyardIsNearer()
    {
        GraveyardCatalog catalog = Catalog([Loc(1, 0, 0, 0, 0), Loc(2, 0, 500, 0, 0)], (1, 10, 0), (2, 12, 0));

        Assert.Equal(2u, Pick(catalog, 0, 0, 0, 0, zone: 10, area: 12, team: Alliance));
    }

    [Fact]
    public void WithoutAnAreaLink_TheZoneIsUsed_ButOnlyWhenTheAreaIsNotTheZoneItself()
    {
        GraveyardCatalog catalog = Catalog([Loc(1, 0, 0, 0, 0)], (1, 10, 0));

        Assert.Equal(1u, Pick(catalog, 0, 5, 5, 0, zone: 10, area: 12, team: Alliance));
        Assert.Equal(1u, Pick(catalog, 0, 5, 5, 0, zone: 10, area: 10, team: Alliance));
        Assert.Null(Pick(catalog, 0, 5, 5, 0, zone: 11, area: 11, team: Alliance));
    }

    [Fact]
    public void OnTheSameMap_TheNearestByThreeDimensionalDistanceWins()
    {
        // From the origin the first graveyard is 100 up and the second 50 away on the ground: 2D picks the first, 3D the second.
        GraveyardCatalog catalog = Catalog([Loc(1, 0, 0, 0, 100), Loc(2, 0, 50, 0, 0)], (1, 10, 0), (2, 10, 0));

        Assert.Equal(2u, Pick(catalog, 0, 0, 0, 0, zone: 10, area: 10, team: Alliance));
    }

    [Fact]
    public void AnEqualDistance_KeepsTheFirstLink()
    {
        GraveyardCatalog catalog = Catalog([Loc(1, 0, 10, 0, 0), Loc(2, 0, -10, 0, 0)], (1, 10, 0), (2, 10, 0));

        Assert.Equal(1u, Pick(catalog, 0, 0, 0, 0, zone: 10, area: 10, team: Alliance));
    }

    [Fact]
    public void TheEnemyFactionsGraveyard_IsSkipped_AndTeamZeroMatchesEverything()
    {
        GraveyardCatalog catalog = Catalog([Loc(1, 0, 0, 0, 0), Loc(2, 0, 100, 0, 0)], (1, 10, Horde), (2, 10, Alliance));

        Assert.Equal(2u, Pick(catalog, 0, 0, 0, 0, 10, 10, Alliance)); // the nearer one is the Horde's
        Assert.Equal(1u, Pick(catalog, 0, 0, 0, 0, 10, 10, Horde));
        Assert.Equal(1u, Pick(catalog, 0, 0, 0, 0, 10, 10, team: 0));  // .neargrave without a team
    }

    [Fact]
    public void ALinkForBothTeams_ServesTheEnemyToo()
    {
        GraveyardCatalog catalog = Catalog([Loc(1, 0, 0, 0, 0)], (1, 10, 0));

        Assert.Equal(1u, Pick(catalog, 0, 0, 0, 0, 10, 10, Alliance));
        Assert.Equal(1u, Pick(catalog, 0, 0, 0, 0, 10, 10, Horde));
    }

    [Fact]
    public void AnAreaWhoseLinksAreAllEnemy_FallsThroughToTheZone()
    {
        GraveyardCatalog catalog = Catalog([Loc(1, 0, 0, 0, 0), Loc(2, 0, 900, 0, 0)], (1, 12, Horde), (2, 10, Alliance));

        Assert.Equal(2u, Pick(catalog, 0, 0, 0, 0, zone: 10, area: 12, team: Alliance));
    }

    [Fact]
    public void OnlyTheEnemyHasAGraveyardInTheZone_NoGraveyardForUs()
    {
        // 17 of the 100 zones of classic-db are like this; vmangos returns null and the ghost stays where it is.
        GraveyardCatalog catalog = Catalog([Loc(1, 0, 0, 0, 0)], (1, 10, Horde));

        Assert.Null(Pick(catalog, 0, 0, 0, 0, 10, 10, Alliance));
    }

    [Fact]
    public void InADungeon_TheGraveyardNearestTheEntrance_OnTheEntranceMap_IsChosen_Measured2D()
    {
        // The corpse position inside the dungeon is irrelevant; the entrance is on map 0 at (-230, 1570).
        GraveyardCatalog catalog = Catalog(
            [Loc(1, 0, 0, 0, 0), Loc(2, 0, -240, 1560, 0), Loc(3, 1, -230, 1570, 0)],
            (1, 100, 0), (2, 100, 0), (3, 100, 0));

        Assert.Equal(2u, Pick(catalog, 33, 7, 7, 7, zone: 100, area: 100, team: Alliance));
    }

    [Fact]
    public void AGraveyardOnTheCorpsesOwnMap_BeatsOneNearTheEntrance_AndBothBeatTheFarOnes()
    {
        GraveyardCatalog catalog = Catalog([Loc(1, 33, 1000, 0, 0), Loc(2, 0, -230, 1570, 0), Loc(3, 1, 0, 0, 0)], (1, 100, 0), (2, 100, 0), (3, 100, 0));

        Assert.Equal(1u, Pick(catalog, 33, 0, 0, 0, 100, 100, Alliance));
        Assert.Equal(2u, Pick(Catalog([Loc(2, 0, -230, 1570, 0), Loc(3, 1, 0, 0, 0)], (2, 100, 0), (3, 100, 0)), 33, 0, 0, 0, 100, 100, Alliance));
    }

    [Fact]
    public void InADungeonWithoutEntranceData_TheLastGraveyardSeen_IsUsed()
    {
        GraveyardCatalog catalog = Catalog([Loc(1, 0, 0, 0, 0), Loc(2, 1, 5, 5, 5)], (1, 100, 0), (2, 100, 0));

        Assert.Equal(2u, Pick(catalog, 34, 0, 0, 0, 100, 100, Alliance));
    }

    [Fact]
    public void AGraveyardOnAnotherMapThanTheEntranceMap_IsOnlyTheLastResort()
    {
        // Map 33's entrance is on map 0; the only link is on map 1, so it cannot be measured and is used as the last resort.
        GraveyardCatalog catalog = Catalog([Loc(3, 1, 9, 9, 9)], (3, 100, 0));

        Assert.Equal(3u, Pick(catalog, 33, 0, 0, 0, 100, 100, Alliance));
    }

    [Fact]
    public void WithoutALink_NothingIsFound_UnlessTheDefaultsAreAskedFor()
    {
        GraveyardCatalog catalog = Catalog(
            [Loc(GraveyardCatalog.DefaultAllianceGraveyard, 0, 1, 1, 1), Loc(GraveyardCatalog.DefaultHordeGraveyard, 1, 2, 2, 2)]);

        Assert.Null(Pick(catalog, 0, 0, 0, 0, 10, 10, Alliance));
        Assert.Equal(4u, GraveyardSelector.FindClosestOrDefault(catalog, Maps, 0, 0, 0, 0, 10, 10, Alliance)!.Id);
        Assert.Equal(10u, GraveyardSelector.FindClosestOrDefault(catalog, Maps, 0, 0, 0, 0, 10, 10, Horde)!.Id);
    }

    [Fact]
    public void TheDefaultsOnlyApplyWhenNothingIsLinked()
    {
        GraveyardCatalog catalog = Catalog([Loc(4, 0, 1, 1, 1), Loc(7, 0, 50, 50, 0)], (7, 10, 0));

        Assert.Equal(7u, GraveyardSelector.FindClosestOrDefault(catalog, Maps, 0, 0, 0, 0, 10, 10, Alliance)!.Id);
    }

    [Fact]
    public void ACatalogWithoutGraveyards_FindsNothing()
        => Assert.Null(Pick(GraveyardCatalog.Empty, 0, 0, 0, 0, 10, 10, Alliance));

    [Fact]
    public void Build_AppliesTheLoadRulesOfLoadGraveyardZones()
    {
        var content = new GraveyardContent(
            [Loc(1, 0, 0, 0, 0)],
            [
                new GraveyardLink(1, 10, 0),      // fine
                new GraveyardLink(1, 10, 469),    // duplicate of (1, 10): skipped, the first stays
                new GraveyardLink(99, 10, 0),     // no such graveyard
                new GraveyardLink(1, 11, 5),      // not a player faction
                new GraveyardLink(1, 77, 0),      // no such zone
            ]);

        GraveyardCatalog catalog = GraveyardCatalog.Build(content, id => id != 77, out IReadOnlyList<string> diagnostics);

        Assert.Equal(1, catalog.LinkCount);
        Assert.Equal(0u, Assert.Single(catalog.LinksOf(10)).Team);
        Assert.Equal(4, diagnostics.Count);
        Assert.Contains(diagnostics, d => d.Contains("not existing graveyard 99", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Contains("non player faction 5", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Contains("not existing zone id 77", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Contains("duplicate record", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_WithoutAnAreaCheck_KeepsLinksOfUnknownZones()
    {
        // A world with no area table cannot tell a wrong zone id from a right one.
        var content = new GraveyardContent([Loc(1, 0, 0, 0, 0)], [new GraveyardLink(1, 77, 0)]);

        Assert.Equal(1, GraveyardCatalog.Build(content, null, out _).LinkCount);
    }
}

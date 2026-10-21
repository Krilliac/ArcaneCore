using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Graveyards;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Playerbots.Population;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Population;

/// <summary>
/// Undercity (zone 1497) and Ironforge (1537) are WMOs under Tirisfal Glades (85) and Dun Morogh (1): the area lookup now answers the
/// city, where it used to answer the zone around it. What the population does with a bot standing in a city must not change with that:
/// a graduate in its race's capital is still in its start country, and a bot in a capital is left where it is by the relocation pass.
/// </summary>
public sealed class WmoCapitalStartCountryTests
{
    private const uint Tirisfal = 85, Undercity = 1497, DunMorogh = 1, Ironforge = 1537;

    private static readonly PlayerbotPopulationOptions Pop = new();

    private static PopulationBotFacts Bot(uint zone, float x = 1600f, float y = 240f)
        => new(Guid.NewGuid(), 8, Team.Horde, zone, true, false, false, false, MapId: 0, X: x, Y: y);

    // Deathknell and Coldridge Valley are about 1,400 and 5,700 yards from their cities: far outside the graduated home radius.
    private static readonly StartPosition Deathknell = new(0, Tirisfal, 1676f, 1677f, 95f, 0f);
    private static readonly StartPosition Coldridge = new(0, DunMorogh, -6240f, 331f, 383f, 0f);

    [Fact]
    public void AGraduateInUndercity_IsStillInTheTirisfalStartCountry()
    {
        Assert.True(PlayerbotPopulationFeature.InStartCountry(Bot(Undercity), Deathknell, Pop));
    }

    [Fact]
    public void AGraduateInIronforge_IsStillInTheDunMoroghStartCountry()
    {
        Assert.True(PlayerbotPopulationFeature.InStartCountry(Bot(Ironforge, -4840f, -870f), Coldridge, Pop));
    }

    [Fact]
    public void ACapitalIsOnlyTheStartCountryOfTheZoneAroundIt()
    {
        // A Dwarf in Undercity, an Undead in Ironforge: far from home in another country, released as before.
        Assert.False(PlayerbotPopulationFeature.InStartCountry(Bot(Undercity), Coldridge, Pop));
        Assert.False(PlayerbotPopulationFeature.InStartCountry(Bot(Ironforge, -4840f, -870f), Deathknell, Pop));
        Assert.Equal(Undercity + 1, PlayerbotPopulationFeature.StartCountryZone(Undercity + 1));
    }

    [Fact]
    public void TheAtlasHasNoCapitalZone_SoARelocationLeavesABotInUndercityWhereItIs()
    {
        AreaTemplate[] areas =
        [
            new(Tirisfal, 0, 0, Tirisfal, 0, 0, "Tirisfal Glades", AreaTeams.Horde, 0),
            new(154, 0, Tirisfal, 154, 0, 1, "Deathknell", 0, 0),
            new(Undercity, 0, 0, Undercity, (uint)AreaFlags.Capital, 1, "Undercity", AreaTeams.Horde, 0),
            new(1498, 0, Undercity, 1498, 0, 1, "The Trade Quarter", 0, 0),
        ];
        WorldSafeLoc[] locs = [new(1, 0, 10f, 20f, 50f, 0f, "Brill"), new(2, 0, 30f, 40f, 50f, 0f, "Undercity")];
        GraveyardLink[] links = [new(1, Tirisfal, 0), new(2, Undercity, 0)];
        GraveyardCatalog graveyards = GraveyardCatalog.Build(new GraveyardContent(locs, links), null, out _);

        PopulationZoneAtlas atlas = PopulationZoneAtlas.Build(areas, graveyards, [0, 1], new HashSet<uint>());

        Assert.NotNull(atlas.Find(Tirisfal));
        Assert.Null(atlas.Find(Undercity));
        Assert.Null(atlas.Find(Ironforge));
    }
}

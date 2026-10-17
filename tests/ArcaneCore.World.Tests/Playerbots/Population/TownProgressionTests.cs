using ArcaneCore.Data.Content;
using System.Collections.Concurrent;
using System.Numerics;
using ArcaneCore.Data.Stores;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Population;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Population;

/// <summary>
/// Town progression: after the start flow a population bot has a master home (its faction capital) and a secondary home (the town of
/// its level band) and lives at one of them at a time. The pure rules (table, bands, spread, hysteresis, master weights, the cycle, the
/// travel rule, the idle rule), then the sweep in a hosted world with a synthetic town table, then the real world data.
/// </summary>
internal sealed class RealTownWorldFactAttribute : FactAttribute
{
    public RealTownWorldFactAttribute()
    {
        string? db = Environment.GetEnvironmentVariable("ARCANECORE_TEST_WORLD_DB");
        if (string.IsNullOrEmpty(db) || !File.Exists(db))
            Skip = "ARCANECORE_TEST_WORLD_DB (an imported world SQLite database) is not set or missing: the real-data check of the town table did not run.";
    }
}

public sealed class TownProgressionTests
{
    private static readonly PlayerbotPopulationOptions Defaults = new();

    // ---- the table ----

    [Fact]
    public void TheTable_HasUniqueShortKeys_ThreeCapitalsPerFaction_AndEveryLevelServedPerFactionAndContinent()
    {
        Assert.Equal(PopulationTownProgression.Rows.Count, PopulationTownProgression.Rows.Select(r => r.Key).Distinct().Count());
        Assert.All(PopulationTownProgression.Rows, r =>
        {
            Assert.InRange(r.Key.Length, 3, 14);
            Assert.True(r.MinLevel < r.MaxLevel);
            Assert.True(r.MapId is 0 or 1);
        });
        Assert.Equal(3, PopulationTownProgression.CapitalsOf(Team.Alliance).Count());
        Assert.Equal(3, PopulationTownProgression.CapitalsOf(Team.Horde).Count());
        Assert.Equal("stormwind", PopulationTownProgression.CapitalsOf(Team.Alliance).First().Key);
        Assert.Equal("orgrimmar", PopulationTownProgression.CapitalsOf(Team.Horde).First().Key);

        // Every level from the release level to 60 has a level-band town for each faction on each continent a race can start on.
        foreach (Team team in new[] { Team.Alliance, Team.Horde })
            foreach (uint continent in new[] { 0u, 1u })
                for (int level = new PlayerbotPopulationOptions().FreshReleaseLevel; level <= 60; level++)
                {
                    TownRow? row = PopulationTownProgression.PickSecondary(team, continent, level, new Dictionary<string, int>(), _ => 0, _ => true);
                    Assert.True(row is not null, $"no town serves a level {level} {team} bot");
                    if (level <= 45) Assert.True(row!.MapId == continent, $"level {level} {team} on map {continent} got {row.Key} on map {row.MapId}");
                }

        // Every row can be picked: a band that ends at or before the release level (the town stage begins there) could never serve a bot.
        Assert.All(PopulationTownProgression.Rows.Where(r => !r.Capital), r => Assert.True(r.MaxLevel > new PlayerbotPopulationOptions().FreshReleaseLevel, r.Key));
        Assert.Equal(46, PopulationTownProgression.Rows.Count);

        // The longest place text fits the 64 characters of a stored home.
        string longest = PopulationTownProgression.PlaceOf("cenarion-hold", "thunder-bluff", "cenarion-hold");
        Assert.True(longest.Length <= PlayerbotHomeBounds.MaxPlaceLength);
    }

    [Theory]
    [InlineData(Team.Alliance, 0u, 10, "kharanos,sentinel-hill,thelsamar")]
    [InlineData(Team.Alliance, 0u, 12, "sentinel-hill,thelsamar")]
    [InlineData(Team.Alliance, 0u, 27, "southshore,darkshire,menethil")]
    [InlineData(Team.Alliance, 1u, 10, "auberdine")]
    [InlineData(Team.Alliance, 1u, 14, "auberdine")]
    [InlineData(Team.Horde, 1u, 10, "crossroads")]
    [InlineData(Team.Horde, 1u, 12, "crossroads")]
    [InlineData(Team.Horde, 0u, 10, "brill,sepulcher")]
    [InlineData(Team.Horde, 0u, 24, "tarren-mill")]
    [InlineData(Team.Horde, 0u, 33, "hammerfall,grom-gol")]
    [InlineData(Team.Alliance, 0u, 58, "chillwind,lights-hope")]
    public void TheBand_OfALevelAndFaction_NamesItsTowns(Team team, uint continent, int level, string expected)
    {
        string[] towns = [.. PopulationTownProgression.Rows
            .Where(r => !r.Capital && r.MapId == continent && PopulationTownProgression.ServesTeam(r, team) && PopulationTownProgression.StillServes(r, level))
            .Select(r => r.Key).Order()];
        Assert.Equal([.. expected.Split(',').Order()], towns);
        TownRow picked = Assert.IsType<TownRow>(PopulationTownProgression.PickSecondary(team, continent, level, new Dictionary<string, int>(), _ => 0, _ => true));
        Assert.Contains(picked.Key, towns);
    }

    [Fact]
    public void ABotOfAnotherContinent_IsOnlyGivenATownThereWhenItsOwnContinentHasNone()
    {
        // Alliance level 59 on Kalimdor: Everlook and Cenarion Hold (Kalimdor) serve it.
        Assert.Equal(1u, PopulationTownProgression.PickSecondary(Team.Alliance, 1, 59, new Dictionary<string, int>(), _ => 0, _ => true)!.MapId);
        // Nothing of the own continent usable: the other continent's town (reached by the one teleport).
        TownRow? across = PopulationTownProgression.PickSecondary(Team.Alliance, 1, 58, new Dictionary<string, int>(), _ => 0, k => k is "chillwind");
        Assert.Equal("chillwind", across!.Key);
        Assert.Null(PopulationTownProgression.PickSecondary(Team.Alliance, 0, 58, new Dictionary<string, int>(), _ => 0, _ => false));
    }

    // ---- spread and cap ----

    [Fact]
    public void TheBots_SpreadOverTheTownsOfTheirBand_UnderTheCap_ThenTheLeastPopulatedTakesThem()
    {
        var counts = new Dictionary<string, int>();
        var assigned = new List<string>();
        for (int i = 0; i < 6; i++)
        {
            TownRow row = PopulationTownProgression.PickSecondary(Team.Alliance, 0, 12, counts, _ => 2, _ => true)!;
            counts[row.Key] = counts.GetValueOrDefault(row.Key) + 1;
            assigned.Add(row.Key);
        }

        // Sentinel Hill and Thelsamar (10-20): two each under the cap, then the over-cap fallback keeps them level (3 and 3), none is left out.
        Assert.Equal(["sentinel-hill", "thelsamar"], [.. counts.Keys.Order()]);
        Assert.Equal(3, counts["sentinel-hill"]);
        Assert.Equal(3, counts["thelsamar"]);
        Assert.Equal(6, assigned.Count);

        // A full town is skipped while another has room.
        var oneFull = new Dictionary<string, int> { ["thelsamar"] = 2, ["sentinel-hill"] = 1 };
        Assert.Equal("sentinel-hill", PopulationTownProgression.PickSecondary(Team.Alliance, 0, 12, oneFull, _ => 2, _ => true)!.Key);
        // The least populated wins before the cap is looked at.
        var uneven = new Dictionary<string, int> { ["thelsamar"] = 5, ["sentinel-hill"] = 9 };
        Assert.Equal("thelsamar", PopulationTownProgression.PickSecondary(Team.Alliance, 0, 12, uneven, _ => 0, _ => true)!.Key);
    }

    [Fact]
    public void ThePerTownCaps_OverrideTheGlobalCap_AndAreValidated()
    {
        var options = new PlayerbotPopulationOptions { TownCap = 60, TownCaps = "sentinel-hill=7, thelsamar=0" };
        Assert.Equal(7, PopulationTownProgression.CapOf(options, "sentinel-hill"));
        Assert.Equal(0, PopulationTownProgression.CapOf(options, "thelsamar"));
        Assert.Equal(60, PopulationTownProgression.CapOf(options, "lakeshire"));
        options.Validate();
        foreach (string bad in new[] { "atlantis=3", "sentinel-hill", "sentinel-hill=1001", "sentinel-hill=-1", "sentinel-hill=1,sentinel-hill=2", "stormwind=5", "=5" })
        {
            var broken = new PlayerbotPopulationOptions { TownCaps = bad };
            Assert.Throws<InvalidOperationException>(broken.Validate);
        }
    }

    /// <summary>
    /// The keys TownCaps accepts live in the contracts assembly (the core validates them at .reload config, tier 2b), the town table in
    /// the pack: the two lists must be the same, or a cap for a real town is refused, or one for a removed town silently ignored.
    /// </summary>
    [Fact]
    public void TheTownCapKeysOfTheOptions_AreTheLevelBandTownsOfTheTable()
    {
        string[] table = [.. PopulationTownProgression.Rows.Where(r => !r.Capital).Select(r => r.Key).Order(StringComparer.Ordinal)];
        Assert.Equal(40, table.Length);
        Assert.Equal<string>(table, PlayerbotPopulationOptions.LevelBandTownKeys.Order(StringComparer.Ordinal));
    }

    // ---- hysteresis ----

    [Fact]
    public void ABotKeepsItsTown_UntilItsLevelReachesTheBandsEnd_PlusTheMargin()
    {
        TownRow sentinel = PopulationTownProgression.Find("sentinel-hill")!; // 10-20
        for (int level = 10; level <= 19; level++) Assert.True(PopulationTownProgression.StillServes(sentinel, level), $"level {level}");
        Assert.False(PopulationTownProgression.StillServes(sentinel, 20));
        Assert.False(PopulationTownProgression.StillServes(sentinel, 9));
        Assert.True(PopulationTownProgression.StillServes(sentinel, 22, margin: 3));
        Assert.False(PopulationTownProgression.StillServes(sentinel, 23, margin: 3));
        Assert.False(PopulationTownProgression.StillServes(PopulationTownProgression.Find("stormwind")!, 30)); // a capital is never a band town
    }

    // ---- master weighting ----

    private static Guid[] SeededGuids(int count)
    {
        var random = new Random(20261015);
        var ids = new Guid[count];
        for (int i = 0; i < ids.Length; i++)
        {
            byte[] bytes = new byte[16];
            random.NextBytes(bytes);
            ids[i] = new Guid(bytes);
        }

        return ids;
    }

    [Fact]
    public void TheMasterCapital_FavoursStormwindAndOrgrimmar_ByTheConfiguredShare_Deterministically()
    {
        Guid[] ids = SeededGuids(4000);
        foreach ((Team team, string major) in new[] { (Team.Alliance, "stormwind"), (Team.Horde, "orgrimmar") })
        {
            byte race = team == Team.Alliance ? (byte)1 : (byte)2;
            string[] masters = [.. ids.Select(id => PopulationTownProgression.MasterFor(team, race, id, 65, _ => true)!)];
            double share = masters.Count(m => m == major) / (double)masters.Length;
            Assert.InRange(share, 0.62, 0.68);
            Assert.All(masters, m => Assert.Equal(team, PopulationTownProgression.Find(m)!.Team));
            // The same bots always get the same capitals.
            Assert.Equal(masters, ids.Select(id => PopulationTownProgression.MasterFor(team, race, id, 65, _ => true)!));
            // The extremes.
            Assert.All(ids.Take(300), id => Assert.Equal(major, PopulationTownProgression.MasterFor(team, race, id, 100, _ => true)));
            Assert.All(ids.Take(300), id => Assert.NotEqual(major, PopulationTownProgression.MasterFor(team, race, id, 0, _ => true)));
        }
    }

    [Fact]
    public void TheRace_OnlyBreaksTheTieInsideTheMinorCapitals()
    {
        Guid id = SeededGuids(1)[0];
        // Share 0: the minor capitals only; each race's own people first (dwarf Ironforge, night elf Darnassus, undead Undercity, tauren Thunder Bluff).
        Assert.Equal("ironforge", PopulationTownProgression.MasterFor(Team.Alliance, (byte)Race.Dwarf, id, 0, _ => true));
        Assert.Equal("darnassus", PopulationTownProgression.MasterFor(Team.Alliance, (byte)Race.NightElf, id, 0, _ => true));
        Assert.Equal("undercity", PopulationTownProgression.MasterFor(Team.Horde, (byte)Race.Undead, id, 0, _ => true));
        Assert.Equal("thunder-bluff", PopulationTownProgression.MasterFor(Team.Horde, (byte)Race.Tauren, id, 0, _ => true));
        // A race with no minor capital of its own is placed by the id; at the default share the race changes nothing for the major bots.
        Assert.Contains(PopulationTownProgression.MasterFor(Team.Alliance, (byte)Race.Human, id, 0, _ => true), new[] { "ironforge", "darnassus" });
        Guid[] ids = SeededGuids(400);
        Assert.Equal(ids.Select(i => PopulationTownProgression.MasterFor(Team.Alliance, (byte)Race.Human, i, 65, _ => true)!.Equals("stormwind")),
            ids.Select(i => PopulationTownProgression.MasterFor(Team.Alliance, (byte)Race.NightElf, i, 65, _ => true)!.Equals("stormwind")));
        // An unusable capital is skipped; none usable is null.
        Assert.Equal("ironforge", PopulationTownProgression.MasterFor(Team.Alliance, 1, id, 100, k => k == "ironforge"));
        Assert.Null(PopulationTownProgression.MasterFor(Team.Alliance, 1, id, 100, _ => false));
    }

    // ---- the cycle ----

    [Fact]
    public void TheCycle_SwitchesTheActiveHome_AtTheEndsOfTheStays()
    {
        var atCapital = new TownPlace("stormwind", "stormwind", "darkshire");
        long capital = 5 * 60_000L, secondary = 60 * 60_000L;
        Assert.Equal("stormwind", PopulationTownProgression.NextActive(atCapital, "darkshire", false, capital - 1, capital, secondary));
        Assert.Equal("darkshire", PopulationTownProgression.NextActive(atCapital, "darkshire", false, capital, capital, secondary));

        var atSecondary = new TownPlace("darkshire", "stormwind", "darkshire");
        Assert.Equal("darkshire", PopulationTownProgression.NextActive(atSecondary, "darkshire", false, secondary - 1, capital, secondary));
        Assert.Equal("stormwind", PopulationTownProgression.NextActive(atSecondary, "darkshire", false, secondary, capital, secondary));
        // The band moved while at the secondary: straight to the new town. While at the capital: the capital stays until its stay is over.
        Assert.Equal("southshore", PopulationTownProgression.NextActive(atSecondary, "southshore", true, 1, capital, secondary));
        Assert.Equal("stormwind", PopulationTownProgression.NextActive(atCapital, "southshore", true, 1, capital, secondary));
        // No secondary: the capital.
        Assert.Equal("stormwind", PopulationTownProgression.NextActive(atSecondary, null, false, 99 * 60_000L, capital, secondary));
    }

    [Fact]
    public void TheStays_AreFixedPerBot_BetweenTheMinAndTheMax()
    {
        Guid[] ids = SeededGuids(300);
        long[] stays = [.. ids.Select(id => PopulationTownProgression.StayMs(30, 90, id))];
        Assert.All(stays, s => Assert.InRange(s, 30 * 60_000L, 90 * 60_000L));
        Assert.True(stays.Distinct().Count() > 100, "a fleet must not move in step");
        Assert.Equal(stays, ids.Select(id => PopulationTownProgression.StayMs(30, 90, id)));
        Assert.Equal(10 * 60_000L, PopulationTownProgression.StayMs(10, 10, ids[0]));
    }

    [Fact]
    public void ThePlaceText_RoundTrips_AndIsTheOnlyThingATownHomeIsRecognisedBy()
    {
        string place = PopulationTownProgression.PlaceOf("darkshire", "stormwind", "darkshire");
        Assert.Equal("town:darkshire;m=stormwind;s=darkshire", place);
        Assert.True(PopulationTownProgression.TryParsePlace(place, out TownPlace parsed));
        Assert.Equal(new TownPlace("darkshire", "stormwind", "darkshire"), parsed);
        Assert.False(parsed.AtCapital);
        Assert.Equal(StartHomeStage.Town, PopulationStartAreas.StageOfPlace(place));
        Assert.Equal(TownStay.Secondary, PopulationTownProgression.StayOf(place));
        Assert.Equal(TownStay.Capital, PopulationTownProgression.StayOf(PopulationTownProgression.PlaceOf("stormwind", "stormwind", "darkshire")));
        Assert.True(PopulationTownProgression.TryParsePlace("town:orgrimmar;m=orgrimmar", out TownPlace capitalOnly));
        Assert.Null(capitalOnly.Secondary);
        foreach (string bad in new[] { "Sentinel Hill", "town:atlantis;m=stormwind", "town:darkshire", "town:darkshire;m=darkshire", "town:darkshire;m=stormwind;s=stormwind", "town:darkshire;x=1", null! })
            Assert.False(PopulationTownProgression.TryParsePlace(bad, out _), bad);
        Assert.Equal(TownStay.None, PopulationTownProgression.StayOf("zone:northshire"));
    }

    // ---- travel ----

    [Fact]
    public void WalkableTrips_Walk_AndTheOthers_TakeTheReturnTeleport()
    {
        var auto = new PlayerbotHomeOptions();
        float walk = PlayerbotHomeRules.TeleportDistanceFor(auto, Defaults.TownWalkYards);
        Assert.True(walk > auto.TeleportDistanceYards, "a town home walks farther than the Home option alone");
        // Westfall to Stormwind (about 1850 yards) and the Crossroads to Orgrimmar (about 2800) walk; the Home option alone would teleport them.
        foreach (float yards in new[] { 1850f, 2800f })
        {
            var facts = new PlayerbotHomeFacts(true, yards, 0, 20, false);
            Assert.Equal(PlayerbotHomeTeleportReason.None, DefaultHomeTravel.Instance.Decide(facts, auto, walk));
            Assert.Equal(PlayerbotHomeTeleportReason.Distance, PlayerbotHomeRules.Decide(facts, auto));
        }

        // Too far, water between (infinite distance), or another continent: the one teleport.
        Assert.Equal(PlayerbotHomeTeleportReason.Distance, DefaultHomeTravel.Instance.Decide(new(true, 6000f, 0, 20, false), auto, walk));
        Assert.Equal(PlayerbotHomeTeleportReason.Distance, DefaultHomeTravel.Instance.Decide(new(true, float.PositiveInfinity, 0, 20, false), auto, walk));
        Assert.Equal(PlayerbotHomeTeleportReason.OtherMap, DefaultHomeTravel.Instance.Decide(new(false, float.NaN, 0, 20, false), auto, walk));
    }

    [Fact]
    public void Teldrassil_IsAnIsland_AndKalimdorAndTheKingdomsAreNotConnected()
    {
        Vector3 auberdine = Position("auberdine"), darnassus = Position("darnassus"), dolanaar = Position("dolanaar");
        Assert.True(PopulationTownProgression.SeparatedByWater(1, auberdine.X, auberdine.Y, 1, darnassus.X, darnassus.Y));
        Assert.False(PopulationTownProgression.SeparatedByWater(1, dolanaar.X, dolanaar.Y, 1, darnassus.X, darnassus.Y));
        Assert.False(PopulationTownProgression.SeparatedByWater(0, 0, 0, 0, 9000, 2000));
        Assert.True(DefaultHomeTravel.Instance.CanReachWithoutTeleport(1, dolanaar, 1, darnassus));
        Assert.False(DefaultHomeTravel.Instance.CanReachWithoutTeleport(1, auberdine, 1, darnassus));
        Assert.False(DefaultHomeTravel.Instance.CanReachWithoutTeleport(0, new Vector3(-8868, 674, 98), 1, darnassus));

        static Vector3 Position(string key) => key switch
        {
            "auberdine" => new(6407, 515, 9),
            "darnassus" => new(10128, 2225, 1329),
            _ => new(9802, 983, 1314),
        };
    }

    // ---- the idle rule ----

    [Fact]
    public void AnIdleBot_WithATownHome_GoesBackToTheInn_RatherThanExploring()
    {
        var capital = new PlayerbotTownIdle();
        Assert.Equal(TownIdleAction.WalkToTown, capital.Decide(TownStay.Capital, 800f, 0));
        Assert.Equal(TownIdleAction.Linger, capital.Decide(TownStay.Capital, 2f, 1_000_000));
        Assert.Equal(TownIdleAction.WalkToTown, capital.Decide(TownStay.Capital, 800f, 9_000_000)); // never explores at the capital

        var secondary = new PlayerbotTownIdle();
        Assert.Equal(TownIdleAction.WalkToTown, secondary.Decide(TownStay.Secondary, 1500f, 0));      // idle: back to town first
        Assert.Equal(TownIdleAction.Linger, secondary.Decide(TownStay.Secondary, 2f, 60_000));
        Assert.Equal(TownIdleAction.Explore, secondary.Decide(TownStay.Secondary, 2f, PlayerbotTownIdle.LingerMs + 1)); // then a bounded look round
        Assert.Equal(TownIdleAction.Explore, secondary.Decide(TownStay.Secondary, 900f, PlayerbotTownIdle.LingerMs + 100_000));
        Assert.Equal(TownIdleAction.WalkToTown, secondary.Decide(TownStay.Secondary, 900f, PlayerbotTownIdle.LingerMs + PlayerbotTownIdle.ExploreMs + 2));

        Assert.Equal(TownIdleAction.Explore, new PlayerbotTownIdle().Decide(TownStay.None, 5000f, 0)); // any other home explores as before
    }

    [Fact]
    public void ABotStayingInATown_BindsThereWhenItsBindIsElsewhere()
    {
        Assert.True(PlayerbotTownIdle.NeedsBind(0, -9000f, -100f, 0, -8868f, 674f));   // bound in the start valley
        Assert.True(PlayerbotTownIdle.NeedsBind(1, -8868f, 674f, 0, -8868f, 674f));    // another map
        Assert.False(PlayerbotTownIdle.NeedsBind(0, -8860f, 670f, 0, -8868f, 674f));   // already at the inn
    }

    // ---- options ----

    [Fact]
    public void TheTownKeys_HaveTheirDefaults_AndAreValidated()
    {
        var o = new PlayerbotPopulationOptions();
        Assert.True(o.TownProgression);
        Assert.True(o.TownProgressionTeleport);
        Assert.False(o.AdoptOperatorHomes);
        Assert.Equal((4500, 500, 60, 65), (o.TownHomeRadius, o.CapitalHomeRadius, o.TownCap, o.CapitalMajorSharePct));
        Assert.Equal((30, 90, 5, 15, 0, 4500), (o.SecondaryStayMinMinutes, o.SecondaryStayMaxMinutes, o.CapitalStayMinMinutes, o.CapitalStayMaxMinutes, o.TownBandMargin, o.TownWalkYards));
        Assert.Equal(10, o.FreshReleaseLevel);
        o.Validate();
        foreach (Action<PlayerbotPopulationOptions> bad in new Action<PlayerbotPopulationOptions>[]
        {
            x => x.TownHomeRadius = 49, x => x.CapitalHomeRadius = 5001, x => x.TownCap = -1, x => x.TownCap = 1001,
            x => x.CapitalMajorSharePct = 101, x => x.CapitalMajorSharePct = -1, x => x.SecondaryStayMinMinutes = 4,
            x => x.SecondaryStayMaxMinutes = 29, x => x.CapitalStayMinMinutes = 0, x => x.CapitalStayMaxMinutes = 4,
            x => x.CapitalStayMaxMinutes = 241, x => x.TownBandMargin = 11, x => x.TownWalkYards = 99, x => x.TownWalkYards = 20001,
        })
        {
            var broken = new PlayerbotPopulationOptions();
            bad(broken);
            Assert.Throws<InvalidOperationException>(broken.Validate);
        }
    }

    [Fact]
    public void TheStageFollowsTheLevel_ThenATownHome_OrNoneWhenTheProgressionIsOff()
    {
        var on = new PlayerbotPopulationOptions();
        Assert.Equal(StartHomeStage.Start, PopulationStartAreas.StageFor(5, on));
        Assert.Equal(StartHomeStage.Zone, PopulationStartAreas.StageFor(9, on));
        Assert.Equal(StartHomeStage.Town, PopulationStartAreas.StageFor(10, on));
        Assert.Equal(StartHomeStage.Town, PopulationStartAreas.StageFor(60, on));
        Assert.Equal(StartHomeStage.None, PopulationStartAreas.StageFor(10, new PlayerbotPopulationOptions { TownProgression = false }));
    }

    // ---- the sweep, in a hosted world with a synthetic town table ----

    /// <summary>A creature content with the anchor NPC of every town in the table, each town 20000 yards from the next so no home contains another town.</summary>
    private static CreatureContent TownContent(params string[] except)
    {
        var spawns = new List<CreatureSpawn>();
        uint guid = 1;
        int index = 0;
        foreach (TownRow row in PopulationTownProgression.Rows)
        {
            index++;
            if (except.Contains(row.Key)) continue;
            // Darnassus is on the island of Teldrassil (x above 8000, y above -200 on map 1), as in the world; every other town is 20000 yards from the next.
            (float x, float y) = row.Key == "darnassus" ? (9800f, 983f) : (-9000f, 20000f * index);
            spawns.Add(new CreatureSpawn { Guid = guid++, Entry = row.AnchorEntry, MapId = row.MapId, X = x, Y = y, Z = 50f });
        }

        // An auction house, a trainer and a banker 300 yards from the centre of Stormwind and of Orgrimmar.
        foreach (string capital in new[] { "stormwind", "orgrimmar" })
        {
            TownRow row = PopulationTownProgression.Find(capital)!;
            float y = 20000f * (PopulationTownProgression.Rows.ToList().IndexOf(row) + 1) + 300f;
            foreach (uint entry in new uint[] { ErrandAuctioneer, ErrandTrainer, ErrandBanker })
                spawns.Add(new CreatureSpawn { Guid = guid++, Entry = entry, MapId = row.MapId, X = -9000f, Y = y, Z = 50f });
        }

        return new CreatureContent([], spawns, [], [], []);
    }

    private const uint ErrandAuctioneer = 900_001, ErrandTrainer = 900_002, ErrandBanker = 900_003;

    private static Task<PopulationTestHost> StartAsync(MemoryHomeStore homes, Action<PlayerbotOptions>? configure = null, CreatureContent? content = null,
        Action<IServiceCollection>? extra = null)
    {
        content ??= TownContent();
        return PopulationTestHost.StartAsync(o =>
        {
            o.MaxBots = 40;
            o.MaxRegisteredBots = 50;
            o.Population.FreshLaunch = true;
            o.Population.TargetOnline = 2;
            o.Population.Factions = PopulationFactions.Alliance;
            o.Population.MaxStartsPerInterval = 40;
            o.Population.MaxCreatePerMinute = 60;
            o.Population.FreshStartHome = false; // bots first exist homeless; the tests turn the start homes on when they need them
            // Fixed stays: one minute at the capital, five at the town (every pass is ten minutes of world time).
            o.Population.CapitalStayMinMinutes = o.Population.CapitalStayMaxMinutes = 1;
            o.Population.SecondaryStayMinMinutes = o.Population.SecondaryStayMaxMinutes = 5;
            configure?.Invoke(o);
        }, configureServices: services =>
        {
            services.AddSingleton<IManagedPlayerbotHomeStore>(homes);
            extra?.Invoke(services);
        }, dataStore: () => new FreshLaunchTests.RaceStartDataStore());
    }

    private static async Task<PopulationTestHost> WorldAsync(MemoryHomeStore homes, int bots, Action<PlayerbotOptions>? configure = null, CreatureContent? content = null,
        Action<IServiceCollection>? extra = null)
    {
        PopulationTestHost world = await StartAsync(homes, o =>
        {
            o.Population.TargetOnline = bots;
            configure?.Invoke(o);
        }, content, extra);
        world.Bots.ZoneLookups = _ => new PlayerbotZoneLookup((_, _, _) => PopulationTestHost.Elwynn);
        CreatureContent townContent = content ?? TownContent();
        world.Feature.TownContentSource = () => townContent;
        world.Ticks.Set(200, 10);
        await world.PassAsync();
        await world.WaitForSettledAsync(bots);
        return world;
    }

    private static Task SetLevelAsync(PopulationTestHost world, Guid botId, byte level)
        => world.Host.World.InvokeAsync(() =>
        {
            world.Bots.FindSession(botId)!.Player!.Level = level;
            return true;
        });

    private static async Task<PopulationStatus> NextPassAsync(PopulationTestHost world)
    {
        world.Feature.ClockMs += (world.Population.IntervalSeconds * 1000L) + 1000;
        world.Ticks.Set(200, 10);
        return await world.PassAsync();
    }

    private static TownPlace PlaceOf(PopulationTestHost world, Guid botId)
    {
        PlayerbotHome home = Assert.IsType<PlayerbotHome>(world.Bots.HomeOf(botId));
        Assert.True(PopulationTownProgression.TryParsePlace(home.Place, out TownPlace place), home.Place);
        return place;
    }

    private static Guid[] Bots(PopulationTestHost world) => [.. world.Bots.Snapshot().Select(s => s.BotId)];

    [Fact]
    public async Task AGraduate_GetsItsCapitalAndTheTownOfItsBand_StartingAtTheCapital_AndTheHomeIsTheActiveOne()
    {
        var homes = new MemoryHomeStore();
        await using PopulationTestHost world = await WorldAsync(homes, 3);
        world.Population.FreshStartHome = true;
        Guid[] bots = Bots(world);
        foreach (Guid id in bots) await SetLevelAsync(world, id, 12);
        await NextPassAsync(world);

        foreach (Guid id in bots)
        {
            TownPlace place = PlaceOf(world, id);
            TownRow secondary = PopulationTownProgression.Find(place.Secondary)!;
            Assert.True(PopulationTownProgression.StillServes(secondary, 12), secondary.Key);
            Assert.Equal(Team.Alliance, PopulationTownProgression.Find(place.Master)!.Team);
            Assert.True(place.AtCapital, "a graduate starts its first stay at the capital (trainers)");
            PlayerbotHome home = world.Bots.HomeOf(id)!;
            Assert.Equal(PopulationTownProgression.Find(place.Master)!.MapId, home.MapId);
            Assert.Equal((float)world.Population.CapitalHomeRadius, home.Radius);
            Assert.True(home.ZoneClipped);
            Assert.NotNull(homes.Get(id)); // stored
            // The brain's home is the stored, active one (the pull back goes there and nowhere else).
            Assert.Equal(home.Place, world.Bots.FindBrain(id)!.Home!.Place);
            Assert.Equal(TownStay.Capital, world.Bots.FindBrain(id)!.Home!.Stay);
            Assert.False(world.Bots.FindBrain(id)!.Home!.AllowsLeaveQuests); // a capital sends nobody off on a quest
            Assert.Equal((float)world.Population.TownWalkYards, world.Bots.FindBrain(id)!.Home!.WalkRangeYards);
        }
    }

    [Fact]
    public async Task TheHome_CyclesBetweenCapitalAndTown_AtTheEndsOfTheStays_AndTheBrainFollowsTheActiveOne()
    {
        await using PopulationTestHost world = await WorldAsync(new MemoryHomeStore(), 1);
        world.Population.FreshStartHome = true;
        Guid bot = Bots(world).Single();
        await SetLevelAsync(world, bot, 15);

        await NextPassAsync(world);                                    // t0: the capital stay begins
        TownPlace first = PlaceOf(world, bot);
        Assert.True(first.AtCapital);
        await NextPassAsync(world);                                    // 10 minutes later: the one-minute capital stay is over -> the town
        TownPlace second = PlaceOf(world, bot);
        Assert.Equal(first.Secondary, second.Active);
        Assert.Equal(first.Master, second.Master);
        Assert.Equal((float)world.Population.TownHomeRadius, world.Bots.HomeOf(bot)!.Radius);
        Assert.Equal(TownStay.Secondary, world.Bots.FindBrain(bot)!.Home!.Stay);
        Assert.True(world.Bots.FindBrain(bot)!.Home!.AllowsLeaveQuests);            // the band town may send the bot on to the next one
        await NextPassAsync(world);                                    // the five-minute town stay is over -> the capital
        Assert.True(PlaceOf(world, bot).AtCapital);
        Assert.Equal((float)world.Population.CapitalHomeRadius, world.Bots.HomeOf(bot)!.Radius);
        Assert.Equal(TownStay.Capital, world.Bots.FindBrain(bot)!.Home!.Stay);
        Assert.False(world.Bots.FindBrain(bot)!.Home!.AllowsLeaveQuests);           // a capital sends nobody off on a quest
        await NextPassAsync(world);
        Assert.Equal(first.Secondary, PlaceOf(world, bot).Active);
    }

    [Fact]
    public async Task ALevelUpAcrossABand_ReHomesTheBotToTheNextTown_AndNothingFlapsWithinTheBand()
    {
        await using PopulationTestHost world = await WorldAsync(new MemoryHomeStore(), 1, o =>
        {
            // Stays far longer than the test, so only the band moves the home.
            o.Population.SecondaryStayMinMinutes = o.Population.SecondaryStayMaxMinutes = 1440;
            o.Population.CapitalStayMinMinutes = o.Population.CapitalStayMaxMinutes = 1;
        });
        world.Population.FreshStartHome = true;
        Guid bot = Bots(world).Single();
        await SetLevelAsync(world, bot, 12);
        await NextPassAsync(world);
        await NextPassAsync(world);                                    // out to the band town
        TownPlace atBand = PlaceOf(world, bot);
        Assert.False(atBand.AtCapital);
        TownRow band = PopulationTownProgression.Find(atBand.Secondary)!;
        Assert.InRange(12, band.MinLevel, band.MaxLevel - 1);

        // Levels inside the band: not one change of home, however many sweeps.
        for (byte level = 13; level < band.MaxLevel; level++)
        {
            await SetLevelAsync(world, bot, level);
            await NextPassAsync(world);
            Assert.Equal(atBand, PlaceOf(world, bot));
        }

        // The level reaches the cap of the band: the bot moves on, straight to a town that serves the new level.
        await SetLevelAsync(world, bot, (byte)band.MaxLevel);
        await NextPassAsync(world);
        TownPlace moved = PlaceOf(world, bot);
        Assert.NotEqual(atBand.Secondary, moved.Secondary);
        Assert.Equal(moved.Secondary, moved.Active);
        Assert.True(PopulationTownProgression.StillServes(PopulationTownProgression.Find(moved.Secondary)!, band.MaxLevel));
        Assert.Equal(atBand.Master, moved.Master);
        await NextPassAsync(world);
        Assert.Equal(moved, PlaceOf(world, bot));
    }

    [Fact]
    public async Task ATownWhoseZoneHoldsNothingOfTheBotsLevel_IsLeftForOneThatDoes()
    {
        await using PopulationTestHost world = await WorldAsync(new MemoryHomeStore(), 1, o =>
        {
            o.Population.SecondaryStayMinMinutes = o.Population.SecondaryStayMaxMinutes = 1440;
        });
        world.Population.FreshStartHome = true;
        Guid bot = Bots(world).Single();
        await SetLevelAsync(world, bot, 21);
        await NextPassAsync(world);
        string before = PlaceOf(world, bot).Secondary!;

        // The explore atlas has no creature of the bot's level round that town (an over-levelled zone): it takes another town of the band.
        Vector3 centre = TownCentre(before);
        world.Feature.TownFits = (_, _, at) => at != centre;
        await NextPassAsync(world);
        Assert.Equal(before, PlaceOf(world, bot).Secondary);          // one sweep without a fit is not enough (the hysteresis)
        await NextPassAsync(world);
        string after = PlaceOf(world, bot).Secondary!;
        Assert.NotEqual(before, after);
        Assert.True(PopulationTownProgression.StillServes(PopulationTownProgression.Find(after)!, 21));

        // When no town of the band fits, the bot keeps a town of its band rather than none.
        world.Feature.TownFits = (_, _, _) => false;
        await NextPassAsync(world);
        Assert.NotNull(PlaceOf(world, bot).Secondary);
    }

    private static Vector3 TownCentre(string key)
        => PopulationTownProgression.Resolve(TownContent())[key].Centre;

    [Fact]
    public async Task TheErrandServices_LieInsideTheCapitalHome_ButNotInsideTheTownHome()
    {
        await using PopulationTestHost world = await WorldAsync(new MemoryHomeStore(), 1, o => o.Population.Factions = PopulationFactions.Alliance);
        world.Population.FreshStartHome = true;
        world.Population.CapitalMajorSharePct = 100;
        Guid bot = Bots(world).Single();
        await SetLevelAsync(world, bot, 15);
        CreatureContent content = TownContent();

        await NextPassAsync(world);
        Assert.Equal("stormwind", PlaceOf(world, bot).Active);
        PlayerbotHomeArea capital = world.Bots.FindBrain(bot)!.Home!;
        Assert.All(new[] { ErrandAuctioneer, ErrandTrainer, ErrandBanker }, entry => Assert.True(capital.HasSpawnInside(content, entry), $"entry {entry}"));

        await NextPassAsync(world);
        PlayerbotHomeArea town = world.Bots.FindBrain(bot)!.Home!;
        Assert.Equal(TownStay.Secondary, town.Stay);
        Assert.All(new[] { ErrandAuctioneer, ErrandTrainer, ErrandBanker }, entry => Assert.False(town.HasSpawnInside(content, entry), $"entry {entry}"));
    }

    [Fact]
    public async Task TheMastersOfAFleet_AreMostlyStormwind_AndEveryBotHasOne()
    {
        await using PopulationTestHost world = await WorldAsync(new MemoryHomeStore(), 30);
        world.Population.FreshStartHome = true;
        Guid[] bots = Bots(world);
        foreach (Guid id in bots) await SetLevelAsync(world, id, 14);
        int passes = 0;
        while (bots.Any(id => world.Bots.HomeOf(id) is null) && passes < 8) { await NextPassAsync(world); passes++; } // MaxHomeChangesPerSweep = 25
        Assert.True(bots.All(id => world.Bots.HomeOf(id) is not null), $"homeless after {passes} passes: {world.Feature.Status.LastError} / {world.Feature.Status.Reason}");
        Assert.InRange(passes, 2, 3);
        string[] masters = [.. bots.Select(id => PlaceOf(world, id).Master)];
        Assert.Equal(30, masters.Length);
        Assert.InRange(masters.Count(m => m == "stormwind"), 12, 28);
        Assert.Contains(masters, m => m != "stormwind");
    }

    [Fact]
    public async Task ARestart_KeepsTheHome_AndStartsAFreshStay_WithoutFlapping()
    {
        await using PopulationTestHost world = await WorldAsync(new MemoryHomeStore(), 1);
        world.Population.FreshStartHome = true;
        Guid bot = Bots(world).Single();
        await SetLevelAsync(world, bot, 15);
        await NextPassAsync(world);
        await NextPassAsync(world);
        TownPlace atTown = PlaceOf(world, bot);
        Assert.False(atTown.AtCapital);

        // The process restarts: the stored home survives, the stay clocks do not. The five-minute stay would be over after the next
        // ten minutes, but a restart gives the bot a fresh stay: this sweep leaves it, the next one ends it.
        world.Feature.ForgetTownPhases();
        await NextPassAsync(world);
        Assert.Equal(atTown, PlaceOf(world, bot));
        await NextPassAsync(world);
        Assert.True(PlaceOf(world, bot).AtCapital);
    }

    [Fact]
    public async Task AnOperatorsHome_IsNeverTouched_WithoutTheMigration_AndConvertedWithIt()
    {
        var homes = new MemoryHomeStore();
        await using PopulationTestHost world = await WorldAsync(homes, 2);
        world.Population.FreshStartHome = true;
        Guid[] bots = Bots(world);
        var sentinel = new Vector3(-10653f, 1166f, 35f);
        foreach (Guid id in bots)
            await world.Bots.SetHomesAsync([new ManagedPlayerbotFeature.HomeTarget(id, 0, sentinel)], 750f, "Sentinel Hill");
        await SetLevelAsync(world, bots[1], 15);

        // Without the migration: untouched, at level 1 and at level 15.
        await NextPassAsync(world);
        await NextPassAsync(world);
        Assert.All(bots, id => Assert.Equal("Sentinel Hill", world.Bots.HomeOf(id)!.Place));

        // The option: a level 1 bot goes onto the start-area flow, the level 15 one onto its town homes.
        world.Population.AdoptOperatorHomes = true;
        await NextPassAsync(world);
        Assert.StartsWith("start:", world.Bots.HomeOf(bots[0])!.Place);
        Assert.StartsWith("town:", world.Bots.HomeOf(bots[1])!.Place);
    }

    [Fact]
    public async Task TheAdoptCommand_ConvertsTheOperatorHomesOnce_ThenStopsAdopting()
    {
        await using PopulationTestHost world = await WorldAsync(new MemoryHomeStore(), 2);
        world.Population.FreshStartHome = true;
        Guid[] bots = Bots(world);
        var sentinel = new Vector3(-10653f, 1166f, 35f);
        await world.Bots.SetHomesAsync([new ManagedPlayerbotFeature.HomeTarget(bots[0], 0, sentinel)], 750f, "Sentinel Hill");
        await NextPassAsync(world);
        Assert.Equal("Sentinel Hill", world.Bots.HomeOf(bots[0])!.Place);
        Assert.False(world.Feature.AdoptPending);

        world.Feature.RequestAdopt();
        Assert.True(world.Feature.AdoptPending);
        await NextPassAsync(world);
        Assert.StartsWith("start:", world.Bots.HomeOf(bots[0])!.Place);
        Assert.False(world.Feature.AdoptPending, "every operator home is taken over: the request is done");

        // A home an operator sets afterwards is left alone again.
        await world.Bots.SetHomesAsync([new ManagedPlayerbotFeature.HomeTarget(bots[0], 0, sentinel)], 750f, "Sentinel Hill");
        await NextPassAsync(world);
        Assert.Equal("Sentinel Hill", world.Bots.HomeOf(bots[0])!.Place);
    }

    [Fact]
    public async Task ABotResetToLevelOne_LeavesItsTownHome_ForItsStartArea()
    {
        await using PopulationTestHost world = await WorldAsync(new MemoryHomeStore(), 1);
        world.Population.FreshStartHome = true;
        Guid bot = Bots(world).Single();
        await SetLevelAsync(world, bot, 15);
        await NextPassAsync(world);
        Assert.StartsWith("town:", world.Bots.HomeOf(bot)!.Place);

        await SetLevelAsync(world, bot, 1);
        await NextPassAsync(world);
        Assert.StartsWith("start:", world.Bots.HomeOf(bot)!.Place);
    }

    [Fact]
    public async Task ATownCentreThatCannotBeResolved_FallsBackToNoHome_WithOneAnomaly()
    {
        // Nothing resolves: no anchor NPC has a spawn.
        var empty = new CreatureContent([], [], [], [], []);
        await using PopulationTestHost world = await WorldAsync(new MemoryHomeStore(), 3, content: empty);
        using AnomalyCapture capture = PlayerbotAnomalies.CaptureIn(world.Host.World, n => n.Key == PlayerbotAnomalies.Key("population", "town-unresolved") && n.Detail.Contains("stormwind", StringComparison.Ordinal));
        world.Feature.TownContentSource = () => empty;
        world.Population.FreshStartHome = true;
        Guid[] bots = Bots(world);
        foreach (Guid id in bots) await SetLevelAsync(world, id, 15);
        for (int i = 0; i < 3; i++) await NextPassAsync(world);

        Assert.All(bots, id => Assert.Null(world.Bots.HomeOf(id))); // the old behaviour: no home, and not stranded anywhere new
        Assert.Equal(1, capture.Count(PlayerbotAnomalies.Key("population", "town-unresolved")));
    }

    [Fact]
    public async Task ATownMissingFromTheData_IsSkipped_AndReportedOnce()
    {
        CreatureContent content = TownContent("thelsamar", "sentinel-hill", "kharanos");
        await using PopulationTestHost world = await WorldAsync(new MemoryHomeStore(), 4, content: content);
        using AnomalyCapture capture = PlayerbotAnomalies.CaptureIn(world.Host.World, n => n.Key == PlayerbotAnomalies.Key("population", "town-unresolved") && n.Detail.Contains("thelsamar", StringComparison.Ordinal));
        world.Population.FreshStartHome = true;
        Guid[] bots = Bots(world);
        foreach (Guid id in bots) await SetLevelAsync(world, id, 12);
        for (int i = 0; i < 3; i++) await NextPassAsync(world);

        foreach (Guid id in bots)
        {
            TownPlace place = PlaceOf(world, id); // never homeless: the capital at least
            Assert.NotEqual("thelsamar", place.Secondary);
            Assert.NotEqual("sentinel-hill", place.Secondary);
        }

        // One report for the town, however many sweeps met it (the round robin meets different towns each time).
        Assert.Equal(1, capture.Count(PlayerbotAnomalies.Key("population", "town-unresolved")));
        Assert.All(capture.Notices, n => Assert.Contains("thelsamar", n.Detail, StringComparison.Ordinal));
    }

    /// <summary>A travel layer that reaches nothing without a teleport (what the default says of a town across the water).</summary>
    private sealed class NothingReachable : IPlayerbotHomeTravel
    {
        public bool CanReachWithoutTeleport(uint fromMap, Vector3 from, uint toMap, Vector3 to) => false;

        public PlayerbotHomeTeleportReason Decide(in PlayerbotHomeFacts facts, PlayerbotHomeOptions options, float teleportDistanceYards)
            => PlayerbotHomeRules.Decide(facts, options, teleportDistanceYards);
    }

    [Fact]
    public async Task WithTheTeleportOff_ATownThatNeedsOneIsNotAssigned_TheBotKeepsItsHome_WithOneInfoAnomaly()
    {
        // The travel seam says no town can be reached without a teleport (a boat or a zeppelin is in the way, whatever the reason).
        await using PopulationTestHost world = await WorldAsync(new MemoryHomeStore(), 6, o => o.Population.TownProgressionTeleport = false,
            extra: services => services.AddSingleton<IPlayerbotHomeTravel>(new NothingReachable()));
        using AnomalyCapture capture = PlayerbotAnomalies.CaptureIn(world.Host.World, n => n.Key == PlayerbotAnomalies.Key("population", "town-unreachable"));
        world.Population.FreshStartHome = true;
        Guid[] bots = Bots(world);
        await NextPassAsync(world);                                   // everyone is homed at its start area (level 1)
        string[] before = [.. bots.Select(id => world.Bots.HomeOf(id)!.Place!)];
        Assert.All(before, place => Assert.StartsWith("start:", place));
        foreach (Guid id in bots) await SetLevelAsync(world, id, 15);
        for (int i = 0; i < 3; i++) await NextPassAsync(world);

        // Not assigned, not stranded: every bot keeps the home it had, and one Info anomaly (not one per bot) says why.
        Assert.Equal(before, bots.Select(id => world.Bots.HomeOf(id)!.Place!));
        Assert.Equal(1, capture.Count(PlayerbotAnomalies.Key("population", "town-unreachable")));
        Assert.All(capture.Notices, n => Assert.Equal(AnomalySeverity.Info, n.Severity));
    }

    [Fact]
    public async Task WithTheTeleportOff_ABotTakesTheTownsItCanWalkTo_AndOnTheTeleportAnyContinentIsAssigned()
    {
        // The default travel rule (same map, no water): the bots of the test world stand on map 0, so every town they get is on map 0.
        await using PopulationTestHost walking = await WorldAsync(new MemoryHomeStore(), 12, o => o.Population.TownProgressionTeleport = false);
        walking.Population.FreshStartHome = true;
        Guid[] bots = Bots(walking);
        foreach (Guid id in bots) await SetLevelAsync(walking, id, 15);
        for (int i = 0; i < 2; i++) await NextPassAsync(walking);
        foreach (Guid id in bots)
        {
            TownPlace place = PlaceOf(walking, id);
            Assert.Equal(0u, PopulationTownProgression.Find(place.Master)!.MapId);
            Assert.Equal(0u, PopulationTownProgression.Find(place.Secondary)!.MapId); // the test world's bots all stand on map 0 (the race starts are there): the towns they can walk to are on map 0
            Assert.False(walking.Bots.FindBrain(id)!.Home!.TownTeleports);
        }
    }

    [Fact]
    public async Task ACrossContinentMaster_IsStoredOnItsOwnMap_SoTheReturnIsTheOneTeleport()
    {
        await using PopulationTestHost world = await WorldAsync(new MemoryHomeStore(), 6, o => o.Population.Factions = PopulationFactions.Horde);
        world.Population.FreshStartHome = true;
        world.Population.CapitalMajorSharePct = 100; // Orgrimmar (Kalimdor) for every Horde bot
        Guid[] bots = Bots(world);
        foreach (Guid id in bots) await SetLevelAsync(world, id, 25);
        await NextPassAsync(world);
        foreach (Guid id in bots)
        {
            PlayerbotHome home = world.Bots.HomeOf(id)!;
            Assert.Equal(1u, home.MapId);                                     // Orgrimmar, map 1
            PlayerbotHomeArea area = world.Bots.FindBrain(id)!.Home!;
            // The bots of the test world stand on map 0: the real return rules, run on the real player and area, answer with the one teleport.
            PlayerbotHomeTeleportReason reason = await world.Host.World.InvokeAsync(() =>
            {
                Player player = world.Bots.FindSession(id)!.Player!;
                Assert.Equal(0u, player.MapId);
                var home = new PlayerbotHomeReturn();
                home.Evaluate(player, area, 1000);
                return home.Judge(player, area, new PlayerbotHomeOptions());
            });
            Assert.Equal(PlayerbotHomeTeleportReason.OtherMap, reason);
        }
    }

    // ---- bots that cannot leave Teldrassil, the water, the sweep order ----

    private static Task PlaceOnTeldrassilAsync(PopulationTestHost world, Guid botId)
        => world.Host.World.InvokeAsync(() =>
        {
            Player player = world.Bots.FindSession(botId)!.Player!;
            player.MapId = 1; // inside the island (x above 8000, y above -200 on map 1): the way off is a boat
            player.X = 9802f;
            player.Y = 983f;
            return true;
        });

    [Fact]
    public async Task WithTheTeleportOff_ABotOnTeldrassil_IsNeverParkedAtADarnassusCapitalHome()
    {
        await using PopulationTestHost world = await WorldAsync(new MemoryHomeStore(), 3, o => o.Population.TownProgressionTeleport = false);
        using AnomalyCapture capture = PlayerbotAnomalies.CaptureIn(world.Host.World, n => n.Key == PlayerbotAnomalies.Key("population", "town-unreachable"));
        world.Population.FreshStartHome = true;
        Guid[] bots = Bots(world);
        foreach (Guid id in bots)
        {
            await PlaceOnTeldrassilAsync(world, id);
            await SetLevelAsync(world, id, 25);
        }

        // The state the unfixed sweep left: a capital-only home at Darnassus (every town of the level is across the water or on the kingdoms).
        Vector3 darnassus = TownCentre("darnassus");
        await world.Bots.SetHomesAsync([new ManagedPlayerbotFeature.HomeTarget(bots[0], 1, darnassus)], world.Population.CapitalHomeRadius, "town:darnassus;m=darnassus");
        for (int i = 0; i < 3; i++) await NextPassAsync(world);

        foreach (Guid id in bots)
        {
            // No town home at all: not a Darnassus one (the capital idle rule would hold the bot at the inn for good), the bot explores.
            Assert.Null(world.Bots.HomeOf(id));
            Assert.Null(world.Bots.FindBrain(id)!.Home);
            Assert.Equal(TownStay.None, PopulationTownProgression.StayOf(PopulationTownProgression.PlaceOf("darnassus", "darnassus", null)));
        }

        Assert.Equal(1, capture.Count(PlayerbotAnomalies.Key("population", "town-unreachable")));
        Assert.Contains("boat", capture.Notices.Single().Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithTheTeleportOn_ABotOnTeldrassil_GetsTheTownsOfItsBand()
    {
        await using PopulationTestHost world = await WorldAsync(new MemoryHomeStore(), 2);
        world.Population.FreshStartHome = true;
        Guid[] bots = Bots(world);
        foreach (Guid id in bots)
        {
            await PlaceOnTeldrassilAsync(world, id);
            await SetLevelAsync(world, id, 25);
        }

        await NextPassAsync(world);
        foreach (Guid id in bots) Assert.NotNull(PlaceOf(world, id).Secondary); // the one teleport takes it there
    }

    [Fact]
    public void ACapitalOnlyPlace_HasNoStay_SoTheBotExploresInsteadOfLingering()
    {
        Assert.Equal(TownStay.None, PopulationTownProgression.StayOf("town:darnassus;m=darnassus"));
        Assert.Equal(TownStay.Capital, PopulationTownProgression.StayOf("town:darnassus;m=darnassus;s=auberdine"));
        Assert.Equal(TownStay.Secondary, PopulationTownProgression.StayOf("town:auberdine;m=darnassus;s=auberdine"));
    }

    [Fact]
    public async Task WaterBetweenTheBotAndATownHome_MakesTheReturnATeleport_OnlyWhenTheProgressionMayTeleport()
    {
        await using PopulationTestHost world = await WorldAsync(new MemoryHomeStore(), 1);
        Guid bot = Bots(world).Single();
        await PlaceOnTeldrassilAsync(world, bot);
        var auberdine = new Vector3(6407f, 515f, 9f); // on the mainland, 3,400 yards away: well inside the town walk range
        (PlayerbotHomeTeleportReason On, PlayerbotHomeTeleportReason Off) reasons = await world.Host.World.InvokeAsync(() =>
        {
            Player player = world.Bots.FindSession(bot)!.Player!;
            // The test world has no map 1: the rules read the player's MapId when it is in no map object.
            Map? map = player.Map;
            player.Map = null;
            PlayerbotHomeTeleportReason Judge(bool teleports)
            {
                var area = new PlayerbotHomeArea(1, auberdine, 4500f, "town:auberdine;m=darnassus;s=auberdine")
                {
                    Stay = TownStay.Secondary, WalkRangeYards = teleports ? 4500f : 20000f, TownTeleports = teleports,
                };
                var returning = new PlayerbotHomeReturn();
                returning.Evaluate(player, area, 1000);
                return returning.Judge(player, area, new PlayerbotHomeOptions());
            }

            try { return (Judge(true), Judge(false)); }
            finally { player.Map = map; }
        });

        Assert.Equal(PlayerbotHomeTeleportReason.Distance, reasons.On);   // infinite distance: the water
        Assert.Equal(PlayerbotHomeTeleportReason.None, reasons.Off);      // the progression may not teleport: it walks (the sweep assigns no such town)
    }

    [Fact]
    public async Task TheDangerRule_TeleportsTheWayToATownHome_OnlyWhenTheProgressionMayTeleport()
    {
        await using PopulationTestHost world = await WorldAsync(new MemoryHomeStore(), 1);
        Guid bot = Bots(world).Single();
        await PlaceOnTeldrassilAsync(world, bot);
        var darnassus = new Vector3(10128f, 2225f, 1329f); // the same island, 1,300 yards away: a walk, unless an area on the way is too high
        // Level 60 everywhere but at the town itself (level 5): the way there is dangerous for the bot (the Danger rule).
        var levels = new PlayerbotAreaLevelLookup((x, y, _) => x > 10000f && y > 2000f ? 5 : 60);
        (PlayerbotHomeTeleportReason On, PlayerbotHomeTeleportReason Off) reasons = await world.Host.World.InvokeAsync(() =>
        {
            Player player = world.Bots.FindSession(bot)!.Player!;
            Map? map = player.Map;
            player.Map = null;
            PlayerbotHomeTeleportReason Judge(bool teleports)
            {
                var area = new PlayerbotHomeArea(1, darnassus, 300f, "town:darnassus;m=darnassus;s=auberdine")
                {
                    Stay = TownStay.Capital, WalkRangeYards = teleports ? 4500f : 20000f, TownTeleports = teleports, AreaLevels = levels,
                };
                var returning = new PlayerbotHomeReturn();
                returning.Evaluate(player, area, 1000);
                return returning.Judge(player, area, new PlayerbotHomeOptions());
            }

            try { return (Judge(true), Judge(false)); }
            finally { player.Map = map; }
        });

        Assert.Equal(PlayerbotHomeTeleportReason.Danger, reasons.On);
        Assert.Equal(PlayerbotHomeTeleportReason.None, reasons.Off);
    }

    [Fact]
    public async Task WithTheTeleportOff_ANightElfWithAZoneHome_KeepsItAtLevelTenAndAbove_AndIsNeverGivenATownHome()
    {
        await using PopulationTestHost world = await WorldAsync(new MemoryHomeStore(), 3, o => o.Population.TownProgressionTeleport = false);
        world.Population.FreshStartHome = true;
        Guid[] bots = Bots(world);
        await NextPassAsync(world);                                   // level 1: the start homes
        foreach (Guid id in bots)
        {
            await PlaceOnTeldrassilAsync(world, id);
            await SetLevelAsync(world, id, 6);
        }

        await NextPassAsync(world);                                   // level 6: the zone homes
        string[] zone = [.. bots.Select(id => world.Bots.HomeOf(id)!.Place!)];
        Assert.All(zone, place => Assert.StartsWith("zone:", place));
        foreach (Guid id in bots) await SetLevelAsync(world, id, 12);
        for (int i = 0; i < 3; i++) await NextPassAsync(world);

        Assert.Equal(zone, bots.Select(id => world.Bots.HomeOf(id)!.Place!));
        Assert.All(bots, id => Assert.Equal(TownStay.None, world.Bots.FindBrain(id)!.Home!.Stay));
    }

    [Fact]
    public void ABotYoungerThanTheFirstTownOfItsContinent_IsSentToNoTownOverseas()
    {
        var none = new Dictionary<string, int>();
        Assert.Null(PopulationTownProgression.PickSecondary(Team.Horde, 1, 7, none, _ => 0, _ => true));     // Kalimdor's first Horde town serves from 10
        Assert.Null(PopulationTownProgression.PickSecondary(Team.Alliance, 1, 9, none, _ => 0, _ => true));
        Assert.Equal("crossroads", PopulationTownProgression.PickSecondary(Team.Horde, 1, 10, none, _ => 0, _ => true)!.Key);
        Assert.NotNull(PopulationTownProgression.PickSecondary(Team.Horde, 0, 7, none, _ => 0, _ => true)); // Brill serves from 5
        // A bot of age whose own continent's towns are all unusable still falls back overseas.
        Assert.NotNull(PopulationTownProgression.PickSecondary(Team.Horde, 1, 30, none, _ => 0, r => PopulationTownProgression.Find(r)!.MapId == 0));
    }

    [Fact]
    public async Task AnAdoptedOperatorHome_OfABotThatLeftItsStartCountry_IsClearedAndTheRequestIsRetired()
    {
        await using PopulationTestHost world = await WorldAsync(new MemoryHomeStore(), 1);
        world.Population.FreshStartHome = true;
        Guid bot = Bots(world).Single();
        await SetLevelAsync(world, bot, 7);                           // the zone stage
        await world.Host.World.InvokeAsync(() =>
        {
            Player player = world.Bots.FindSession(bot)!.Player!;
            player.MapId = 1;                                         // not on the race's start map: out of its start country
            return true;
        });
        await world.Bots.SetHomesAsync([new ManagedPlayerbotFeature.HomeTarget(bot, 0, new Vector3(-10653f, 1166f, 35f))], 750f, "Sentinel Hill");
        world.Feature.RequestAdopt();
        await NextPassAsync(world);
        Assert.Null(world.Bots.HomeOf(bot));
        Assert.False(world.Feature.AdoptPending);
    }

    [Fact]
    public async Task ASweepThatSpendsItsAttempts_ContinuesWhereItStopped_SoNoBotStaysHomeless()
    {
        const int fleet = 30; // more than the 25 attempts of a sweep; the stays of one minute re-home every homed bot at every sweep
        await using PopulationTestHost world = await WorldAsync(new MemoryHomeStore(), fleet);
        world.Population.FreshStartHome = true;
        Guid[] bots = Bots(world);
        foreach (Guid id in bots) await SetLevelAsync(world, id, 15);

        await NextPassAsync(world);
        Assert.Equal(25, bots.Count(id => world.Bots.HomeOf(id) is not null)); // MaxHomeChangesPerSweep
        await NextPassAsync(world);
        await NextPassAsync(world);
        Assert.All(bots, id => Assert.NotNull(world.Bots.HomeOf(id)));
    }

    [Fact]
    public async Task AnAdoptRequest_ArrivingBeforeTheTownsAreKnown_StaysPending()
    {
        await using PopulationTestHost world = await WorldAsync(new MemoryHomeStore(), 1);
        world.Population.FreshStartHome = true;
        Guid bot = Bots(world).Single();
        await SetLevelAsync(world, bot, 15);
        var sentinel = new Vector3(-10653f, 1166f, 35f);
        await world.Bots.SetHomesAsync([new ManagedPlayerbotFeature.HomeTarget(bot, 0, sentinel)], 750f, "Sentinel Hill");
        world.Feature.TownContentSource = () => null; // the creature content is not installed yet
        world.Feature.RequestAdopt();
        await NextPassAsync(world);
        Assert.Equal("Sentinel Hill", world.Bots.HomeOf(bot)!.Place);
        Assert.True(world.Feature.AdoptPending, "nothing was taken over: the request must survive the sweep");

        CreatureContent content = TownContent();
        world.Feature.TownContentSource = () => content;
        await NextPassAsync(world);
        Assert.StartsWith("town:", world.Bots.HomeOf(bot)!.Place);
        Assert.False(world.Feature.AdoptPending);
    }

    [Fact]
    public void AWalkToAnInnSpotNoRouteReaches_IsGivenUp_ThenTriedAgainLater_NeverReplannedEveryThink()
    {
        var idle = new PlayerbotTownIdle();
        // Making progress: it keeps walking.
        Assert.Equal(TownIdleAction.WalkToTown, idle.Decide(TownStay.Capital, 90f, 0));
        Assert.Equal(TownIdleAction.WalkToTown, idle.Decide(TownStay.Capital, 60f, 10_000));
        Assert.Equal(TownIdleAction.WalkToTown, idle.Decide(TownStay.Capital, 30f, 25_000));
        // Stuck 6 yards from the spot (a counter, an upper floor), thinking every second: not a yard closer in WalkStallMs, the walk is given up.
        uint at = 30_000;
        Assert.Equal(TownIdleAction.WalkToTown, idle.Decide(TownStay.Capital, 6f, at));
        for (uint step = 1_000; step < PlayerbotTownIdle.WalkStallMs; step += 1_000)
            Assert.Equal(TownIdleAction.WalkToTown, idle.Decide(TownStay.Capital, 6f, at + step));
        Assert.Equal(TownIdleAction.Linger, idle.Decide(TownStay.Capital, 6f, at + PlayerbotTownIdle.WalkStallMs));
        Assert.Equal(TownIdleAction.Linger, idle.Decide(TownStay.Capital, 6f, at + PlayerbotTownIdle.WalkStallMs + 60_000));
        // After the deferral it tries again, and gives up again after another stall window.
        uint retry = at + PlayerbotTownIdle.WalkStallMs + PlayerbotTownIdle.StallDeferMs;
        Assert.Equal(TownIdleAction.WalkToTown, idle.Decide(TownStay.Capital, 6f, retry));
        for (uint step = 1_000; step < PlayerbotTownIdle.WalkStallMs; step += 1_000)
            Assert.Equal(TownIdleAction.WalkToTown, idle.Decide(TownStay.Capital, 6f, retry + step));
        Assert.Equal(TownIdleAction.Linger, idle.Decide(TownStay.Capital, 6f, retry + PlayerbotTownIdle.WalkStallMs));
    }

    [Fact]
    public void AWalkInterruptedByAnotherAction_RestartsItsProgressWindow_WhereverItResumes()
    {
        var idle = new PlayerbotTownIdle();
        Assert.Equal(TownIdleAction.WalkToTown, idle.Decide(TownStay.Capital, 40f, 0));
        // A quest, a fight or the explore window takes the bot away (no walk decision for a minute); it comes back farther from the inn.
        uint back = 60_000;
        idle.Interrupted();                                         // the brain ran a quest, a fight or an errand in between
        Assert.Equal(TownIdleAction.WalkToTown, idle.Decide(TownStay.Capital, 300f, back));
        Assert.Equal(TownIdleAction.WalkToTown, idle.Decide(TownStay.Capital, 290f, back + 2_000));
        // The same after another action was decided in between (a secondary's explore window).
        var secondary = new PlayerbotTownIdle();
        Assert.Equal(TownIdleAction.WalkToTown, secondary.Decide(TownStay.Secondary, 40f, 0));
        Assert.Equal(TownIdleAction.Explore, secondary.Decide(TownStay.Secondary, 400f, PlayerbotTownIdle.LingerMs + 1));
        Assert.Equal(TownIdleAction.WalkToTown, secondary.Decide(TownStay.Secondary, 500f, PlayerbotTownIdle.LingerMs + PlayerbotTownIdle.ExploreMs + 2_000));
        // Without an interruption, however long the pause between two walk decisions, the walk is one run and stalls on time.
        var slow = new PlayerbotTownIdle();
        Assert.Equal(TownIdleAction.WalkToTown, slow.Decide(TownStay.Capital, 300f, 0));
        Assert.Equal(TownIdleAction.Linger, slow.Decide(TownStay.Capital, 300f, 60_000)); // no progress for 60 s: given up, not reset by the pause
        // A resumed walk that really makes no progress is still given up, 20 s after its last progress.
        for (uint step = 3_000; step < 2_000 + PlayerbotTownIdle.WalkStallMs; step += 1_000)
            Assert.Equal(TownIdleAction.WalkToTown, idle.Decide(TownStay.Capital, 290f, back + step));
        Assert.Equal(TownIdleAction.Linger, idle.Decide(TownStay.Capital, 290f, back + 2_000 + PlayerbotTownIdle.WalkStallMs));
    }

    [Theory]
    [InlineData(10_000u)]
    [InlineData(16_000u)]
    [InlineData(19_999u)]
    public void AStalledWalk_ThatThinksEveryTenToSixteenSeconds_IsGivenUpOnceTheStallWindowHasPassed(uint gapMs)
    {
        // A busy server's think gap is 10 to 16 s; the stall window (20 s from the last progress) must still fire.
        var idle = new PlayerbotTownIdle();
        uint at = 100_000;
        Assert.Equal(TownIdleAction.WalkToTown, idle.Decide(TownStay.Capital, 6f, at));
        TownIdleAction last = TownIdleAction.WalkToTown;
        uint now = at;
        while (now - at < PlayerbotTownIdle.WalkStallMs)
        {
            now += gapMs;
            last = idle.Decide(TownStay.Capital, 6f, now);
            Assert.Equal(now - at >= PlayerbotTownIdle.WalkStallMs ? TownIdleAction.Linger : TownIdleAction.WalkToTown, last);
        }

        Assert.Equal(TownIdleAction.Linger, last);
    }

    [Fact]
    public async Task ATownWalk_IsNeverChainedOntoAForwardWanderLeg_OnlyAnExploreLegIs()
    {
        await using PopulationTestHost world = await WorldAsync(new MemoryHomeStore(), 1);
        PlayerbotBrain brain = world.Bots.FindBrain(Bots(world).Single())!;
        var route = new PlayerbotRoute([new Vector3(0, 0, 0), new Vector3(30, 0, 0)], 30f);
        brain.MarkLeg(route, townWalk: true);
        Assert.False(brain.CanChain(route), "a leg to the inn ends there: chaining would carry the bot past the innkeeper");
        brain.MarkLeg(route, townWalk: false);
        Assert.True(brain.CanChain(route));
        brain.MarkLeg(route, townWalk: true);
        Assert.False(brain.CanChain(route));
    }

    [Fact]
    public async Task TheTownStateOfADeletedBot_IsForgotten()
    {
        await using PopulationTestHost world = await WorldAsync(new MemoryHomeStore(), 2);
        world.Population.FreshStartHome = true;
        Guid[] bots = Bots(world);
        foreach (Guid id in bots) await SetLevelAsync(world, id, 15);
        await NextPassAsync(world);
        Assert.Equal(2, world.Feature.TownStateCount);
        Assert.True(await world.Members.DeleteAsync(bots[0])); // the bot leaves the population
        await NextPassAsync(world);
        Assert.Equal(1, world.Feature.TownStateCount);
    }

    // ---- the real world data ----

    [RealTownWorldFact]
    public void EveryAnchorNpcOfTheTable_HasASpawnOnItsMap_AndIsAnInnkeeperOrFlightMaster_RealData()
    {
        string db = Environment.GetEnvironmentVariable("ARCANECORE_TEST_WORLD_DB")!;
        var options = new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={db};Mode=ReadOnly;Pooling=False").Options;
        CreatureContent creatures;
        using (var context = new WorldDbContext(options))
            creatures = new EfCreatureDataStore(context).LoadAsync().GetAwaiter().GetResult();
        Assert.True(creatures.SpawnCount > 1000, "the database holds no creature spawns: the check would pass vacuously");

        Dictionary<string, ResolvedTown> resolved = PopulationTownProgression.Resolve(creatures);
        foreach (TownRow row in PopulationTownProgression.Rows)
        {
            Assert.True(resolved.TryGetValue(row.Key, out ResolvedTown town), $"{row.Key}: anchor entry {row.AnchorEntry} has no spawn on map {row.MapId}");
            Assert.Equal(row.MapId, creatures.GetSpawns(row.MapId, row.AnchorEntry)[0].MapId);
            CreatureTemplate template = Assert.IsType<CreatureTemplate>(creatures.FindTemplate(row.AnchorEntry));
            const uint innkeeper = 128, flightMaster = 8;
            Assert.True((template.NpcFlags & (innkeeper | flightMaster)) != 0, $"{row.Key}: {template.Name} is neither an innkeeper nor a flight master");
            Assert.True(float.IsFinite(town.Centre.X) && float.IsFinite(town.Centre.Y), row.Key);
        }

        Assert.Equal(PopulationTownProgression.Rows.Count, resolved.Count);
    }

    private sealed class MemoryHomeStore : IManagedPlayerbotHomeStore
    {
        private readonly ConcurrentDictionary<Guid, PlayerbotHome> _rows = new();

        public PlayerbotHome? Get(Guid botId) => _rows.GetValueOrDefault(botId);

        public Task<IReadOnlyList<PlayerbotHome>> LoadAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PlayerbotHome>>([.. _rows.Values.OrderBy(h => h.BotId)]);

        public Task SetAsync(PlayerbotHome home, CancellationToken cancellationToken = default)
        {
            _rows[home.BotId] = home;
            return Task.CompletedTask;
        }

        public Task<bool> ClearAsync(Guid botId, CancellationToken cancellationToken = default) => Task.FromResult(_rows.TryRemove(botId, out _));
    }
}

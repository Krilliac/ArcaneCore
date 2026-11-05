using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.City;

/// <summary>
/// The capital catalog derived from content (<see cref="PlayerbotCapitalCatalog"/>), the WMO area rows of the locator
/// (<see cref="WmoAreaMap"/>), the city options and the trip limiter (<see cref="PlayerbotCityLimiter"/>). The trip goals, bank and
/// hearthstone arrive with the capital goals lane. Unproven until the integration gate.
/// </summary>
public sealed class PlayerbotCapitalCatalogTests
{
    // --- the catalog -----------------------------------------------------------------------------------------------

    private static CreatureTemplate Npc(uint entry, uint faction, NpcFlags flags, uint trainerClass = 0, TrainerType type = TrainerType.Class)
        => new() { Entry = entry, Name = $"npc {entry}", Faction = faction, NpcFlags = (uint)flags, DisplayIds = [49], TrainerType = (uint)type, TrainerClass = (byte)trainerClass };

    private static CreatureSpawn At(uint guid, uint entry, uint map, float x, float y) => new() { Guid = guid, Entry = entry, MapId = map, X = x, Y = y, Z = 10 };

    private static FactionTemplateCatalog Factions() => new([
        new FactionTemplateRecord(1, 1, 0, 1, 1, 2),     // Human: own 1, friends 1, hostile to 2
        new FactionTemplateRecord(2, 2, 0, 2, 2, 1),     // Orc
        new FactionTemplateRecord(11, 11, 0, 1, 1, 2),   // a Stormwind NPC: Alliance
        new FactionTemplateRecord(85, 85, 0, 2, 2, 1),   // an Orgrimmar NPC: Horde
    ]);

    private static (AreaTemplate[] Areas, CreatureContent Content, GameObjectContent Objects, Func<uint, float, float, float, (uint, uint)> Locate) World()
    {
        AreaTemplate[] areas =
        [
            new(1519, 0, 0, 100, (uint)AreaFlags.Capital, 0, "Stormwind City", AreaTeams.Ally, 0),
            new(12, 0, 0, 101, 0, 1, "Elwynn Forest", AreaTeams.None, 0),
            new(1637, 1, 0, 102, (uint)AreaFlags.Capital, 0, "Orgrimmar", AreaTeams.Horde, 0),
        ];
        CreatureTemplate[] templates =
        [
            Npc(1, 11, NpcFlags.Auctioneer), Npc(2, 11, NpcFlags.Banker), Npc(3, 11, NpcFlags.Innkeeper),
            Npc(4, 11, NpcFlags.Trainer, trainerClass: 1), Npc(5, 11, NpcFlags.Vendor | NpcFlags.Repair),
            Npc(6, 11, NpcFlags.Trainer, type: TrainerType.TradeSkills),
            Npc(11, 85, NpcFlags.Auctioneer), Npc(12, 85, NpcFlags.Banker),
        ];
        // Stormwind sits at x < -8000 on map 0, Elwynn at x >= -8000; Orgrimmar is anywhere on map 1.
        CreatureSpawn[] spawns =
        [
            At(1, 1, 0, -8800, 600), At(2, 2, 0, -8900, 620), At(3, 3, 0, -8850, 560), At(4, 4, 0, -8700, 500), At(5, 5, 0, -8810, 640),
            At(6, 6, 0, -8760, 540),
            At(7, 5, 0, -7000, 500),   // a repair vendor in Elwynn: not the city's
            At(8, 5, 0, -8800, 1500),  // a vendor 900 yards away in the city's zone: out of the service area
            At(21, 11, 1, 1630, -4430), At(22, 12, 1, 1650, -4440),
        ];
        var objects = new GameObjectContent(
            [new GameObjectTemplate { Entry = 500, Type = (uint)GameObjectType.Mailbox, Name = "Mailbox" }],
            [new GameObjectSpawn { Guid = 900, Entry = 500, MapId = 0, X = -8790, Y = 610, Z = 10 },
             new GameObjectSpawn { Guid = 901, Entry = 500, MapId = 0, X = -3000, Y = 0, Z = 10 }], [], [], []);
        (uint, uint) Locate(uint map, float x, float y, float z)
            => map == 0 ? (y > 0 && x < -8000 ? (1519u, 1519u) : (12u, 12u)) : map == 1 ? (1637u, 1637u) : (0u, 0u);
        return (areas, new CreatureContent(templates, spawns, [], [], []), objects, Locate);
    }

    [Fact]
    public void Catalog_DerivesTheCapitals_FromTheCapitalFlag_NotCoordinates()
    {
        var (areas, content, objects, locate) = World();
        PlayerbotCapitalCatalog catalog = PlayerbotCapitalCatalog.Build(areas, content, objects, Factions(), locate);

        Assert.Equal([1519u, 1637u], catalog.Capitals.Select(c => c.ZoneId));
        Capital stormwind = catalog.Find(1519)!;
        Assert.Equal(0u, stormwind.MapId);
        Assert.Equal("Stormwind City", stormwind.Name);
        Assert.Equal([1u], stormwind.Of(CapitalService.Auctioneer).Select(s => s.Entry));
        Assert.Equal([2u], stormwind.Of(CapitalService.Banker).Select(s => s.Entry));
        Assert.Equal([3u], stormwind.Of(CapitalService.Innkeeper).Select(s => s.Entry));
        Assert.Equal([4u], stormwind.Of(CapitalService.ClassTrainer).Select(s => s.Entry));
        Assert.Equal((byte)1, stormwind.Of(CapitalService.ClassTrainer)[0].TrainerClass);
        Assert.Equal([6u], stormwind.Of(CapitalService.ProfessionTrainer).Select(s => s.Entry));
        Assert.Equal([5u], stormwind.Of(CapitalService.Vendor).Select(s => s.Entry));
        Assert.Equal([5u], stormwind.Of(CapitalService.Repair).Select(s => s.Entry));
        Assert.Equal([900u], stormwind.Of(CapitalService.Mailbox).Select(s => s.Spawn.Guid)); // the far mailbox is not the city's
        Assert.DoesNotContain(stormwind.Spawns, s => s.Spawn.Guid is 7 or 8); // Elwynn's vendor, and the one out of the service area
        Assert.NotNull(stormwind.Anchor);
        Assert.True(stormwind.Anchor!.Services.HasFlag(CapitalService.Auctioneer) || stormwind.Anchor.Services.HasFlag(CapitalService.Banker));
    }

    [Fact]
    public void Catalog_KnowsWhichTeamEachCapitalIsFriendlyTo()
    {
        var (areas, content, objects, locate) = World();
        PlayerbotCapitalCatalog catalog = PlayerbotCapitalCatalog.Build(areas, content, objects, Factions(), locate);
        Assert.Equal([1519u], catalog.For(Team.Alliance).Select(c => c.ZoneId));
        Assert.Equal([1637u], catalog.For(Team.Horde).Select(c => c.ZoneId));
    }

    [Fact]
    public void Catalog_WithoutFactionTemplates_FallsBackToTheAreasTeam()
    {
        var (areas, content, objects, locate) = World();
        PlayerbotCapitalCatalog catalog = PlayerbotCapitalCatalog.Build(areas, content, objects, null, locate);
        Assert.Equal([1519u], catalog.For(Team.Alliance).Select(c => c.ZoneId));
        Assert.Equal([1637u], catalog.For(Team.Horde).Select(c => c.ZoneId));
    }

    [Fact]
    public void Catalog_HasNoCapital_WhenNoZoneIsFlagged_OrTheTerrainKnowsNothing()
    {
        var (areas, content, objects, locate) = World();
        Assert.Empty(PlayerbotCapitalCatalog.Build([.. areas.Select(a => a with { Flags = 0 })], content, objects, Factions(), locate).Capitals);
        Assert.Empty(PlayerbotCapitalCatalog.Build(areas, content, objects, Factions(), (_, _, _, _) => (0u, 0u)).Capitals);
    }

    [Fact]
    public void Catalog_At_FindsTheCityByPosition()
    {
        var (areas, content, objects, locate) = World();
        PlayerbotCapitalCatalog catalog = PlayerbotCapitalCatalog.Build(areas, content, objects, Factions(), locate);
        Assert.Equal(1519u, catalog.At(0, new Vector3(-8800, 600, 10))!.ZoneId);
        Assert.Null(catalog.At(0, new Vector3(-7000, 500, 10)));
        Assert.Null(catalog.At(1, new Vector3(-8800, 600, 10)));
    }

    [Fact]
    public void Catalog_Build_PassesTheMapAndHeightToTheLocator()
    {
        var (areas, content, objects, _) = World();
        var seen = new List<(uint Map, float Z)>();
        PlayerbotCapitalCatalog.Build(areas, content, objects, Factions(), (map, _, _, z) =>
        {
            seen.Add((map, z));
            return (0u, 0u);
        });
        Assert.NotEmpty(seen);
        Assert.All(seen, s => Assert.Equal(10f, s.Z));
        Assert.Contains(seen, s => s.Map == 0);
        Assert.Contains(seen, s => s.Map == 1);
    }

    [Fact]
    public void Catalog_Build_CancelsBetweenLookups()
    {
        var (areas, content, objects, locate) = World();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => PlayerbotCapitalCatalog.Build(areas, content, objects, Factions(), locate, cancel.Token));
    }

    // --- the WMO area rows -------------------------------------------------------------------------------------------

    [Fact]
    public void WmoAreaMap_FindsTheExactTriple_ElseTheSameRootAndGroupUnderAnyNameSet_ElseZero()
    {
        WmoAreaMap map = WmoAreaMap.Of([(100, 0, 5, 1537u), (100, 2, 5, 1538u), (200, 0, 1, 0u)]);
        Assert.Equal(2, map.Count);
        Assert.Equal(1537u, map.AreaOf(new ModelAreaInfo(0, 0, 100, 5, 0f)));
        Assert.Equal(1538u, map.AreaOf(new ModelAreaInfo(0, 2, 100, 5, 0f)));
        Assert.Equal(1537u, map.AreaOf(new ModelAreaInfo(0, 9, 100, 5, 0f)));   // loose: the first name set seen for the root and group
        Assert.Equal(0u, map.AreaOf(new ModelAreaInfo(0, 0, 100, 6, 0f)));
        Assert.Equal(0u, map.AreaOf(new ModelAreaInfo(0, 0, 200, 1, 0f)));      // a row without an area is not kept
        Assert.Equal(0, WmoAreaMap.Empty.Count);
    }

    // --- options ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Options_DefaultsAndValidation()
    {
        var options = new PlayerbotCityOptions();
        Assert.True(options.Enabled);
        Assert.False(options.BindInCapital);
        Assert.Equal(6, options.MaxTripsPerCapital);
        Assert.Equal(4, options.MaxStartsPerMinute);
        options.Validate();
        Assert.Throws<InvalidOperationException>(() => new PlayerbotCityOptions { VisitIntervalMinutes = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotCityOptions { MaxTripYards = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotCityOptions { MaxTripYards = float.NaN }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotCityOptions { MinGoldForAuctionTrip = -1 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotCityOptions { MinLevel = 61 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotCityOptions { MaxTripsPerCapital = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotCityOptions { MaxStartsPerMinute = 601 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotOptions { City = null! }.Validate());
    }

    // --- the limiter -----------------------------------------------------------------------------------------------

    [Fact]
    public void Limiter_HoldsAtMostMaxTripsPerCapital_AndReleaseGivesTheSlotBack()
    {
        var limiter = new PlayerbotCityLimiter();
        var city = new PlayerbotCityOptions { MaxTripsPerCapital = 2, MaxStartsPerMinute = 600 };
        object a = new(), b = new(), c = new();
        Assert.True(limiter.TryAcquire(a, 1519, 0, city));
        Assert.True(limiter.TryAcquire(b, 1519, 1, city));
        Assert.False(limiter.TryAcquire(c, 1519, 2, city));
        Assert.True(limiter.TryAcquire(c, 1637, 2, city));   // another capital has its own cap
        Assert.Equal(2, limiter.ActiveTrips(1519));
        limiter.Release(a);
        Assert.Equal(1, limiter.ActiveTrips(1519));
        Assert.True(limiter.TryAcquire(c, 1519, 3, city));
    }

    [Fact]
    public void Limiter_AHolderAskingAgain_DoesNotTakeASecondSlot()
    {
        var limiter = new PlayerbotCityLimiter();
        var city = new PlayerbotCityOptions { MaxTripsPerCapital = 1, MaxStartsPerMinute = 600 };
        object a = new();
        Assert.True(limiter.TryAcquire(a, 1519, 0, city));
        Assert.True(limiter.TryAcquire(a, 1519, 10, city));
        Assert.Equal(1, limiter.ActiveTrips(1519));
    }

    [Fact]
    public void Limiter_LimitsStartsPerMinute_ThenAllowsAgainOnceTheWindowPassed()
    {
        var limiter = new PlayerbotCityLimiter();
        var city = new PlayerbotCityOptions { MaxTripsPerCapital = 100, MaxStartsPerMinute = 2 };
        Assert.True(limiter.TryAcquire(new object(), 1519, 0, city));
        Assert.True(limiter.TryAcquire(new object(), 1519, 1_000, city));
        Assert.False(limiter.TryAcquire(new object(), 1519, 59_999, city));
        Assert.True(limiter.TryAcquire(new object(), 1519, 60_000, city));   // the first start left the window
        Assert.False(limiter.TryAcquire(new object(), 1519, 60_500, city));
    }

    [Fact]
    public void Limiter_ExpiresASlotItsHolderNeverReleased_AfterTheTripCap()
    {
        var limiter = new PlayerbotCityLimiter();
        var city = new PlayerbotCityOptions { MaxTripsPerCapital = 1, MaxStartsPerMinute = 600 };
        Assert.True(limiter.TryAcquire(new object(), 1519, 0, city));
        Assert.False(limiter.TryAcquire(new object(), 1519, PlayerbotCityLimiter.TripCapMs, city));
        Assert.True(limiter.TryAcquire(new object(), 1519, PlayerbotCityLimiter.TripCapMs + 1, city));
    }

    [Fact]
    public void Limiter_SurvivesTheClockWrapping()
    {
        var limiter = new PlayerbotCityLimiter();
        var city = new PlayerbotCityOptions { MaxTripsPerCapital = 1, MaxStartsPerMinute = 600 };
        uint near = uint.MaxValue - 100;
        Assert.True(limiter.TryAcquire(new object(), 1519, near, city));
        Assert.False(limiter.TryAcquire(new object(), 1519, 200, city));   // 301 ms later across the wrap: still held
    }
}

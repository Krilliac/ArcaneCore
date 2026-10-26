using System.Collections.Frozen;
using System.Numerics;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.WorldState;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.WorldBossRealRows;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// The world-boss catalogue (<see cref="PlayerbotWorldBossCatalogue"/>, docs/areas/playbots-groups.md, World-boss raids): the known bosses with
/// their locators, the derive rule pinned to the real world database rows (<see cref="WorldBossRealRows"/>), the operator's included and excluded
/// entries and size overrides, and the frozen snapshot the map-thread kill talk reads.
/// </summary>
public sealed class PlayerbotWorldBossCatalogueTests
{
    private static readonly IReadOnlySet<uint> NoEventSpawns = new HashSet<uint>();

    private static PlayerbotWorldBossCatalogue Build(PlayerbotWorldBossOptions? options = null, CreatureContent? content = null, IReadOnlySet<uint>? eventSpawns = null)
        => PlayerbotWorldBossCatalogue.Build(options ?? new PlayerbotWorldBossOptions(), content ?? Content(), Factions(), eventSpawns ?? NoEventSpawns);

    private static uint[] Derived(CreatureContent content, IReadOnlySet<uint>? eventSpawns = null)
        => [.. PlayerbotWorldBossCatalogue.Derive(content, Factions(), eventSpawns ?? NoEventSpawns).Select(e => e.Entry).Order()];

    // --- the derive rule over the real rows --------------------------------------------------------------------------

    [Fact]
    public void TheRealRank3ContinentRows_AreTwentyOne_AndTheDeriveRuleKeepsExactlyAzuregosKazzakAndTeremus()
    {
        Assert.Equal(21, Rank3ContinentSpawns.Length); // the world database's rank-3 spawns on maps 0 and 1: nothing dropped from the fixture
        Assert.All(Rank3ContinentSpawns, r => Assert.True(r.Rank == 3 && r.MapId is 0 or 1));

        Assert.Equal(new uint[] { Teremus, Azuregos, Kazzak }.Order(), Derived(Content()));
    }

    /// <summary>Every other rank-3 continent row the content has is left out, whatever rule drops it (leaders, Benedictus, Varimathras, Anachronos, the Spirit, Rajaxx, the Colossi).</summary>
    [Theory]
    [InlineData(1284u)]  // Archbishop Benedictus: UnitFlags 512, friendly faction
    [InlineData(1748u)]  // Highlord Bolvar Fordragon: racial leader
    [InlineData(2425u)]  // Varimathras: friendly to the Horde (faction 68), no flag of the other rules
    [InlineData(2784u)]
    [InlineData(3057u)]
    [InlineData(3516u)]
    [InlineData(4949u)]
    [InlineData(4968u)]  // Lady Jaina Proudmoore: friendly to the Alliance (faction 1096)
    [InlineData(7937u)]  // High Tinker Mekkatorque: friendly to the Alliance (faction 64)
    [InlineData(7999u)]
    [InlineData(10181u)]
    [InlineData(10540u)] // Vol'jin: not a racial leader in the data; only his faction (125) excludes him
    [InlineData(15192u)] // Anachronos: UnitFlags 512
    [InlineData(15341u)] // General Rajaxx
    [InlineData(15481u)] // Spirit of Azuregos: UnitFlags 768
    [InlineData(15740u)]
    [InlineData(15741u)]
    [InlineData(15742u)]
    public void EveryOtherRank3ContinentCreature_IsNotInTheCatalogue(uint entry)
    {
        PlayerbotWorldBossCatalogue catalogue = Build();
        Assert.False(catalogue.IsWorldBossEntry(entry), Of(entry).Name);
        Assert.Null(catalogue.Find(entry));
    }

    /// <summary>The faction rule alone: Varimathras, Jaina, Mekkatorque and Vol'jin carry none of the flags, so the real faction templates decide.</summary>
    [Theory]
    [InlineData(2425u)]
    [InlineData(4968u)]
    [InlineData(7937u)]
    [InlineData(10540u)]
    public void AFactionFriendlyToEitherTeam_ExcludesWhenNothingElseDoes(uint entry)
    {
        Row row = Of(entry);
        Assert.False(row.RacialLeader);
        Assert.Equal(0u, row.UnitFlags & (2 | 256 | 512));
        CreatureContent hostile = Content(r => r.Entry == entry ? r with { Faction = 14 } : r); // the same row, hostile faction 14 (Kazzak's)
        Assert.Contains(entry, Derived(hostile));
        Assert.DoesNotContain(entry, Derived(Content()));
    }

    /// <summary>Each rule on its own: Teremus, a real row that passes, fails once one column of it changes.</summary>
    [Theory]
    [InlineData("rank")]
    [InlineData("leader")]
    [InlineData("instance-bind")]
    [InlineData("flag-2")]
    [InlineData("flag-256")]
    [InlineData("flag-512")]
    [InlineData("friendly-faction")]
    [InlineData("other-map")]
    public void EachDeriveRule_ExcludesTeremusWhenItsColumnChanges(string rule)
    {
        CreatureContent content = Content(r => r.Entry != Teremus ? r : rule switch
        {
            "rank" => r with { Rank = 2 },
            "leader" => r with { RacialLeader = true },
            "instance-bind" => r with { ExtraFlags = r.ExtraFlags | ExtraInstanceBind },
            "flag-2" => r with { UnitFlags = 2 },
            "flag-256" => r with { UnitFlags = 256 },
            "flag-512" => r with { UnitFlags = 512 },
            "friendly-faction" => r with { Faction = 68 },
            "other-map" => r with { MapId = 530 },
            _ => throw new ArgumentOutOfRangeException(nameof(rule)),
        });
        Assert.DoesNotContain(Teremus, Derived(content));
        Assert.Contains(Teremus, Derived(Content())); // control: unchanged, it is in
    }

    [Fact]
    public void ASpawnPlacedByAGameEvent_IsNotDerived()
    {
        Assert.Contains(Teremus, Derived(Content()));
        Assert.DoesNotContain(Teremus, Derived(Content(), new HashSet<uint> { Of(Teremus).SpawnGuid }));
        Assert.Contains(Teremus, Derived(Content(), new HashSet<uint> { Of(Teremus).SpawnGuid + 1 })); // another spawn's event does not matter
    }

    // --- known entries and locators ----------------------------------------------------------------------------------

    [Fact]
    public void TheCatalogue_HoldsTheKnownBossesAndTheDerivedOne_WithTheirLevelsAndLocators()
    {
        PlayerbotWorldBossCatalogue catalogue = Build();
        Assert.Equal(new[] { Azuregos, Teremus, Kazzak, Ysondre, Lethon, Emeriss, Taerar }.Order(), catalogue.Entries.Select(e => e.Entry).Order());

        PlayerbotWorldBossEntry azuregos = catalogue.Find(Azuregos)!;
        Assert.Equal("Azuregos", azuregos.Name);
        Assert.Equal((byte)63, azuregos.Level);
        Assert.False(azuregos.Derived);
        Assert.Equal(PlayerbotWorldBossLocatorKind.Spawn, azuregos.Locator.Kind);
        Assert.Equal((1u, 52349u), (azuregos.Locator.MapId, azuregos.Locator.SpawnGuid));
        Assert.Equal(PositionOf(Azuregos), azuregos.Locator.Position);

        PlayerbotWorldBossEntry kazzak = catalogue.Find(Kazzak)!;
        Assert.Equal(PlayerbotWorldBossLocatorKind.Spawn, kazzak.Locator.Kind);
        Assert.Equal((0u, 2723u), (kazzak.Locator.MapId, kazzak.Locator.SpawnGuid));
        Assert.Equal((byte)63, kazzak.Level);

        PlayerbotWorldBossEntry teremus = catalogue.Find(Teremus)!;
        Assert.True(teremus.Derived);
        Assert.Equal((byte)60, teremus.Level);
        Assert.Equal(PlayerbotWorldBossLocatorKind.Spawn, teremus.Locator.Kind);
        Assert.Equal((0u, 134380u), (teremus.Locator.MapId, teremus.Locator.SpawnGuid));
    }

    [Fact]
    public void TheFourDragons_AreFoundAtTheirPortals_NotBySpawn()
    {
        PlayerbotWorldBossCatalogue catalogue = Build();
        uint[] dragons = [Ysondre, Lethon, Emeriss, Taerar];
        for (int portal = 0; portal < dragons.Length; portal++)
        {
            PlayerbotWorldBossEntry dragon = catalogue.Find(dragons[portal])!;
            Assert.Equal(PlayerbotWorldBossLocatorKind.Portal, dragon.Locator.Kind);
            Assert.Equal(portal, dragon.Locator.PortalIndex); // NightmareDragonsFeature.Portals order: the entry's own portal
            Assert.Equal(NightmareDragonsFeature.Portals[portal].MapId, dragon.Locator.MapId);
            Assert.Equal((byte)63, dragon.Level);
            Assert.False(dragon.Derived);
        }
    }

    [Fact]
    public void WithoutDerivation_OnlyTheSixKnownBossesRemain()
    {
        PlayerbotWorldBossCatalogue catalogue = Build(new PlayerbotWorldBossOptions { DeriveFromContent = false });
        Assert.Equal(new[] { Azuregos, Kazzak, Ysondre, Lethon, Emeriss, Taerar }.Order(), catalogue.Entries.Select(e => e.Entry).Order());
        Assert.False(catalogue.IsWorldBossEntry(Teremus));
    }

    [Fact]
    public void WithNoCreatureContent_TheCatalogueIsEmpty_AndNothingThrows()
    {
        PlayerbotWorldBossCatalogue catalogue = Build(content: CreatureContent.Empty);
        Assert.Empty(catalogue.Entries);
        Assert.False(catalogue.IsWorldBossEntry(Azuregos));
        Assert.Empty(catalogue.EntrySet);
    }

    [Fact]
    public void AKnownBossWhoseSpawnIsMissing_IsLeftOut_NotGuessed()
    {
        CreatureContent noKazzakSpawn = new(
            [.. Rank3ContinentSpawns.Select(r => r.Template), .. Dragons.Select(r => r.Template)],
            [.. Rank3ContinentSpawns.Where(r => r.Entry != Kazzak).Select(r => r.Spawn)], [], [], []);
        PlayerbotWorldBossCatalogue catalogue = Build(content: noKazzakSpawn);
        Assert.False(catalogue.IsWorldBossEntry(Kazzak));
        Assert.True(catalogue.IsWorldBossEntry(Azuregos));
    }

    // --- operator entries ---------------------------------------------------------------------------------------------

    private const uint PlacedEntry = 990810, SpawnedEntry = 990811;

    private static CreatureTemplate Template(uint entry, byte level) => new() { Entry = entry, Name = "Event Boss " + entry, MinLevel = level, MaxLevel = level, Faction = 14, Rank = 3 };

    private static CreatureContent WithTestBosses() => Content(
        extraTemplates: [Template(PlacedEntry, 61), Template(SpawnedEntry, 59)],
        extraSpawns: [new CreatureSpawn { Guid = 880811, Entry = SpawnedEntry, MapId = 1, X = 10f, Y = 20f, Z = 30f }]);

    [Fact]
    public void IncludedEntryAtMap_IsPlaced_WithNoSpawnNeeded_AndTakesItsLevelFromTheTemplate()
    {
        PlayerbotWorldBossCatalogue catalogue = Build(new PlayerbotWorldBossOptions { DeriveFromContent = false, IncludedEntries = $"{PlacedEntry}@0" }, WithTestBosses());
        PlayerbotWorldBossEntry placed = catalogue.Find(PlacedEntry)!;
        Assert.Equal(PlayerbotWorldBossLocatorKind.Placed, placed.Locator.Kind);
        Assert.Equal(0u, placed.Locator.MapId);
        Assert.Equal((byte)61, placed.Level);
        Assert.False(placed.Derived);
        Assert.True(catalogue.IsWorldBossEntry(PlacedEntry));
        Assert.Empty(catalogue.Warnings);
    }

    [Fact]
    public void IncludedBareEntry_NeedsAContinentSpawn_AndUsesIt()
    {
        PlayerbotWorldBossCatalogue catalogue = Build(new PlayerbotWorldBossOptions { DeriveFromContent = false, IncludedEntries = $"{SpawnedEntry},{PlacedEntry}" }, WithTestBosses());

        PlayerbotWorldBossEntry spawned = catalogue.Find(SpawnedEntry)!;
        Assert.Equal(PlayerbotWorldBossLocatorKind.Spawn, spawned.Locator.Kind);
        Assert.Equal((1u, 880811u), (spawned.Locator.MapId, spawned.Locator.SpawnGuid));
        Assert.Equal(new Vector3(10f, 20f, 30f), spawned.Locator.Position);

        // The bare entry with no continent spawn is logged and ignored, not guessed.
        Assert.Null(catalogue.Find(PlacedEntry));
        Assert.Contains(catalogue.Warnings, w => w.Contains(PlacedEntry.ToString(), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("990810@2")]   // not a continent
    [InlineData("990810@36")]
    [InlineData("990899@0")]   // no template
    [InlineData("990899")]
    [InlineData("abc")]
    public void AnInvalidIncludedEntry_IsIgnoredWithAWarning_AndTheRestStillWork(string value)
    {
        PlayerbotWorldBossCatalogue catalogue = Build(new PlayerbotWorldBossOptions { DeriveFromContent = false, IncludedEntries = value + $",{SpawnedEntry}" }, WithTestBosses());
        Assert.False(catalogue.IsWorldBossEntry(PlacedEntry));
        Assert.Single(catalogue.Warnings);
        Assert.True(catalogue.IsWorldBossEntry(SpawnedEntry));
        Assert.True(catalogue.IsWorldBossEntry(Azuregos)); // the known bosses are unaffected
    }

    [Fact]
    public void ExcludedEntries_RemoveKnownDerivedAndIncludedBosses_AndExclusionWins()
    {
        var options = new PlayerbotWorldBossOptions { IncludedEntries = $"{PlacedEntry}@0", ExcludedEntries = $"{Azuregos},{Teremus},{PlacedEntry},{Ysondre}" };
        PlayerbotWorldBossCatalogue catalogue = Build(options, WithTestBosses());
        Assert.False(catalogue.IsWorldBossEntry(Azuregos));
        Assert.False(catalogue.IsWorldBossEntry(Teremus));
        Assert.False(catalogue.IsWorldBossEntry(PlacedEntry));
        Assert.False(catalogue.IsWorldBossEntry(Ysondre));
        Assert.Equal(new[] { Kazzak, Lethon, Emeriss, Taerar, SpawnedEntry }.Order(), catalogue.Entries.Select(e => e.Entry).Order());
    }

    [Fact]
    public void SizeOverrides_SetThePerBossTargetSize_AndOthersKeepTheDefault()
    {
        PlayerbotWorldBossCatalogue catalogue = Build(new PlayerbotWorldBossOptions { SizeOverrides = $"{Azuregos}:25,{Teremus}:5,{Kazzak}:40" });
        Assert.Equal(25, catalogue.Find(Azuregos)!.SizeOverride);
        Assert.Equal(5, catalogue.Find(Teremus)!.SizeOverride);
        Assert.Equal(40, catalogue.Find(Kazzak)!.SizeOverride);
        Assert.Null(catalogue.Find(Lethon)!.SizeOverride);
    }

    // --- the frozen snapshot ----------------------------------------------------------------------------------------

    [Fact]
    public void TheSnapshot_IsAFrozenSet_AnswersByEntry_AndIsNotRankThree()
    {
        PlayerbotWorldBossCatalogue catalogue = Build();
        Assert.IsAssignableFrom<FrozenSet<uint>>(catalogue.EntrySet);
        Assert.Equal(7, catalogue.EntrySet.Count);
        Assert.True(catalogue.IsWorldBossEntry(Azuregos));
        Assert.True(catalogue.IsWorldBossEntry(Teremus));
        Assert.True(catalogue.IsWorldBossEntry(Taerar));
        // Rank 3 on a continent, and still not a raid boss: a faction leader and Benedictus stay silent in the kill talk.
        Assert.False(catalogue.IsWorldBossEntry(1748));
        Assert.False(catalogue.IsWorldBossEntry(1284));
        Assert.False(catalogue.IsWorldBossEntry(0));
        Assert.False(catalogue.IsWorldBossEntry(uint.MaxValue));
    }

    [Fact]
    public void ABuildIsIndependentOfTheNext_SoAPublishedSnapshotNeverChanges()
    {
        PlayerbotWorldBossCatalogue first = Build();
        PlayerbotWorldBossCatalogue second = Build(new PlayerbotWorldBossOptions { ExcludedEntries = Azuregos.ToString() });
        Assert.True(first.IsWorldBossEntry(Azuregos));
        Assert.False(second.IsWorldBossEntry(Azuregos));
        Assert.Equal(7, first.Entries.Count);
        Assert.Equal(6, second.Entries.Count);
    }
}

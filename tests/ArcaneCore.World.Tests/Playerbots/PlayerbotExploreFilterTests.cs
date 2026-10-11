using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotExploreFilterTests
{
    private static CreatureTemplate Template(uint entry, byte level = 8) => new()
    { Entry = entry, Name = $"creature {entry}", MinLevel = level, MaxLevel = level, Faction = 14 };

    private static CreatureSpawn Spawn(uint guid, uint entry, uint map, float x) => new()
    { Guid = guid, Entry = entry, MapId = map, X = x, Y = 0, Z = 20 };

    private static uint? Choose(CreatureContent content, uint map = 0, uint level = 8)
        => new PlayerbotExploreAtlas(content).Find(map, level, new Vector3(0, 0, 20),
            _ => true, (_, _) => false, out _)?.Spawn.Guid;

    [Fact]
    public void HigherLevelSpawnCloserThanRightLevelSpawn_IsSkipped()
    {
        var content = new CreatureContent(
            [Template(1, 18), Template(2, 20), Template(3)],
            [Spawn(1, 1, 0, 25), Spawn(2, 2, 0, 45), Spawn(3, 3, 0, 250)], [], [], []);
        Assert.Equal(3u, Choose(content));
    }

    [Theory]
    [InlineData("npc")]
    [InlineData("elite")]
    [InlineData("rare")]
    [InlineData("critter")]
    [InlineData("civilian")]
    [InlineData("not-selectable")]
    public void ExcludedNearSpawn_IsSkippedForOrdinaryGrind(string reason)
    {
        CreatureTemplate excluded = reason switch
        {
            "npc" => Template(1) with { NpcFlags = 1 },
            "elite" => Template(1) with { Rank = 1 },
            "rare" => Template(1) with { Rank = 4 },
            "critter" => Template(1) with { CreatureType = CreatureAiFactory.CritterType },
            "civilian" => Template(1) with { Civilian = true },
            "not-selectable" => Template(1) with { UnitFlags = (uint)UnitFlags.NotSelectable },
            _ => throw new ArgumentOutOfRangeException(nameof(reason)),
        };
        var content = new CreatureContent([excluded, Template(2)],
            [Spawn(1, 1, 0, 25), Spawn(2, 2, 0, 250)], [], [], []);
        Assert.Equal(2u, Choose(content));
    }

    [Fact]
    public void OtherContinentAndInstanceSpawns_AreNeverMapZeroCandidates()
    {
        var content = new CreatureContent([Template(1)],
            [Spawn(1, 1, 1, 25), Spawn(2, 1, 33, 35), Spawn(3, 1, 0, 250)], [], [], []);
        var atlas = new PlayerbotExploreAtlas(content);
        Assert.Equal(3u, atlas.Find(0, 8, new Vector3(0, 0, 20), _ => true, (_, _) => false, out _)?.Spawn.Guid);
        Assert.Equal(1u, atlas.Find(1, 8, new Vector3(0, 0, 20), _ => true, (_, _) => false, out _)?.Spawn.Guid);
        Assert.Null(atlas.Find(33, 8, new Vector3(0, 0, 20), _ => true, (_, _) => false, out _));
        Assert.False(atlas.HasIndexedMap(33));
    }

    [Fact]
    public void DenseFriendlyThreats_DoNotHideANearbyHostileThreat()
    {
        var friendly = Template(1, 60) with { Faction = 15 };
        var hostile = Template(2, 20);
        var rightLevel = Template(3);
        CreatureSpawn[] friendlySpawns = [.. Enumerable.Range(1, 30).Select(i => Spawn((uint)i, 1, 0, 100 + i % 5))];
        var content = new CreatureContent([friendly, hostile, rightLevel],
            [.. friendlySpawns, Spawn(31, 3, 0, 110), Spawn(32, 2, 0, 125)], [], [], []);
        var atlas = new PlayerbotExploreAtlas(content);
        PlayerbotExploreAtlas.Candidate site = Assert.IsType<PlayerbotExploreAtlas.Candidate>(atlas.Find(
            0, 8, new Vector3(0, 0, 20), _ => true, (_, _) => false, out _));
        Assert.True(atlas.HasStrongHazard(site, 8, faction => faction == 14));
        var withoutHostile = new PlayerbotExploreAtlas(new CreatureContent([friendly, rightLevel],
            [.. friendlySpawns, Spawn(31, 3, 0, 110)], [], [], []));
        PlayerbotExploreAtlas.Candidate cleanSite = Assert.IsType<PlayerbotExploreAtlas.Candidate>(withoutHostile.Find(
            0, 8, new Vector3(0, 0, 20), _ => true, (_, _) => false, out _));
        Assert.False(withoutHostile.HasStrongHazard(cleanSite, 8, faction => faction == 14));
    }
}

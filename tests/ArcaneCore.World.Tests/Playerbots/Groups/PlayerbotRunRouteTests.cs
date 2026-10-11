using System.Numerics;
using ArcaneCore.World.Playerbots.Groups;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>Which trash a run pulls next (<see cref="PlayerbotRunRoute.NextPull"/>): positions in, a decision out.</summary>
public sealed class PlayerbotRunRouteTests
{
    private const uint BossEntry = 639, TrashEntry = 634;

    /// <summary>A straight corridor of four points, 50 yards apart.</summary>
    private static readonly Vector3[] Corridor = [new(0, 0, 0), new(50, 0, 0), new(100, 0, 0), new(150, 0, 0)];

    private static PlayerbotRunCreature Trash(ulong guid, float x, float y, bool alive = true, bool hostile = true, bool inCombat = false, uint entry = TrashEntry)
        => new(guid, entry, new Vector3(x, y, 0), alive, hostile, inCombat);

    [Fact]
    public void NothingAlongTheRoute_IsNoPull()
    {
        Assert.Null(PlayerbotRunRoute.NextPull(Corridor, 0, [], BossEntry));
        Assert.Null(PlayerbotRunRoute.NextPull([], 0, [Trash(1, 0, 0)], BossEntry));
        Assert.Null(PlayerbotRunRoute.NextPull(Corridor, 0, [Trash(1, 50, PlayerbotRunRoute.RouteClearYards + 1f)], BossEntry));
    }

    [Fact]
    public void ACreatureWithinTheClearingDistanceOfTheRoute_IsPulled()
    {
        PlayerbotRunPull pull = PlayerbotRunRoute.NextPull(Corridor, 0, [Trash(1, 50, PlayerbotRunRoute.RouteClearYards)], BossEntry)!;

        Assert.Equal(1ul, pull.Lead.Guid);
        Assert.Equal(1, pull.RouteIndex);
        Assert.Equal(1, pull.PacksAhead);
        Assert.Equal([1ul], pull.Pack.Select(c => c.Guid));
    }

    [Fact]
    public void TheBoss_TheDead_TheNeutral_AndWhatIsFightingAlready_AreNeverPulled()
    {
        PlayerbotRunCreature[] creatures =
        [
            Trash(1, 50, 0, entry: BossEntry),
            Trash(2, 50, 2, alive: false),
            Trash(3, 50, 4, hostile: false),
            Trash(4, 50, 6, inCombat: true),
        ];

        Assert.Null(PlayerbotRunRoute.NextPull(Corridor, 0, creatures, BossEntry));

        // Without a boss entry (0) the boss entry is just another creature.
        Assert.Equal(1ul, PlayerbotRunRoute.NextPull(Corridor, 0, creatures, 0)!.Lead.Guid);
    }

    [Fact]
    public void Packs_AreMetInRouteOrder_WhateverTheOrderTheyAreListedIn()
    {
        PlayerbotRunCreature far = Trash(1, 150, 3), middle = Trash(2, 100, 3), near = Trash(3, 50, 3);

        PlayerbotRunPull pull = PlayerbotRunRoute.NextPull(Corridor, 0, [far, middle, near], BossEntry)!;

        Assert.Equal(3ul, pull.Lead.Guid);
        Assert.Equal(1, pull.RouteIndex);
        Assert.Equal(3, pull.PacksAhead);
    }

    [Fact]
    public void AtTheSameRoutePoint_TheNearestToTheRouteLeads_ThenTheLowestGuid()
    {
        PlayerbotRunPull byDistance = PlayerbotRunRoute.NextPull(Corridor, 0, [Trash(1, 50, 9), Trash(2, 50, 4)], BossEntry)!;
        Assert.Equal(2ul, byDistance.Lead.Guid);

        PlayerbotRunPull byGuid = PlayerbotRunRoute.NextPull(Corridor, 0, [Trash(9, 50, 5), Trash(7, 50, -5)], BossEntry)!;
        Assert.Equal(7ul, byGuid.Lead.Guid);
    }

    [Fact]
    public void RoutePointsAlreadyPassed_AreNotClearedBehindTheGroup()
    {
        PlayerbotRunCreature behind = Trash(1, 0, 3), ahead = Trash(2, 100, 3);

        PlayerbotRunPull pull = PlayerbotRunRoute.NextPull(Corridor, 2, [behind, ahead], BossEntry)!;

        Assert.Equal(2ul, pull.Lead.Guid);
        Assert.Equal(2, pull.RouteIndex);
        Assert.Equal(1, pull.PacksAhead);
        Assert.Null(PlayerbotRunRoute.NextPull(Corridor, 2, [behind], BossEntry));
    }

    [Fact]
    public void TheProgress_IsClampedToTheRoute()
    {
        PlayerbotRunCreature atEnd = Trash(1, 150, 3);

        Assert.Equal(1ul, PlayerbotRunRoute.NextPull(Corridor, 99, [atEnd], BossEntry)!.Lead.Guid);
        Assert.Equal(1ul, PlayerbotRunRoute.NextPull(Corridor, -5, [atEnd], BossEntry)!.Lead.Guid);
    }

    [Fact]
    public void APack_IsTheLeadAndWhatStandsWithinPackYardsOfIt_AndAnotherPackIsAnotherPull()
    {
        PlayerbotRunCreature lead = Trash(1, 50, 5);
        PlayerbotRunCreature mate = Trash(2, 50, 5 + PlayerbotRunRoute.PackYards - 1f);   // 9 yards from the lead, 14 from the route
        PlayerbotRunCreature other = Trash(3, 50, -14.9f);                                  // 19.9 yards from the lead: its own pack

        PlayerbotRunPull pull = PlayerbotRunRoute.NextPull(Corridor, 0, [other, mate, lead], BossEntry)!;

        Assert.Equal(1ul, pull.Lead.Guid);
        Assert.Equal([1ul, 2ul], pull.Pack.Select(c => c.Guid));
        Assert.Equal(2, pull.PacksAhead);
    }
}

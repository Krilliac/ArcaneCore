using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Playerbots.Qa;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>
/// <see cref="PlayerbotQaContentFacts.HasWaypointPath"/> answers the "path N missing for entry E" relay warning. A cmangos
/// <c>waypoint_path</c> row is stored under entry 0 (<see cref="CreatureContent.WaypointPathBit"/>) and is shared by path id alone;
/// path ids 0 and 1 exist globally and are not this creature's <c>creature_movement_template</c> path (#329, #278).
/// </summary>
public sealed class PlayerbotQaWaypointFactsTests
{
    private const uint Entry = 5697;

    private static readonly CreatureWaypoint Point = new(1, 1, 2, 3, 0, 0);

    private readonly record struct Loaded(PlayerbotQaContentFacts Facts, CreatureContent Content);

    private static Loaded Load(params (uint Entry, uint PathId, CreatureWaypoint Point)[] paths)
    {
        var content = new CreatureContent(
            [new CreatureTemplate { Entry = Entry, Name = "Relay walker", DisplayIds = [1] }],
            [], [], [], [], entryWaypoints: paths);
        ServiceProvider services = new ServiceCollection()
            .AddSingleton(sp =>
            {
                var creatures = new CreatureWorldFeature(sp, NullLogger<CreatureWorldFeature>.Instance);
                creatures.Install(content);
                return creatures;
            })
            .BuildServiceProvider();
        return new Loaded(new PlayerbotQaContentFacts(services), content);
    }

    /// <summary>The unrelated shared paths the bug used to treat as this entry's path.</summary>
    private static (uint Entry, uint PathId, CreatureWaypoint Point)[] SharedPaths { get; } =
    [
        (CreatureContent.WaypointPathEntry, CreatureContent.WaypointPathBit | 0u, Point),
        (CreatureContent.WaypointPathEntry, CreatureContent.WaypointPathBit | 1u, Point),
    ];

    [Fact]
    public void ASharedWaypointPath_IsNotTheEntrysPath_AndPathZeroFollowsTheDefaultResolve()
    {
        Loaded bare = Load(SharedPaths);
        Assert.True(bare.Facts.Available);
        Assert.NotEmpty(bare.Content.GetWaypointPath(1));
        Assert.NotEmpty(bare.Content.GetWaypointPath(0));
        Assert.Empty(bare.Content.GetEntryWaypoints(Entry, 1));
        Assert.Empty(bare.Content.ResolveWaypointPath(0, Entry).Points);

        Assert.False(bare.Facts.HasWaypointPath(Entry, 1));
        Assert.False(bare.Facts.HasWaypointPath(Entry, 0));

        QaSignal missing = QaData.Parse(
            "relay script 4021 uses unsupported MOVEMENT waypoint path 1 missing for entry 5697; that step is skipped");
        Assert.DoesNotContain("loaded now", PlayerbotQaClassifier.Classify(missing, bare.Facts).Detail, StringComparison.Ordinal);

        Loaded withDefault = Load([.. SharedPaths, (Entry, 0u, Point)]);
        Assert.NotEmpty(withDefault.Content.ResolveWaypointPath(0, Entry).Points);
        Assert.True(withDefault.Facts.HasWaypointPath(Entry, 0));
        Assert.False(withDefault.Facts.HasWaypointPath(Entry, 1));

        Loaded ownPath = Load([.. SharedPaths, (Entry, 1u, Point)]);
        Assert.Empty(ownPath.Content.ResolveWaypointPath(0, Entry).Points);
        Assert.False(ownPath.Facts.HasWaypointPath(Entry, 0));
        Assert.True(ownPath.Facts.HasWaypointPath(Entry, 1));
    }
}

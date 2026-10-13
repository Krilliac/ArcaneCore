using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Risk;

/// <summary>
/// A place the bot already died or fled in (bot QA #446, #454). Death-loop zones refused a route that never left them; the
/// ordinary death and retreat marks did not, and target choice did not look at them either, so the next creature of the camp,
/// still standing on the corpse, was pulled again.
/// </summary>
public sealed class PlayerbotPlaceHazardRouteTests
{
    private static readonly Vector3 At = new(1900f, -450f, 35f);

    [Fact]
    public void ARouteThatStaysInsideADeathMark_IsRefused_WalkingOutIsNot()
    {
        var hazards = new PlayerbotHazards();
        hazards.Add(0, At, PlayerbotHazards.DeathYards, nowMs: 1_000, seconds: 300, "death");

        Assert.Equal("death", hazards.FirstOnRoute(0, [At, At + new Vector3(10f, 0f, 0f)], 1_000)!.Value.Kind);
        Assert.Null(hazards.FirstOnRoute(0, [At, At + new Vector3(PlayerbotHazards.DeathYards + 15f, 0f, 0f)], 1_000));
        Assert.True(hazards.CoversDeathLoop(0, At + new Vector3(10f, 0f, 0f), 1_000));
        Assert.False(hazards.CoversDeathLoop(0, At + new Vector3(PlayerbotHazards.DeathYards + 1f, 0f, 0f), 1_000));
        Assert.False(hazards.CoversDeathLoop(1, At, 1_000));
    }

    [Fact]
    public void ARouteThatStaysInsideARetreatMark_IsRefused_AndIsNotAPull()
    {
        var hazards = new PlayerbotHazards();
        hazards.Add(0, At, PlayerbotHazards.RetreatYards, nowMs: 1_000, seconds: 300, "retreat");

        Assert.Equal("retreat", hazards.FirstOnRoute(0, [At, At + new Vector3(8f, 4f, 0f)], 1_000)!.Value.Kind);
        Assert.True(hazards.CoversDeathLoop(0, At + new Vector3(8f, 4f, 0f), 1_000));
        Assert.Null(hazards.FirstOnRoute(0, [At, At + new Vector3(PlayerbotHazards.RetreatYards + 15f, 0f, 0f)], 1_000));
    }

    [Fact]
    public void ARouteIntoADeathMarkFromOutside_IsStillRefused()
    {
        var hazards = new PlayerbotHazards();
        hazards.Add(0, At, PlayerbotHazards.DeathYards, nowMs: 1_000, seconds: 300, "death");
        Vector3 outside = At + new Vector3(PlayerbotHazards.DeathYards + 20f, 0f, 0f);

        Assert.Equal("death", hazards.FirstOnRoute(0, [outside, At], 1_000)!.Value.Kind);
    }

    [Fact]
    public void ALethalReachTheBotAlreadyStandsIn_IsStillAWayOut_AndNotAPlaceMark()
    {
        var hazards = new PlayerbotHazards();
        hazards.Add(0, At, 20f, nowMs: 1_000, seconds: 300, "lethal", new ObjectGuid(0xF130000000000001UL));

        Assert.Null(hazards.FirstOnRoute(0, [At, At + new Vector3(5f, 0f, 0f)], 1_000));
        Assert.False(hazards.CoversDeathLoop(0, At, 1_000));
        Assert.Equal("lethal", hazards.FirstOnRoute(0, [At + new Vector3(40f, 0f, 0f), At], 1_000)!.Value.Kind);
    }

    [Fact]
    public void AnExpiredDeathMark_IsNeitherARouteNorAPull()
    {
        var hazards = new PlayerbotHazards();
        hazards.Add(0, At, PlayerbotHazards.DeathYards, nowMs: 1_000, seconds: 300, "death");

        Assert.Null(hazards.FirstOnRoute(0, [At, At + new Vector3(10f, 0f, 0f)], 1_000 + 300_000));
        Assert.False(hazards.CoversDeathLoop(0, At, 1_000 + 300_000));
    }
}

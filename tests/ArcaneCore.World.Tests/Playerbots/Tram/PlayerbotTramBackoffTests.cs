using System.Numerics;
using ArcaneCore.Kernel.WorldData.Transports;
using ArcaneCore.World.Playerbots;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Tram;

/// <summary>
/// A failed tram trip holds back only the entrance it began at (<see cref="PlayerbotTransitBackoff"/>, the travel schedule's
/// <see cref="PlayerbotTravelOptions.TransitBackoffSeconds"/>): the other entrance is taken at once, and the failed one again once its backoff
/// ran out. No hold of the bot's whole tram use. Through <see cref="PlayerbotTram.TryBegin"/> after a real failure (no car runs).
/// </summary>
public sealed class PlayerbotTramBackoffTests(ITestOutputHelper output)
{
    private static readonly Vector3 IronforgeGoal = new(-4918.9f, -982.5f, 501.5f); // the Commons, by the Ironforge exit
    private static readonly Vector3 StormwindGoal = TramTestData.StormwindExitLanding + new Vector3(-150f, 100f, 0f);

    private static PlayerbotOptions Options() => new() { Enabled = true, ThinkIntervalMs = 100, Population = { TramMinLevel = 0 } };

    [Fact]
    public async Task AfterAFailedTrip_TheOtherEntranceIsTakenAtOnce_AndTheFailedOneOnceItsBackoffEnds()
    {
        await using TramTestWorld world = await TramTestWorld.StartAsync(animation: new TransportAnimationCatalog([(4170u, new TransportAnimationNode(0, 0, 0, 0))]));
        await world.PlaceAsync(0, TramTestData.StormwindExitLanding);
        PlayerbotOptions options = Options();
        var brain = new PlayerbotBrain(world.Session, options);
        Assert.True(await world.OnWorldAsync(() => PlayerbotTram.TryBegin(world.Session, world.Player, IronforgeGoal, TramTestData.ZoneAt)));
        string? stall = null;
        for (int step = 0; step < 2_000 && stall is null; step++)
        {
            stall = await world.TickAsync(() =>
            {
                int before = brain.TransitFailureCount;
                brain.Update(100);
                return brain.TransitFailureCount != before ? brain.LastTransitFailure : null;
            });
        }

        output.WriteLine(stall ?? "(none)");
        Assert.StartsWith("tram: no tram car runs", stall);
        brain.Stop();
        uint failedAt = await world.OnWorldAsync(() => world.Host.World.NowMs);

        // The other entrance, from the Ironforge side to Stormwind: begun at once.
        await world.PlaceAsync(0, TramTestData.IronforgeExitLanding);
        Assert.True(await world.OnWorldAsync(() => PlayerbotTram.IsWayTo(world.Session, world.Player, StormwindGoal, TramTestData.ZoneAt)),
            "the fixture's Stormwind goal is not a tram goal from Ironforge");
        Assert.True(await world.OnWorldAsync(() => PlayerbotTram.TryBegin(world.Session, world.Player, StormwindGoal, TramTestData.ZoneAt)),
            "the Ironforge entrance was held back by the failure at Stormwind's");
        Assert.False(await world.OnWorldAsync(() => PlayerbotTram.IsHeldOff(world.Session, world.Player, StormwindGoal, TramTestData.ZoneAt)));
        await world.OnWorldAsync(() => PlayerbotTram.Cancel(world.Player));

        // The failed entrance: held back (and said so) until its backoff ends, then taken again.
        await world.PlaceAsync(0, TramTestData.StormwindExitLanding);
        Assert.False(await world.OnWorldAsync(() => PlayerbotTram.TryBegin(world.Session, world.Player, IronforgeGoal, TramTestData.ZoneAt)), "retried at once");
        Assert.True(await world.OnWorldAsync(() => PlayerbotTram.IsHeldOff(world.Session, world.Player, IronforgeGoal, TramTestData.ZoneAt)));
        uint now = await world.OnWorldAsync(() => world.Host.World.NowMs);
        uint backoff = (uint)options.Travel.TransitBackoffSeconds * 1000;
        uint wait = unchecked(failedAt + backoff + 10_000 - now);
        if (wait is > 0 and < 600_000) await world.AdvanceAsync(wait);
        Assert.True(await world.OnWorldAsync(() => world.Host.World.NowMs - failedAt) < PlayerbotTram.RetryAfterFailureMs,
            "the clock ran past the old ten-minute hold: the test proves nothing");
        Assert.False(await world.OnWorldAsync(() => PlayerbotTram.IsHeldOff(world.Session, world.Player, IronforgeGoal, TramTestData.ZoneAt)));
        Assert.True(await world.OnWorldAsync(() => PlayerbotTram.TryBegin(world.Session, world.Player, IronforgeGoal, TramTestData.ZoneAt)),
            "the Stormwind entrance was still held back after its backoff");
    }
}

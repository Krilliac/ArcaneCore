using System.Collections.Concurrent;
using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.AntiCheat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Protocol;
using ArcaneCore.World.AntiCheat;
using ArcaneCore.World.Tests.Playerbots.Tram;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Transports;

/// <summary>
/// The Deeprun Tram cars for players (docs/areas/transports.md, "Elevators and trams"): every player on the tram map has all six cars,
/// whatever its distance from their spawns, as vmangos keeps its elevators map-wide (ObjectMgr.cpp:2667-2668, Map.cpp:520-600, 1720-1733);
/// a rider halfway through the tunnel keeps the car under it; and the anticheat judges a rider by where the car is now, not by its spawn.
/// Before, the cars were grid objects seen within 100 yards of their spawn: a rider lost its car about a hundred yards out, a player at
/// one platform never saw the cars of the other, and every rider past 600 yards was a fake transport moving at a speed hack's pace.
/// </summary>
public sealed class TramCarVisibilityTests
{
    private static IEnumerable<ObjectGuid> CarGuids(GameObjectMapSystem objects)
        => objects.GameObjects.Where(g => g.Type == GameObjectType.Transport).Select(g => g.Guid);

    [Fact]
    public async Task APlayerAtThePlatform_HasAllSixCars_TheOtherPlatformsToo()
    {
        await using TramTestWorld world = await TramTestWorld.StartAsync();
        await world.PlaceAsync(TramTestData.Tram, TramTestData.StormwindPlatform);
        await world.AdvanceAsync(200);
        (int cars, int mapWide, int seen) = await world.OnWorldAsync(() =>
        {
            GameObjectMapSystem objects = world.TramObjects()!;
            return (CarGuids(objects).Count(), objects.MapWideElevators.Count, CarGuids(objects).Count(world.Player.VisibleObjects.Contains));
        });

        Assert.Equal(6, cars);
        Assert.Equal(6, mapWide);
        Assert.Equal(6, seen);
    }

    [Fact]
    public async Task ARiderHalfwayThroughTheTunnel_KeepsTheCarUnderIt()
    {
        await using TramTestWorld world = await TramTestWorld.StartAsync();
        await world.PlaceAsync(TramTestData.Tram, TramTestData.StormwindPlatform);
        await world.AdvanceAsync(200);
        // The Stormwind-home car 176082 (synthetic animation: 47 s out to the Ironforge end) half way there; it exists without the
        // map-wide rule too (its spawn is in the platform's grid), so this is the car a rider would lose.
        await world.AdvanceAsync(23_500);
        (ObjectGuid car, Vector3 at) = await world.OnWorldAsync(() =>
        {
            GameObject tram = world.TramObjects()!.GameObjects.Single(g => g.Entry == 176082);
            (float x, float y, float z) = world.TramObjects()!.ElevatorPosition(tram)!.Value;
            return (tram.Guid, new Vector3(x, y, z));
        });
        Assert.InRange(at.Y, 1000f, 1600f); // far from both platforms

        await world.PlaceAsync(TramTestData.Tram, at with { Z = at.Z - 11f });
        await world.AdvanceAsync(200);
        Assert.True(await world.OnWorldAsync(() => world.Player.VisibleObjects.Contains(car)), "the rider lost the car under it");
    }

    [Fact]
    public async Task ARiderFarFromTheCarsSpawn_IsNotAFakeTransport_ButAClaimFarFromTheCarStillIs()
    {
        await using TramTestWorld world = await TramTestWorld.StartAsync();
        var seen = new ConcurrentQueue<AntiCheatFinding>();
        AntiCheatFeature anticheat = world.Host.WorldServices.GetRequiredService<AntiCheatFeature>();
        anticheat.FindingRecorded += (_, finding, _, _) => seen.Enqueue(finding);
        await world.OnWorldAsync(() => anticheat.ApplyOptions(new AntiCheatOptions
        {
            ExemptManagedBots = false, TerrainChecks = false, BaselineGapMs = 600_000, SpeedClock = { Enabled = false },
        }));
        await world.PlaceAsync(TramTestData.Tram, TramTestData.StormwindPlatform);
        await world.AdvanceAsync(23_700);

        // Aboard car 176080 1,200 yards from its spawn: heartbeats where the car really is, 500 ms apart, as the car carries the bot.
        GameObject car = await world.OnWorldAsync(() => world.TramObjects()!.GameObjects.Single(g => g.Entry == 176080));
        uint time = 1_000;
        for (int beat = 0; beat < 6; beat++)
        {
            bool stored = await world.OnWorldAsync(() =>
            {
                (float x, float y, float z) = world.TramObjects()!.ElevatorPosition(car)!.Value;
                return Heartbeat(world, time, new Vector3(x, y, z - 11f), car.Guid.Value);
            });
            Assert.True(stored, "heartbeat aboard refused");
            time += 500;
            await world.AdvanceAsync(500);
        }

        Assert.DoesNotContain(seen, f => f.Type is AntiCheatViolation.Flag or AntiCheatViolation.Speed or AntiCheatViolation.Teleport);

        // The same claim 700 yards from where the car is: a fake transport (the check still runs).
        Vector3 away = await world.OnWorldAsync(() => new Vector3(world.Player.X + 700f, world.Player.Y, world.Player.Z));
        await world.OnWorldAsync(() => Heartbeat(world, time, Vector3.Zero, 0, clear: true));
        time += 500;
        await world.AdvanceAsync(4_000);
        await world.OnWorldAsync(() => Heartbeat(world, time += 4_000, away, car.Guid.Value));
        Assert.Contains(seen, f => f.Type == AntiCheatViolation.Flag);
    }

    private static bool Heartbeat(TramTestWorld world, uint time, Vector3 at, ulong car, bool clear = false)
    {
        Player bot = world.Player;
        MovementInfo movement = bot.Movement;
        movement.Time = time;
        if (clear)
        {
            movement.Flags &= ~MovementFlags.OnTransport;
            movement.TransportGuid = 0;
        }
        else
        {
            movement.Flags = MovementFlags.OnTransport;
            movement.X = at.X;
            movement.Y = at.Y;
            movement.Z = at.Z;
            movement.TransportGuid = car;
            movement.TransportX = movement.TransportY = 0;
            movement.TransportZ = -11f;
        }

        var packet = new PacketWriter(64);
        movement.Write(packet);
        return world.Session.TryManagedAction(WorldOpcode.MsgMoveHeartbeat, packet.ToArray());
    }
}

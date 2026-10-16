using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.GameObjects;
using Xunit;

namespace ArcaneCore.Game.Tests.GameObjects;

/// <summary><c>gameobject/use-refused-loop</c>: five refusals by one player on one object inside the window; the table that tracks them is bounded.</summary>
public sealed class UseRefusedLoopAnomalyTests
{
    private static (WorldRuntime World, GameObjectMapSystem System, Player Player) Create(ulong playerId)
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var system = new GameObjectMapSystem(map, new GameObjectContent([], [], [], [], []));
        map.AddUpdater(system);
        return (world, system, TestWorld.CreatePlayer((uint)playerId, 0, 0, new FakeSession()));
    }

    [Fact]
    public void FiveRefusalsInsideTheWindow_AreReported_AndSpacedOutRefusalsAreNot()
    {
        (WorldRuntime world, GameObjectMapSystem system, Player player) = Create(601);
        using (world)
        {
            var burstGo = ObjectGuid.WithEntry(HighGuid.GameObject, 4_000_001, 1);
            var spacedGo = ObjectGuid.WithEntry(HighGuid.GameObject, 4_000_002, 2);
            using AnomalyCapture burst = AnomalyLedger.Shared.Capture(n => n.Key == "gameobject/use-refused-loop" && n.Subject.Contains(burstGo.ToString()));
            using AnomalyCapture spaced = AnomalyLedger.Shared.Capture(n => n.Key == "gameobject/use-refused-loop" && n.Subject.Contains(spacedGo.ToString()));

            for (int i = 0; i < GameObjectMapSystem.UseRefusedLoopThreshold; i++)
            {
                system.RecordUseAttemptOutcome(player, burstGo, null, GameObjectUseResult.Locked);
            }

            for (int i = 0; i < GameObjectMapSystem.UseRefusedLoopThreshold - 1; i++)
            {
                system.RecordUseAttemptOutcome(player, spacedGo, null, GameObjectUseResult.Locked);
                world.RunTick(11_000);
            }

            Assert.True(burst.Any("gameobject/use-refused-loop"), burst.Describe());
            Assert.False(spaced.Any("gameobject/use-refused-loop"), spaced.Describe());
        }
    }

    [Fact]
    public void TheRefusalTable_IsBounded_WhateverTheNumberOfDistinctObjects()
    {
        (WorldRuntime world, GameObjectMapSystem system, Player player) = Create(602);
        using (world)
        {
            for (uint i = 1; i <= 5 * GameObjectMapSystem.MaxTrackedRefusedUseAttempts; i++)
            {
                system.RecordUseAttemptOutcome(player, ObjectGuid.WithEntry(HighGuid.GameObject, 4_100_000 + i, i), null, GameObjectUseResult.TooFar);
                Assert.True(system.TrackedRefusedUseAttempts <= GameObjectMapSystem.MaxTrackedRefusedUseAttempts);
            }

            // An expired episode is pruned before the hard bound is applied.
            world.RunTick(11_000);
            system.RecordUseAttemptOutcome(player, ObjectGuid.WithEntry(HighGuid.GameObject, 4_200_000, 1), null, GameObjectUseResult.TooFar);
            Assert.True(system.TrackedRefusedUseAttempts <= GameObjectMapSystem.MaxTrackedRefusedUseAttempts);
        }
    }
}

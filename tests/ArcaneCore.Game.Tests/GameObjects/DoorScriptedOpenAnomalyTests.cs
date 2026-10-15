using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.GameObjects;
using Xunit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;

namespace ArcaneCore.Game.Tests.GameObjects;

/// <summary><c>door/state-desync</c> must not judge a door a script opened (ScriptedInstance.OpenIf sets only the state) and a later toggle closed.</summary>
public sealed class DoorScriptedOpenAnomalyTests
{
    private const uint ScriptedDoor = 948100;

    [Fact]
    public void ADoorOpenedByAScript_AndClosedByAToggle_ReportsNoStateDesync()
    {
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == "door/state-desync" && n.Subject.Contains($"entry {ScriptedDoor} "));
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var system = new GameObjectMapSystem(map, new GameObjectContent([GoTemplate(ScriptedDoor, GameObjectType.Door)], [GoSpawn(1, ScriptedDoor, 3, 0)], [], [], []));
        map.AddUpdater(system);
        using (world)
        {
            world.AddPlayer(TestWorld.CreatePlayer(1, 0, 0, new FakeSession()));
            world.RunTick(50);
            GameObject door = system.GameObjects.Single();

            door.State = GameObjectState.Active; // ScriptedInstance.OpenIf: an encounter-done door starts open
            world.RunTick(100);
            Assert.Equal(GameObjectUseResult.Ok, system.ToggleDoorOrButton(door)); // DoUseDoorOrButton closes it again
            world.RunTick(100);

            Assert.Equal(GameObjectState.Ready, door.State);
            Assert.Equal(GameObjectLootState.Activated, door.LootState);
            Assert.Empty(capture.Notices);
        }
    }
}

using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using Xunit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTypeRig;

namespace ArcaneCore.Game.Tests.GameObjects;

/// <summary>The ScriptDevAI pTrapSearching seam (mangos-classic GameObject.cpp:466-473): <see cref="IGameObjectAi.AcceptsTrapTarget"/>.</summary>
public sealed class GameObjectTrapTargetFilterTests
{
    private sealed class RefusesAi(ObjectGuid refused) : IGameObjectAi
    {
        public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;

        public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs)
        {
        }

        public bool AcceptsTrapTarget(GameObjectMapSystem objects, GameObject go, Unit candidate) => candidate.Guid != refused;
    }


    /// <summary>A script that does not override <see cref="IGameObjectAi.AcceptsTrapTarget"/>: the interface default must accept everyone.</summary>
    private sealed class DefaultFilterAi : IGameObjectAi
    {
        public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;

        public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs)
        {
        }
    }

    private sealed class RefusesEveryoneAi : IGameObjectAi
    {
        public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;

        public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs)
        {
        }

        public bool AcceptsTrapTarget(GameObjectMapSystem objects, GameObject go, Unit candidate) => false;
    }

    [Fact]
    public void AScriptThatKeepsTheDefaultFilterLeavesTheTrapTakingTheNearestPlayer()
    {
        GameObjectTypeRig rig = Create([GoSpawn(1, FireTrap, 3, 0)]);
        (Player near, _) = rig.Join(1, 1, 0);
        rig.Join(2, 0, 0);
        rig.System.RegisterAi(FireTrap, new DefaultFilterAi());

        rig.Spells.Casts.Clear();
        rig.Seconds(2);
        Assert.Equal((Unit)near, Assert.Single(rig.Spells.Casts).Target);
    }

    [Fact]
    public void AnEnvironmentalTrapWhoseScriptRefusesEveryPlayerDoesNotFire()
    {
        GameObjectTypeRig rig = Create([GoSpawn(1, FireTrap, 3, 0)]);
        rig.Join(1, 1, 0);
        rig.Join(2, 0, 0);
        rig.System.RegisterAi(FireTrap, new RefusesEveryoneAi());

        rig.Spells.Casts.Clear();
        rig.Seconds(4);
        Assert.Empty(rig.Spells.Casts);
    }

    [Fact]
    public void AnEnvironmentalTrapSkipsAPlayerItsScriptRefusesAndTakesTheNextNearest()
    {
        GameObjectTypeRig rig = Create([GoSpawn(1, FireTrap, 3, 0)]);
        (Player near, _) = rig.Join(1, 1, 0);
        (Player next, _) = rig.Join(2, 0, 0);
        rig.System.RegisterAi(FireTrap, new RefusesAi(near.Guid));

        rig.Spells.Casts.Clear();
        rig.Seconds(2);
        Assert.Equal((Unit)next, Assert.Single(rig.Spells.Casts).Target);
    }
}

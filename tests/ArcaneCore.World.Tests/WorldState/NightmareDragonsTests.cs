using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Status = ArcaneCore.World.WorldState.NightmareDragonsFeature.DragonStatus;

namespace ArcaneCore.World.Tests.WorldState;

/// <summary>The Dragons of Nightmare rotation (vmangos HardcodedEvents.cpp:160-332).</summary>
public sealed class NightmareDragonsTests
{
    private const long Day = 24 * 60 * 60;

    private sealed class Rig
    {
        public Status[] States { get; } = [Status.Absent, Status.Absent, Status.Absent, Status.Absent];
        public List<(int Portal, uint Entry)> Spawned { get; } = [];
        public List<int> Despawned { get; } = [];
        public long Now { get; set; } = 1_000_000;
        public NightmareDragonsFeature Feature { get; }

        public Rig()
        {
            ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
            Feature = new NightmareDragonsFeature(provider.GetRequiredService<IServiceScopeFactory>())
            {
                NowUnix = () => Now,
                Random = new Random(7),
            };
            Feature.StatusOverride = i => States[i];
            Feature.SpawnOverride = (i, entry) => { Spawned.Add((i, entry)); States[i] = Status.Alive; };
            Feature.DespawnOverride = i => { Despawned.Add(i); States[i] = Status.Absent; };
        }

        public void KillAll() => Array.Fill(States, Status.Dead);
    }

    [Fact]
    public void FirstUpdate_StartsTheEvent_WithEachPortalsOwnDragon()
    {
        var rig = new Rig();
        rig.Feature.Update();
        Assert.True(rig.Feature.State.Active);
        Assert.Equal([(0, 14887u), (1, 14888u), (2, 14889u), (3, 14890u)], rig.Spawned);
    }

    [Fact]
    public void EventWaitsTheStopDelay_ThenShufflesAndSchedulesFourToSevenDaysAhead()
    {
        var rig = new Rig();
        rig.Feature.Update();
        rig.States[0] = Status.Dead;
        rig.Feature.Update();
        Assert.Equal(1, rig.Feature.State.KilledMask);
        Assert.Equal(NightmareDragonsState.DefaultStopDelay, rig.Feature.State.RequiredUpdates);

        rig.KillAll();
        for (uint i = 0; i < NightmareDragonsState.DefaultStopDelay; i++)
        {
            rig.Feature.Update();
            Assert.True(rig.Feature.State.Active);
        }

        Assert.Equal(0u, rig.Feature.State.RequiredUpdates);
        Assert.Empty(rig.Despawned);
        rig.Feature.Update();
        NightmareDragonsState s = rig.Feature.State;
        Assert.False(s.Active);
        Assert.Equal([0, 1, 2, 3], rig.Despawned);
        Assert.InRange(s.RespawnUnix, rig.Now + (4 * Day), rig.Now + (7 * Day));
        Assert.Equal(NightmareDragonsState.DefaultStopDelay, s.RequiredUpdates);
        Assert.Equal([14887u, 14888u, 14889u, 14890u], s.Permutation.Order());
        Assert.Equal(0, s.KilledMask);
    }

    [Fact]
    public void NextSpawn_UsesThePermutation_OnlyAfterTheRespawnTime()
    {
        var rig = new Rig();
        rig.Feature.State = new NightmareDragonsState([14890, 14889, 14888, 14887], rig.Now + 100, 20, false, 0);
        rig.Feature.Update();
        Assert.Empty(rig.Spawned);
        rig.Now += 101;
        rig.Feature.Update();
        Assert.Equal([(0, 14890u), (1, 14889u), (2, 14888u), (3, 14887u)], rig.Spawned);
    }

    [Fact]
    public void ADeadDragonStaysDead_ButALivingOneWhoseGridUnloadedComesBack()
    {
        var rig = new Rig();
        rig.Feature.Update();
        rig.States[0] = Status.Dead;
        rig.Feature.Update();
        rig.Spawned.Clear();
        rig.States[0] = Status.Absent; // corpse gone with its grid
        rig.States[1] = Status.Absent; // a living dragon's grid unloaded
        rig.Feature.Update();
        Assert.Equal([(1, 14888u)], rig.Spawned);
    }

    [Fact]
    public void Portals_AreTheVmangosSpawns()
    {
        Assert.Equal([0u, 1u, 0u, 1u], NightmareDragonsFeature.Portals.Select(p => p.MapId));
        Assert.Equal([14887u, 14888u, 14889u, 14890u], NightmareDragonsFeature.Portals.Select(p => p.DefaultEntry));
    }
}

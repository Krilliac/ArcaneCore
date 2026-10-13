using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Holidays;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.GameObjects;
using Xunit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;

namespace ArcaneCore.Game.Tests.Holidays;

/// <summary>
/// go_cheer_speaker's ten-minute window (vmangos fireworks_show.cpp). The 00:10:30 burst is ten big rockets and then the
/// pool goes back to the full show, so the next hour and the next New Year are not stuck on the big pool.
/// </summary>
public sealed class CheerSpeakerShowTests : IDisposable
{
    private readonly WorldRuntime _world;
    private readonly GameObjectMapSystem _objects;
    private readonly GameObject _speaker;
    private readonly List<bool> _big = [];
    private DateTime _now = new(2026, 1, 1, 0, 0, 0);
    private readonly CheerSpeakerAi _show;

    public CheerSpeakerShowTests()
    {
        _world = TestWorld.CreateRuntime();
        Map map = _world.GetMap(0);
        _objects = new GameObjectMapSystem(map, new GameObjectContent(
            [GoTemplate(CheerSpeakerAi.GoCheerSpeaker, GameObjectType.Generic)],
            [GoSpawn(1, CheerSpeakerAi.GoCheerSpeaker, 0, 0)], [], [], []));
        map.AddUpdater(_objects);
        _world.AddPlayer(TestWorld.CreatePlayer(1, 0, 0, new FakeSession(1)));
        _speaker = Assert.Single(_objects.GameObjects);
        _show = new CheerSpeakerAi(_ => true) { Now = () => _now, Random = new Random(1), LaunchedBig = _big.Add };
    }

    public void Dispose() => _world.Dispose();

    [Fact]
    public void The_new_year_burst_does_not_keep_later_hours_on_the_big_pool()
    {
        At(new DateTime(2026, 1, 1, 0, 0, 0));
        At(new DateTime(2026, 1, 1, 0, 0, 1), 1000);
        Assert.False(Assert.Single(_big)); // the hour opens on the full show

        At(new DateTime(2026, 1, 1, 0, 10, 0));
        At(new DateTime(2026, 1, 1, 0, 10, 30));
        _big.Clear();
        At(new DateTime(2026, 1, 1, 0, 10, 31), 1000);
        Assert.Equal(10, _big.Count);
        Assert.All(_big, big => Assert.True(big));

        _big.Clear();
        At(new DateTime(2026, 1, 1, 1, 0, 0));
        At(new DateTime(2026, 1, 1, 1, 0, 1), 1000);
        Assert.False(Assert.Single(_big)); // an hour later the show is not stuck on big rockets

        At(new DateTime(2026, 1, 1, 1, 10, 0));
        At(new DateTime(2027, 1, 1, 0, 10, 30));
        _big.Clear();
        At(new DateTime(2027, 1, 1, 0, 10, 31), 1000);
        Assert.Equal(10, _big.Count); // the next New Year still gets its burst
        Assert.All(_big, big => Assert.True(big));
    }

    private void At(DateTime now, uint diffMs = 50)
    {
        _now = now;
        _show.Update(_objects, _speaker, diffMs);
    }
}

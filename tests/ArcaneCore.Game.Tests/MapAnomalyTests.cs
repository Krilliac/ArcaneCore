using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Game.Tests;

/// <summary>H1 outcome checks of Map (subsystem map).</summary>
public sealed class MapAnomalyTests
{
    private sealed class Flaky(Func<int, bool> fails) : IMapUpdater
    {
        private int _calls;

        public void Update(Map map, uint diffMs)
        {
            if (fails(++_calls)) throw new InvalidOperationException("boom");
        }

        public void OnPlayerRemoved(Map map, ArcaneCore.Game.Entities.Player player)
        {
        }
    }

    private static (WorldRuntime, Map) Create(int limit)
    {
        var world = new WorldRuntime(new WorldRuntimeOptions { MaxConsecutiveUpdaterFaults = limit }, new RecordingSaveQueue(), NullLogger<WorldRuntime>.Instance);
        return (world, world.GetMap(0));
    }

    [Fact]
    public void UpdaterIsolatedByTheBreaker_IsReported_FlakyOneBelowTheLimitIsNot()
    {
        using AnomalyCapture cap = AnomalyLedger.Shared.Capture(n => n.Subject.Contains("Flaky") && n.Subsystem == "map");
        (WorldRuntime w2, Map m2) = Create(3);
        m2.AddUpdater(new Flaky(c => c % 3 != 0));
        for (int i = 0; i < 12; i++) w2.RunTick(50);
        Assert.Equal(0, cap.Count("map/updater-isolated"));

        (WorldRuntime w1, Map m1) = Create(3);
        m1.AddUpdater(new Flaky(_ => true));
        for (int i = 0; i < 6; i++) w1.RunTick(50);
        Assert.Equal(1, cap.Count("map/updater-isolated"));
    }

    [Fact]
    public void NonFinitePosition_IsReportedOncePerEpisode_FiniteMovesAreNot()
    {
        (_, Map map, _, _) = CombatTestKit.CreateWorld();
        var unit = new CombatTestUnit();
        unit.Spawn(map, 1, 1);
        string id = unit.Guid.Value.ToString("X");
        using AnomalyCapture cap = AnomalyLedger.Shared.Capture(n => n.Subsystem == "map" && n.Subject.Contains(id));
        unit.X = 5;
        unit.X = 6;
        Assert.Empty(cap.Notices);
        unit.X = float.NaN;
        unit.X = float.NaN;
        Assert.Equal(1, cap.Count("map/non-finite-position"));
        Assert.Equal(AnomalySeverity.Error, cap.Notices[0].Severity);
        unit.X = 7;
        unit.X = float.PositiveInfinity;
        Assert.Equal(2, cap.Count("map/non-finite-position"));
    }
}

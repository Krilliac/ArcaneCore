using System.Diagnostics;
using ArcaneCore.Game.Maps;
using Xunit;

namespace ArcaneCore.Game.Tests.GridTerrain;

/// <summary>The slow-update evidence's accumulation (<see cref="MapUpdateDiagnostics"/>).</summary>
public sealed class MapUpdateDiagnosticsTests
{
    [Fact]
    public void PlayerHeartbeats_AreSummedInTicks_AndConvertedToMicrosecondsOnce()
    {
        // Each beat is 1.5 us: truncating every beat to whole microseconds would count 4 us for four beats, the sum is 6 us.
        long perBeat = Stopwatch.Frequency * 3 / 2_000_000;
        var diagnostics = new MapUpdateDiagnostics();

        for (int i = 0; i < 4; i++)
        {
            diagnostics.AddPlayerHeartbeat(perBeat);
        }

        Assert.Equal(perBeat * 4 * 1_000_000 / Stopwatch.Frequency, diagnostics.HeartbeatPlayersMicros);
        Assert.Equal(6, diagnostics.HeartbeatPlayersMicros);

        diagnostics.Reset();
        Assert.Equal(0, diagnostics.HeartbeatPlayersMicros);
    }

    [Fact]
    public void TopContributors_NamesActiveCells_WhenItIsTheLargestPhase()
    {
        var diagnostics = new MapUpdateDiagnostics();
        diagnostics.Begin();
        Thread.Sleep(30);
        diagnostics.EndActiveCells();

        Assert.True(diagnostics.ActiveCellsMicros >= 20_000);
        Assert.Equal("active cells", diagnostics.TopContributors(1)[0].Name);
    }
}

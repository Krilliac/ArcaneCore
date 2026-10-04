using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Events;
using Xunit;

namespace ArcaneCore.Game.Tests.WorldState;

/// <summary>The <c>World:GameEvents</c> defaults (every non-retail choice is off) and the world-level tick seam.</summary>
public sealed class GameEventOptionsAndTickTests
{
    [Fact]
    public void Defaults_AreRetail()
    {
        var options = new GameEventOptions();

        Assert.True(options.Enabled);
        Assert.Equal(GameEventDialect.Auto, options.Dialect);
        Assert.Equal(GameEventStartBoundary.Auto, options.StartBoundary);
        Assert.Equal(GameEventDateTimeInterpretation.Wall, options.DateTimeInterpretation); // vmangos UNIX_TIMESTAMP
        Assert.Equal(LeapDayMode.DateStable, options.LeapDayMode);
        Assert.Equal(YearlyRebaseMode.SpanNewYear, options.YearlyRebase);
        Assert.False(options.RestoreServersideEvents); // retail restores nothing
        Assert.False(options.Announce);                // vmangos Event.Announce defaults to 0
        Assert.False(options.AllowReload);             // retail has no .reload game_event
    }

    [Fact]
    public void WorldTick_IsRaisedOncePerTick_WithTheElapsedTime_BeforeTheMapsUpdate()
    {
        using var world = TestWorld.CreateRuntime();
        var seen = new List<uint>();
        world.WorldTick += seen.Add;

        world.RunTick(50);
        world.RunTick(125);

        Assert.Equal([50u, 125u], seen);
    }

    [Fact]
    public void WorldTick_ALoggingFailureInOneHandler_DoesNotStopTheOthersOrTheTick()
    {
        using var world = TestWorld.CreateRuntime();
        var seen = new List<uint>();
        world.WorldTick += _ => throw new InvalidOperationException("boom");
        world.WorldTick += seen.Add;

        world.RunTick(10);

        Assert.Equal([10u], seen);
    }
}

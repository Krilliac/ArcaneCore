using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Kernel.WorldData.WorldState;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Game.Tests.WorldState;

/// <summary><see cref="GameEventService.RemoveListener"/>: a listener that was removed hears nothing more, the others keep hearing.</summary>
public sealed class GameEventListenerRemovalTests
{
    private sealed class Listener : IGameEventListener
    {
        public List<string> Calls { get; } = [];

        public void OnEventChanged(ushort eventId, bool active, bool resume) => Calls.Add($"{eventId} {active} {resume}");
    }

    private static GameEventService Make(DateTimeOffset now)
    {
        var options = new GameEventOptions();
        var content = new GameEventContent(
            [new GameEventRecord(1, 1, 1440, 120, 0, 0, "one")],
            [new GameEventTimeRecord(1, now.AddHours(-1).ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), "2030-12-31 22:59:59")],
            [], [], [], [], []);
        GameEventLoadResult load = GameEventLoader.Load(content, options, now, TimeZoneInfo.Utc);
        return new GameEventService(load, options, () => now, TimeZoneInfo.Utc, NullLogger.Instance, null, null, null);
    }

    [Fact]
    public void ARemovedListener_HearsNothingMore_AndTheOtherStillDoes()
    {
        GameEventService service = Make(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
        var removed = new Listener();
        var kept = new Listener();
        service.AddListener(removed);
        service.AddListener(kept);

        Assert.True(service.RemoveListener(removed));
        service.Initialize(new HashSet<ushort>());

        Assert.Empty(removed.Calls);
        Assert.Equal(["1 True False"], kept.Calls);
    }

    [Fact]
    public void RemovingAListenerThatWasNeverAdded_ReportsFalse_AndRemovesOnlyOneRegistrationPerCall()
    {
        GameEventService service = Make(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
        var listener = new Listener();
        Assert.False(service.RemoveListener(listener));
        service.AddListener(listener);
        service.AddListener(listener);

        Assert.True(service.RemoveListener(listener));
        service.Initialize(new HashSet<ushort>());

        Assert.Equal(["1 True False"], listener.Calls);
        Assert.True(service.RemoveListener(listener));
        Assert.False(service.RemoveListener(listener));
    }
}

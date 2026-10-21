using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.WorldState;

/// <summary>
/// <see cref="GameEventFeature.AddListener"/> returns the registration: disposing it removes the listener from the feature (a later service
/// built by a reload never gets it) and from the running service, and disposing twice is harmless.
/// </summary>
public sealed class GameEventFeatureListenerTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 17, 12, 0, 0, TimeSpan.Zero);

    private sealed class Listener : IGameEventListener
    {
        public List<string> Calls { get; } = [];

        public void OnEventChanged(ushort eventId, bool active, bool resume) => Calls.Add($"{eventId} {active} {resume}");
    }

    private static WorldTestHost Start()
    {
        WorldTestHost.GameTime.Value = new ArcaneCore.Game.WorldState.Time.FixedGameTime(Now, TimeZoneInfo.Local);
        string start = TimeZoneInfo.ConvertTime(Now.AddHours(5), TimeZoneInfo.Local).DateTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        GameEventTestStore.Current.Value = new GameEventContent(
            [new GameEventRecord(2, 1, 1440, 60, 0, 0, "Night Market")],
            [new GameEventTimeRecord(2, start, "2090-12-31 22:59:59")],
            [], [], [], [], []);
        try
        {
            return WorldTestHost.Start();
        }
        finally
        {
            GameEventTestStore.Current.Value = null;
            WorldTestHost.GameTime.Value = null;
        }
    }

    [Fact]
    public async Task ADisposedRegistration_HearsNothingMore_ByTheRunningServiceOrAReloadedOne()
    {
        await using WorldTestHost host = Start();
        GameEventFeature events = host.WorldServices.GetRequiredService<GameEventFeature>();
        await host.WaitForWorldAsync(() => events.Service is { IsInitialised: true }, "the event service is initialised");
        var removed = new Listener();
        var kept = new Listener();

        await host.OnWorldAsync(() =>
        {
            IDisposable registration = events.AddListener(removed);
            events.AddListener(kept);
            registration.Dispose();
            registration.Dispose();
            Assert.True(events.Service!.StartEvent(2));
            Assert.True(events.Service.StopEvent(2));
            // A reload builds a new service from the feature's own listener list: the removed one must not come back.
            events.UseContent(events.Content, new HashSet<ushort>(), continueRunning: false);
            return true;
        });
        await host.WaitForWorldAsync(() => events.Service is { IsInitialised: true }, "the replacement service is initialised");
        await host.OnWorldAsync(() => events.Service!.StartEvent(2));

        Assert.Empty(removed.Calls);
        Assert.Equal(["2 True False", "2 False False", "2 True False"], kept.Calls);
    }
}

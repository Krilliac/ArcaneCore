using System.Runtime.CompilerServices;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Tests.Playerbots.Party;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// The bot social memory's PvP hand-off must not root a world. It used to be a process-wide static event the feature subscribed to at attach
/// and that no world disposal ever undid: every playerbot test world stayed reachable through it, the test process grew to gigabytes of gen-2
/// heap and spent most of its time in GC (World suite 9 min to 30 min locally, and the timing tests of the social and population passes timed
/// out on CI). Now the feature hears its own world's maps (PlayerbotPvpKillFeed), which live and die with that world.
/// </summary>
public sealed class PlayerbotKillObserverLifetimeTests
{
    [Fact]
    public async Task ADisposedWorldWithARunningBot_IsCollectable() => await AssertCollectedAsync(shutDownFirst: true);

    [Fact]
    public async Task AWorldDisposedWithoutTheFeaturesShutdown_WhileABotIsStillWatched_IsCollectable() => await AssertCollectedAsync(shutDownFirst: false);

    private static async Task AssertCollectedAsync(bool shutDownFirst)
    {
        var worlds = new List<WeakReference>();
        for (int i = 0; i < 3; i++) worlds.Add(await RunAndDisposeAsync(shutDownFirst));

        // Up to about 10 s: a world's last timers and posted work let go of it shortly after the dispose.
        for (int i = 0; i < 100 && worlds.Any(w => w.IsAlive); i++)
        {
            await Task.Delay(100);
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.Equal(0, worlds.Count(w => w.IsAlive));
    }

    // Its own method, so no local of the caller's state machine keeps the host or the world reachable.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> RunAndDisposeAsync(bool shutDownFirst)
    {
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners);
        var feature = PartyTestHost.Feature(host);
        await feature.StartupAsync(default);
        Guid id = Assert.IsType<Guid>((await feature.CreateAsync("Killwatch", 1, 1)).BotId);
        Assert.True((await feature.StartAsync(id.ToString())).Success);
        // The brain's update puts the bot on its map's kill observer (PlayerbotBrain.WatchKills); the bot is still on it at the dispose below
        // when the feature's own shutdown is skipped.
        await WorldTestHost.WaitForAsync(() => feature.FindBrain(id) is not null && feature.FindSession(id)?.Player is { IsInWorld: true }, "the bot in the world");
        await host.World.InvokeAsync(() => true);
        await host.World.InvokeAsync(() => true);
        if (shutDownFirst) await feature.ShutdownBeforeWorldStopAsync();
        return new WeakReference(host.World);
    }
}

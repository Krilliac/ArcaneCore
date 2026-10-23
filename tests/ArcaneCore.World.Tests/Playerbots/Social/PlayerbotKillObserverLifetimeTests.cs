using System.Runtime.CompilerServices;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Tests.Playerbots.Party;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// The kill observer's PvP hand-off (bot social memory) must not root a world. It used to be a process-wide static event the feature subscribed
/// to at attach and that no world disposal ever undid: every playerbot test world stayed reachable through it, the test process grew to gigabytes
/// of gen-2 heap and spent most of its time in GC (World suite 9 min to 30 min locally, and the timing tests of the social and population passes
/// timed out on CI). Now each brain hands its PvP kills over through the observer of its own map, which lives and dies with that map.
/// </summary>
public sealed class PlayerbotKillObserverLifetimeTests
{
    [Fact]
    public async Task ADisposedWorldWithARunningBot_IsCollectable()
    {
        var worlds = new List<WeakReference>();
        for (int i = 0; i < 3; i++) worlds.Add(await RunAndDisposeAsync());

        for (int i = 0; i < 20 && worlds.Any(w => w.IsAlive); i++)
        {
            await Task.Delay(100);
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.Equal(0, worlds.Count(w => w.IsAlive));
    }

    // Its own method, so no local of the caller's state machine keeps the host or the world reachable.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> RunAndDisposeAsync()
    {
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners);
        var feature = PartyTestHost.Feature(host);
        await feature.StartupAsync(default);
        Guid id = Assert.IsType<Guid>((await feature.CreateAsync("Killwatch", 1, 1)).BotId);
        Assert.True((await feature.StartAsync(id.ToString())).Success);
        // The brain's update puts the bot on its map's kill observer (PlayerbotBrain.WatchKills).
        await WorldTestHost.WaitForAsync(() => feature.FindBrain(id) is not null && feature.FindSession(id)?.Player is { IsInWorld: true }, "the bot in the world");
        await host.World.InvokeAsync(() => true);
        await host.World.InvokeAsync(() => true);
        await feature.ShutdownBeforeWorldStopAsync();
        return new WeakReference(host.World);
    }
}

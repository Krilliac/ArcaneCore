using System.Diagnostics;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The feature's wiring of the think attribution: a slow think of a running bot is logged with its driver (here a scripted
/// controller, whose think is about no goal of the brain's), each think's probe carries <see cref="PlayerbotOptions.MaxThinkSearchNodes"/>,
/// and both options are read from the shared options at each think, so a change there (what <c>.reload config</c> does) applies at once.
/// </summary>
public sealed class ManagedPlayerbotSlowThinkTests
{
    [Fact]
    public async Task ASlowControllerThink_IsLoggedWithItsDriver_AndTheOptionsApplyAtOnce()
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        var log = new ManagedPlayerbotLifecycleTests.CapturingLogger();
        PlayerbotOptions? shared = null;
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(accounts, characters, owners, logger: log,
            configure: o => { o.SlowThinkLogMs = 30; o.MaxThinkSearchNodes = 50_000; shared = o; });
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await feature.StartupAsync(default);
        Guid id = Assert.IsType<Guid>((await feature.CreateAsync("Slowthink", 1, 1)).BotId);
        var budgets = new List<long>();
        await host.OnWorldAsync(() =>
        {
            feature.ThinkObserver = (bot, probe) => { if (bot == id) budgets.Add(probe.NodeBudget); };
            return true;
        });
        var controller = new SpinningController(40);
        Assert.True((await feature.StartScriptedAsync(id.ToString(), controller)).Success);

        await host.WaitForWorldAsync(() => log.Entries.Any(e => e.Message.Contains("(Slowthink) slow think")), "the slow-think line");
        string line = log.Entries.First(e => e.Message.Contains("(Slowthink) slow think")).Message;
        Assert.Contains("(over 30 ms) by its controller: goal n/a; slowest phase controller", line);
        Assert.Contains($"Playerbot {id} ", line);
        Assert.All(await host.OnWorldAsync(() => budgets.ToArray()), budget => Assert.Equal(50_000, budget));

        // What .reload config does: the shared options change; the next thinks use them.
        await host.OnWorldAsync(() =>
        {
            shared!.SlowThinkLogMs = 0;
            shared.MaxThinkSearchNodes = 0;
            budgets.Clear();
            return true;
        });
        int lines = log.Entries.Count(e => e.Message.Contains("slow think"));
        int ticks = controller.Ticks;
        await host.WaitForWorldAsync(() => controller.Ticks >= ticks + 3, "three more slow thinks");
        Assert.Equal(lines, log.Entries.Count(e => e.Message.Contains("slow think")));
        long[] after = await host.OnWorldAsync(() => budgets.ToArray());
        Assert.NotEmpty(after);
        Assert.All(after, budget => Assert.Equal(0, budget));

        await host.OnWorldAsync(() =>
        {
            feature.ThinkObserver = null;
            return true;
        });
        await feature.ShutdownBeforeWorldStopAsync();
    }

    /// <summary>A scripted controller whose every tick burns <paramref name="ms"/> milliseconds of CPU.</summary>
    private sealed class SpinningController(double ms) : IPlayerbotController
    {
        private int _ticks;

        public int Ticks => Volatile.Read(ref _ticks);

        public void Tick(PlayerbotControllerContext context, uint elapsedMs)
        {
            long until = Stopwatch.GetTimestamp() + (long)(ms * Stopwatch.Frequency / 1000);
            while (Stopwatch.GetTimestamp() < until) Thread.SpinWait(50);
            Interlocked.Increment(ref _ticks);
        }

        public void Detached(Guid botId) { }
    }
}

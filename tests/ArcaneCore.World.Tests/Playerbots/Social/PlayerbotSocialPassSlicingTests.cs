using ArcaneCore.Game.Entities;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// The social pass is incremental: it is spread over ticks under <c>World:Playerbots:Social:PassBudgetMs</c> instead of running whole in
/// one tick. The tests drive <c>PlayerbotSocialFeature.Update</c> by hand on the world thread, inside ONE world-thread action (so the
/// host's own ticks cannot interleave), with an injected clock (<c>PassClockMs</c>) that only advances when a probe lane spends its
/// fake cost: no wall-clock assertion anywhere. Two probe lanes (<c>InjectedLanes</c>, which run per bot slice like the built-in
/// per-bot lanes) record which bots each lane saw and when.
/// </summary>
public sealed class PlayerbotSocialPassSlicingTests
{
    private const int BotCount = 5;
    private const double CostPerBotMs = 1.0;

    private static readonly string[] Names = ["Slicera", "Slicerb", "Slicerc", "Slicerd", "Slicere"];

    private sealed class Rig
    {
        public double Clock;
        public readonly List<(string Lane, Guid Bot)> Events = [];

        public int Visits(string lane, Guid bot) => Events.Count(e => e.Lane == lane && e.Bot == bot);

        public void Install(PlayerbotSocialFeature feature)
        {
            feature.PassClockMs = () => Clock;
            foreach (string lane in new[] { "probe-a", "probe-b" })
            {
                string name = lane;
                feature.InjectedLanes.Add((name, pass =>
                {
                    foreach (PlayerbotSocialBot bot in pass.Bots) Events.Add((name, bot.Id));
                    Clock += pass.Bots.Count * CostPerBotMs;
                }));
            }
        }
    }

    private static async Task<SocialWorld> StartWorldAsync()
    {
        SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        try
        {
            foreach (string name in Names) await world.AddBotAsync(name);
            return world;
        }
        catch
        {
            await world.DisposeAsync();
            throw;
        }
    }

    /// <summary>Run <paramref name="body"/> on the world thread with the pass interval at 1000 ms and the probe lanes installed; always put both back.</summary>
    private static Task RunAsync(SocialWorld world, double budgetMs, Rig rig, Action<PlayerbotSocialFeature> body)
        => world.Host.OnWorldAsync(() =>
        {
            PlayerbotSocialFeature feature = SocialTestKit.Feature(world.Host);
            world.Options.Social.PassBudgetMs = budgetMs;
            feature.Timings.PassIntervalMs = 1000;
            rig.Install(feature);
            try
            {
                body(feature);
            }
            finally
            {
                feature.Timings.PassIntervalMs = int.MaxValue;
                feature.InjectedLanes.Clear();
            }
        });

    /// <summary>One tick of 1000 ms world time; returns the fake time the tick spent.</summary>
    private static double Tick(PlayerbotSocialFeature feature, Rig rig, uint elapsedMs = 1000)
    {
        double before = rig.Clock;
        feature.Update(elapsedMs);
        return rig.Clock - before;
    }

    [Fact]
    public void TheDefaultBudget_IsSmall()
    {
        Assert.Equal(4.0, new PlayerbotSocialOptions().PassBudgetMs);
    }

    [Fact]
    public async Task APassUnderATinyBudget_CompletesOverSeveralTicks_AndEveryLaneVisitsEveryBotOnce()
    {
        await using SocialWorld world = await StartWorldAsync();
        var rig = new Rig();
        var spent = new List<double>();
        int passesBefore = 0;
        bool inProgressBetweenTicks = false;
        await RunAsync(world, budgetMs: 2.0, rig, feature =>
        {
            passesBefore = feature.Passes;
            for (int i = 0; i < 20 && feature.Passes == passesBefore; i++)
            {
                spent.Add(Tick(feature, rig));
                if (feature.Passes == passesBefore) inProgressBetweenTicks |= feature.PassInProgress;
            }
        });

        Assert.True(spent.Count > 1, $"the pass must take several ticks, took {spent.Count}");
        Assert.True(spent.Count <= BotCount, $"every tick must make progress; {spent.Count} ticks for {BotCount} bots");
        Assert.True(inProgressBetweenTicks, "between its ticks the unfinished pass is reported as in progress");
        foreach (Guid bot in world.Bots.Values)
        {
            Assert.Equal(1, rig.Visits("probe-a", bot));
            Assert.Equal(1, rig.Visits("probe-b", bot));
            // Same lane order for one bot: a before b.
            Assert.True(rig.Events.IndexOf(("probe-a", bot)) < rig.Events.IndexOf(("probe-b", bot)));
        }

        Assert.Equal(BotCount * 2, rig.Events.Count);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(3.5)]
    public async Task EveryTick_SpendsNoMoreThanTheBudgetPlusOneBot(double budgetMs)
    {
        await using SocialWorld world = await StartWorldAsync();
        var rig = new Rig();
        var spent = new List<double>();
        await RunAsync(world, budgetMs, rig, feature =>
        {
            int before = feature.Passes;
            for (int i = 0; i < 40 && feature.Passes == before; i++) spent.Add(Tick(feature, rig));
        });

        // Two probe lanes cost 2 * CostPerBotMs per bot, so a tick may overshoot by at most one bot's worth of both lanes.
        double ceiling = budgetMs + 2 * CostPerBotMs;
        Assert.All(spent, s => Assert.True(s <= ceiling, $"a tick spent {s} ms of fake time against a budget of {budgetMs} (ceiling {ceiling})"));
        // The total work is fixed, so a smaller budget needs at least as many ticks as the budget allows for.
        double total = BotCount * 2 * CostPerBotMs;
        Assert.True(spent.Count >= Math.Ceiling(total / ceiling), $"{spent.Count} ticks cannot carry {total} ms under a {budgetMs} ms budget");
        Assert.Equal(total, spent.Sum());
    }

    [Fact]
    public async Task ABudgetAboveTheWholeCost_FinishesThePassInOneTick()
    {
        await using SocialWorld world = await StartWorldAsync();
        var rig = new Rig();
        int ticks = 0, passesBefore = 0, passesAfter = 0;
        await RunAsync(world, budgetMs: 10_000, rig, feature =>
        {
            passesBefore = feature.Passes;
            while (feature.Passes == passesBefore && ticks < 5)
            {
                Tick(feature, rig);
                ticks++;
            }

            passesAfter = feature.Passes;
        });

        Assert.Equal(1, ticks);
        Assert.Equal(passesBefore + 1, passesAfter);
        foreach (Guid bot in world.Bots.Values) Assert.Equal(1, rig.Visits("probe-a", bot));
    }

    [Fact]
    public async Task ABotLeavingTheWorldMidPass_IsNeverVisitedAfterItLeft_AndTheRestAreVisitedOnce()
    {
        await using SocialWorld world = await StartWorldAsync();
        var rig = new Rig();
        string? leaver = null;
        int eventsAtRemoval = -1;
        long faultsBefore = 0, faultsAfter = 0;
        await RunAsync(world, budgetMs: 2.0, rig, feature =>
        {
            faultsBefore = feature.LaneFaultCount;
            int passesBefore = feature.Passes;
            Tick(feature, rig);
            Assert.Equal(passesBefore, feature.Passes); // the pass is unfinished: bots remain
            // Take out a bot the first tick has not reached yet.
            Guid unvisited = world.Bots.Values.First(id => rig.Visits("probe-a", id) == 0);
            leaver = world.Bots.First(p => p.Value == unvisited).Key;
            Player player = world.Host.World.FindOnlinePlayer(leaver)!;
            world.Host.World.RemovePlayer(player);
            Assert.False(player.IsInWorld);
            eventsAtRemoval = rig.Events.Count;
            for (int i = 0; i < 20 && feature.Passes == passesBefore; i++) Tick(feature, rig);
            Assert.True(feature.Passes > passesBefore, "the pass still finishes");
            faultsAfter = feature.LaneFaultCount;
        });

        Guid left = world.Bots[leaver!];
        Assert.DoesNotContain(rig.Events.Skip(eventsAtRemoval), e => e.Bot == left);
        Assert.Equal(faultsBefore, faultsAfter);
        foreach (Guid bot in world.Bots.Values.Where(id => id != left))
        {
            Assert.Equal(1, rig.Visits("probe-a", bot));
            Assert.Equal(1, rig.Visits("probe-b", bot));
        }
    }

    [Fact]
    public async Task ANewPass_DoesNotStartBeforeThePreviousOneFinishes()
    {
        await using SocialWorld world = await StartWorldAsync();
        var rig = new Rig();
        var visitsWhenFirstPassEnded = new Dictionary<Guid, int>();
        int passesBefore = 0, firstPassTicks = 0;
        await RunAsync(world, budgetMs: 1.0, rig, feature =>
        {
            passesBefore = feature.Passes;
            // Every tick brings a full interval of world time, yet the pass in flight is finished first.
            for (; firstPassTicks < 60 && feature.Passes == passesBefore; firstPassTicks++) Tick(feature, rig);
            foreach (Guid bot in world.Bots.Values) visitsWhenFirstPassEnded[bot] = rig.Visits("probe-a", bot);
            // And once it is over, the next one does start.
            for (int i = 0; i < 60 && feature.Passes < passesBefore + 2; i++) Tick(feature, rig);
        });

        Assert.True(firstPassTicks > 1, $"the first pass must span several ticks to make the overlap possible, took {firstPassTicks}");
        Assert.All(visitsWhenFirstPassEnded, pair => Assert.Equal(1, pair.Value));
        foreach (Guid bot in world.Bots.Values) Assert.Equal(2, rig.Visits("probe-a", bot));
    }

    [Fact]
    public async Task APassLongerThanItsInterval_ContinuesWithoutBacklog_AndSkipsNoBot()
    {
        await using SocialWorld world = await StartWorldAsync();
        var rig = new Rig();
        int passesBefore = 0, passesAfter = 0, tickCount = 0;
        await RunAsync(world, budgetMs: 1.0, rig, feature =>
        {
            passesBefore = feature.Passes;
            // Ten intervals of world time arrive with every tick: the late pass is not run again for each missed interval.
            for (; tickCount < 30; tickCount++)
            {
                int before = feature.Passes;
                Tick(feature, rig, elapsedMs: 10_000);
                Assert.True(feature.Passes - before <= 1, "at most one pass can finish in a tick");
            }

            passesAfter = feature.Passes;
        });

        int completed = passesAfter - passesBefore;
        Assert.True(completed >= 1);
        // One pass needs several ticks under this budget, so 30 ticks can never carry 30 passes (no backlog catching up).
        Assert.True(completed < tickCount / 2, $"{completed} passes in {tickCount} ticks");
        foreach (Guid bot in world.Bots.Values)
        {
            int visits = rig.Visits("probe-a", bot);
            Assert.True(visits == completed || visits == completed + 1, $"{visits} visits after {completed} finished passes");
            int second = rig.Visits("probe-b", bot);
            Assert.True(second == completed || second == completed + 1, $"{second} visits of the second lane after {completed} finished passes");
        }
    }
}

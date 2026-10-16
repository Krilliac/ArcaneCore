using ArcaneCore.Game.Creatures;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Policy;
using ArcaneCore.World.Tests.Playerbots.Risk;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Policy;

/// <summary>The policy hook in a real brain: the rule ladder policy changes nothing, a model's different pick is tried first.</summary>
public sealed class PlayerbotPolicyBrainTests
{
    private const int Thinks = 12;

    [Fact]
    public async Task WithTheRuleLadderPolicy_TheBrainDoesExactlyWhatTheLadderDoes()
    {
        IReadOnlyList<string> ladderOnly = await RunAsync(engine: null);
        var engine = new PlayerbotPolicyEngine(new PlayerbotPolicyOptions { Enabled = true });
        IReadOnlyList<string> withPolicy = await RunAsync(engine);

        Assert.Equal(ladderOnly, withPolicy);
        // Not vacuous: the policy really decided on those thinks (and agreed every time) rather than being skipped.
        PlayerbotPolicyStats stats = engine.Stats;
        Assert.True(stats.Agreed > 0, $"agreed {stats.Agreed}, missing {stats.Missing}");
        Assert.Equal(0, stats.Applied);
        Assert.Equal(0, stats.Refused);
        Assert.True(stats.Scored > 0, $"scored {stats.Scored}");
        // And the ladder did choose the grind target on its own: what the next test's model overrides.
        Assert.Contains(ladderOnly, line => line.Contains(":Grind/", StringComparison.Ordinal));
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task AModelThatPicksAnotherOpenGoal_IsHonoured()
    {
        var engine = new PlayerbotPolicyEngine(new PlayerbotPolicyOptions { Enabled = true },
            policy: new PlayerbotPolicyEngineTests.StubPolicy(PlayerbotPolicyGoal.Explore));
        await using RiskTestWorld world = await StartAsync();
        world.Brain.PolicyEngine = engine;
        Creature mob = await world.OnWorldAsync(() => world.Spawn(Mob(), 50f, 0f));
        await world.SeeAsync(mob);

        PlayerbotPolicyVerdict? applied = null;
        for (int i = 0; i < Thinks && applied is null; i++)
        {
            await world.ThinkAsync();
            await world.OnWorldAsync(() => { engine.Dispatch(); return true; });
            await engine.WhenIdleAsync();
            applied = await world.OnWorldAsync(() =>
                world.Brain.PolicyAgent?.LastVerdict is { Source: PlayerbotPolicySource.Applied } verdict ? verdict : (PlayerbotPolicyVerdict?)null);
        }

        Assert.NotNull(applied);
        Assert.Equal(PlayerbotPolicyGoal.Explore, applied.Value.Promote);
        Assert.True(applied.Value.Window!.Open.Contains(PlayerbotPolicyGoal.Grind), "the ladder had a grind target to choose");
        Assert.Equal(PlayerbotPolicyGoal.Grind, applied.Value.Window.LadderPick);
        await world.OnWorldAsync(() =>
        {
            Assert.Equal(PlayerbotGoalKind.Explore, world.Brain.Goal);
            Assert.Null(world.Brain.InspectionTarget);
            return true;
        });
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task UnderResurrectionSickness_APromotionIsRefused_AndTheBotStandsDown()
    {
        // Explore is always open, so this model would walk a sick bot off; the ladder stands every rung below town down instead.
        var engine = new PlayerbotPolicyEngine(new PlayerbotPolicyOptions { Enabled = true },
            policy: new PlayerbotPolicyEngineTests.StubPolicy(PlayerbotPolicyGoal.Explore));
        await using RiskTestWorld world = await StartAsync();
        world.Brain.PolicyEngine = engine;
        Creature mob = await world.OnWorldAsync(() =>
        {
            PlayerbotSicknessTestKit.Apply(world.Host.WorldServices, world.Player);
            return world.Spawn(Mob(), 50f, 0f);
        });
        await world.SeeAsync(mob);

        for (int i = 0; i < Thinks; i++)
        {
            await world.ThinkAsync();
            await world.OnWorldAsync(() => { engine.Dispatch(); return true; });
            await engine.WhenIdleAsync();
            await world.OnWorldAsync(() =>
            {
                Assert.NotEqual(PlayerbotGoalKind.Explore, world.Brain.Goal);
                Assert.NotEqual(PlayerbotPolicySource.Applied, world.Brain.PolicyAgent?.LastVerdict?.Source);
                return true;
            });
        }

        PlayerbotPolicyStats stats = engine.Stats;
        // Not vacuous: the model did pick Explore over the ladder, and the refusal is what kept the bot resting.
        Assert.True(stats.Refused > 0, $"refused {stats.Refused}, applied {stats.Applied}, missing {stats.Missing}");
        Assert.Equal(PlayerbotGoalKind.Rest, await world.OnWorldAsync(() => world.Brain.Goal));
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task WhenTheBotsThinksAreFarApart_ThePolicyStillDecides()
    {
        // Live 2026-10-16: a bot's out-of-combat thinks 8-17 s apart (p95 with ~1000 bots) left every verdict stale (StaleAfterMs 4.5 s),
        // because a snapshot is taken after a think decides and so is first used by the next think.
        var engine = new PlayerbotPolicyEngine(new PlayerbotPolicyOptions { Enabled = true },
            policy: new PlayerbotPolicyEngineTests.StubPolicy(PlayerbotPolicyGoal.Explore));
        await using RiskTestWorld world = await StartAsync();
        world.Brain.PolicyEngine = engine;
        Creature mob = await world.OnWorldAsync(() => world.Spawn(Mob(), 50f, 0f));
        await world.SeeAsync(mob);

        var sources = new List<PlayerbotPolicySource>();
        for (int i = 0; i < 4; i++)
        {
            await world.ThinkAsync();
            await world.OnWorldAsync(() => { engine.Dispatch(); return true; });
            await engine.WhenIdleAsync();
            sources.Add(await world.OnWorldAsync(() => world.Brain.PolicyAgent!.LastVerdict!.Value.Source));
            await world.Host.World.AdvanceClockAsync(13_000 - RiskTestWorld.ThinkMs); // the next think, 13 s of world time later
        }

        Assert.Contains(PlayerbotPolicySource.Applied, sources);
        Assert.True(engine.Stats.Stale == 0, $"stale {engine.Stats.Stale}: {string.Join(", ", sources)}");
        await engine.DisposeAsync();
    }

    /// <summary>The bot's goal and target after each of <see cref="Thinks"/> thinks next to one grindable mob.</summary>
    private static async Task<IReadOnlyList<string>> RunAsync(PlayerbotPolicyEngine? engine)
    {
        await using RiskTestWorld world = await StartAsync();
        world.Brain.PolicyEngine = engine;
        Creature mob = await world.OnWorldAsync(() => world.Spawn(Mob(), 50f, 0f));
        await world.SeeAsync(mob);
        var trace = new List<string>();
        for (int i = 0; i < Thinks; i++)
        {
            await world.ThinkAsync();
            if (engine is not null)
            {
                await world.OnWorldAsync(() => { engine.Dispatch(); return true; });
                await engine.WhenIdleAsync();
            }
            trace.Add(await world.OnWorldAsync(() =>
                $"{world.Brain.LastThinkPhase}:{world.Brain.Goal}/{world.Brain.TargetEntry}/{world.Brain.InspectionTarget?.Entry ?? 0}/{world.Player.X:F1},{world.Player.Y:F1}"));
        }
        return trace;
    }

    private static Task<RiskTestWorld> StartAsync() => RiskTestWorld.StartAsync(options => options.Risk.Enabled = false);

    private static CreatureTemplate Mob() => RiskTestWorld.Template(993_901, level: 1, health: 400);
}

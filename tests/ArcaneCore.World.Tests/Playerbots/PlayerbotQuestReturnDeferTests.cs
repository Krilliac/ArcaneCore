using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Creatures;
using Microsoft.Extensions.DependencyInjection;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Policy;
using ArcaneCore.World.Tests.Npc;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The defer ledger through the brain (<see cref="PlayerbotDeferLedger"/>): what <c>PlayerbotBrain.ObserveDeferWakes</c> wakes
/// at a think, and the bound on a quest the bot keeps abandoning for an unreachable ender.
/// </summary>
public sealed partial class PlayerbotQuestReturnTests
{
    private static DeferKey UnreachableKey(uint quest) => new(DeferKind.UnreachableQuest, quest);

    /// <summary>A quest marked unreachable for an hour (so a wake, not the deadline, is what ends it), then one baseline think.</summary>
    private static async Task<PlayerbotBrain> MarkedBrainAsync(ReturnFixture fixture)
    {
        PlayerbotBrain brain = await StartAsync(fixture, new ScriptedRoute(climb: 23.5f));
        await fixture.Host.World.InvokeAsync(() =>
        {
            brain.Suspensions.Ledger.Defer(UnreachableKey(777), fixture.Host.World.NowMs, untilMs: fixture.Host.World.NowMs + 3_600_000);
            return true;
        });
        await ThinkAsync(fixture, brain, 1); // the first probe only records what the bot is
        return brain;
    }

    private static Task<bool> UnreachableAsync(ReturnFixture fixture, PlayerbotBrain brain, uint after = 0)
        => fixture.Host.World.InvokeAsync(() => brain.Suspensions.IsQuestUnreachable(777, fixture.Host.World.NowMs + after));

    [Fact]
    public async Task ALevelGainedBetweenTwoThinks_ReleasesTheUnreachableQuest()
    {
        await using var fixture = await ReturnFixture.CreateAsync(farGiver: true, manualClock: true);
        PlayerbotBrain brain = await MarkedBrainAsync(fixture);
        try
        {
            Assert.True(await UnreachableAsync(fixture, brain));
            await fixture.Host.World.InvokeAsync(() => { fixture.Session.Player!.Level = (byte)(fixture.Session.Player.Level + 1); return true; });
            await ThinkAsync(fixture, brain, 1);
            Assert.False(await UnreachableAsync(fixture, brain));
            Assert.Equal(1, await fixture.Host.World.InvokeAsync(() => brain.Suspensions.Ledger.Strikes(UnreachableKey(777)))); // a wake keeps the strikes
        }
        finally { await StopAsync(fixture, brain); }
    }

    [Fact]
    public async Task NothingChangedBetweenThinks_ReleasesNothing()
    {
        await using var fixture = await ReturnFixture.CreateAsync(farGiver: true, manualClock: true);
        PlayerbotBrain brain = await MarkedBrainAsync(fixture);
        try
        {
            await ThinkAsync(fixture, brain, 6);
            Assert.True(await UnreachableAsync(fixture, brain));
            Assert.True(await UnreachableAsync(fixture, brain, 3_000_000));
        }
        finally { await StopAsync(fixture, brain); }
    }

    [Fact]
    public async Task ASwappedNavigationDataset_ReleasesTheUnreachableQuestAfterTheBotsStagger_NotAtOnce()
    {
        await using var fixture = await ReturnFixture.CreateAsync(farGiver: true, manualClock: true);
        PlayerbotBrain brain = await MarkedBrainAsync(fixture);
        try
        {
            await fixture.Host.World.InvokeAsync(() =>
            {
                WorldCollision.Of(fixture.Host.World).Install(lineOfSight: new FlatFloor(fixture.Session.Player!.Z), pathfinder: new ScriptedRoute(climb: 1f));
                return true;
            });
            await AssertStaggeredAsync(fixture, brain);
        }
        finally { await StopAsync(fixture, brain); }
    }

    [Fact]
    public async Task ASwapBetweenTheDeferralAndTheNextThink_StillWakesIt()
    {
        await using var fixture = await ReturnFixture.CreateAsync(farGiver: true, manualClock: true);
        PlayerbotBrain brain = await StartAsync(fixture, new ScriptedRoute(climb: 23.5f));
        try
        {
            await ThinkAsync(fixture, brain, 1); // the brain has seen the world, and nothing is deferred yet
            await fixture.Host.World.InvokeAsync(() =>
            {
                brain.Suspensions.Ledger.Defer(UnreachableKey(777), fixture.Host.World.NowMs, untilMs: fixture.Host.World.NowMs + 3_600_000);
                WorldCollision.Of(fixture.Host.World).Install(lineOfSight: new FlatFloor(fixture.Session.Player!.Z), pathfinder: new ScriptedRoute(climb: 1f));
                return true;
            });
            await AssertStaggeredAsync(fixture, brain); // swapped after the deferral began, before the think that would have taken the baseline
        }
        finally { await StopAsync(fixture, brain); }
    }

    [Fact]
    public async Task TheSetAsideCountOfThePolicyInput_CountsOnlySetAsidesWithTimeLeft()
    {
        await using var fixture = await ReturnFixture.CreateAsync(farGiver: true, manualClock: true);
        PlayerbotBrain brain = await StartAsync(fixture, new ScriptedRoute(climb: 23.5f));
        try
        {
            await fixture.Host.World.InvokeAsync(() =>
            {
                float[] features = new float[PlayerbotPolicySchema.FeatureCount];
                uint now = fixture.Host.World.NowMs;
                brain.PolicySnapshot(fixture.Session.Player!, features);
                Assert.Equal(0f, features[5]);

                brain.Suspensions.SuspendEntry(4321, now);
                brain.Suspensions.MarkUnreachableQuest(777, now);
                brain.PolicySnapshot(fixture.Session.Player!, features);
                Assert.Equal(2 / 16f, features[5]);

                // The quest mark ends (its strikes stay, and are saved), the entry's set-aside still has time: one counts.
                brain.Suspensions.Ledger.Wake(UnreachableKey(777), PlayerbotWorldClock.UptimeMs(fixture.Host.World));
                Assert.Equal(1, brain.Suspensions.Ledger.Strikes(UnreachableKey(777)));
                Assert.Equal(2, brain.Suspensions.Export(Guid.Empty, 0, now, 1_000).Length);
                brain.PolicySnapshot(fixture.Session.Player!, features);
                Assert.Equal(1 / 16f, features[5]);
                return true;
            });
        }
        finally { await StopAsync(fixture, brain); }
    }

    [Fact]
    public async Task AnEntrySetAside_StaysEnded_AfterTheWorldRunsPastTwoToThe31AndTwoToThe32WithoutAnyLedgerCall()
    {
        await using var fixture = await ReturnFixture.CreateAsync(farGiver: true, manualClock: true);
        await fixture.Host.World.InvokeAsync(() =>
        {
            var world = fixture.Host.World;
            var suspensions = new PlayerbotSuspensions(world: world);
            suspensions.SuspendEntry(5, world.NowMs);
            suspensions.SuspendQuest(6, world.NowMs);
            Assert.True(suspensions.IsEntrySuspended(5, world.NowMs));
            const uint Hour = 3_600_000;
            for (int step = 0; step < 833; step++) world.RunTick(Hour); // 2.99e9 ms: past 2^31, no ledger call meanwhile
            Assert.True(world.Uptime.TotalMilliseconds > 2.9e9);
            Assert.False(suspensions.IsEntrySuspended(5, world.NowMs));
            Assert.False(suspensions.IsQuestSuspended(6, world.NowMs));
            for (int step = 0; step < 400; step++) world.RunTick(Hour); // past 2^32, where NowMs wrapped, again no call
            Assert.True(world.Uptime.TotalMilliseconds > 4.4e9);
            Assert.True(world.NowMs < 400_000_000u);
            Assert.False(suspensions.IsEntrySuspended(5, world.NowMs));
            Assert.False(suspensions.IsQuestSuspended(6, world.NowMs));
            Assert.Equal(1, suspensions.Ledger.Defer(new DeferKey(DeferKind.Entry, 5), PlayerbotWorldClock.UptimeMs(world)).Strikes); // the strike decayed meanwhile
            suspensions.SuspendEntry(5, world.NowMs); // set aside again, at the wrapped clock
            Assert.True(suspensions.IsEntrySuspended(5, world.NowMs + 599_999));
            Assert.False(suspensions.IsEntrySuspended(5, world.NowMs + 600_000));
            return true;
        });
    }

    [Fact]
    public async Task ASwappedCreatureDefinitionSet_ReleasesTheUnreachableQuestAfterTheBotsStagger_NotAtOnce()
    {
        await using var fixture = await ReturnFixture.CreateAsync(farGiver: true, manualClock: true);
        PlayerbotBrain brain = await MarkedBrainAsync(fixture);
        try
        {
            await fixture.Host.World.InvokeAsync(() =>
            {
                CreatureContent content = fixture.Host.WorldServices.GetRequiredService<CreatureWorldFeature>().Content;
                content.SwapDefinitions(new CreatureContent([], [], [], [], []));
                return true;
            });
            await AssertStaggeredAsync(fixture, brain);
        }
        finally { await StopAsync(fixture, brain); }
    }

    /// <summary>One think after a world change: the marked quest is still held, and ends within the bot's stagger.</summary>
    private static async Task AssertStaggeredAsync(ReturnFixture fixture, PlayerbotBrain brain)
    {
        uint thinkAt = 0;
        await fixture.Host.World.InvokeAsync(() =>
        {
            fixture.Session.ManagedBudget = new ManagedActionBudget(1);
            brain.Update(500);
            thinkAt = fixture.Host.World.NowMs;
            return true;
        });
        uint jitter = PlayerbotDeferLedger.Jitter(fixture.Session.Player!.Guid.Value);
        Assert.InRange(jitter, 1u, PlayerbotDeferLedger.JitterSpanMs - 1);
        await fixture.Host.World.InvokeAsync(() =>
        {
            PlayerbotSuspensions suspensions = brain.Suspensions;
            Assert.True(suspensions.IsQuestUnreachable(777, thinkAt)); // not immediate
            Assert.True(suspensions.IsQuestUnreachable(777, thinkAt + jitter - 1));
            Assert.False(suspensions.IsQuestUnreachable(777, thinkAt + jitter)); // but within the stagger
            return true;
        });
    }

    [Fact]
    public async Task ASwappedQuestStore_ReleasesTheUnreachableQuestAfterTheBotsStagger_NotAtOnce()
    {
        await using var fixture = await ReturnFixture.CreateAsync(farGiver: true, manualClock: true);
        PlayerbotBrain brain = await MarkedBrainAsync(fixture);
        try
        {
            await fixture.Host.World.InvokeAsync(() =>
            {
                fixture.Feature.Services.ReplaceQuests(new QuestStore(new QuestContent([], [], [])));
                return true;
            });
            uint thinkAt = 0;
            await fixture.Host.World.InvokeAsync(() =>
            {
                fixture.Session.ManagedBudget = new ManagedActionBudget(1);
                brain.Update(500);
                thinkAt = fixture.Host.World.NowMs;
                return true;
            });
            uint jitter = PlayerbotDeferLedger.Jitter(fixture.Session.Player!.Guid.Value);
            Assert.InRange(jitter, 1u, PlayerbotDeferLedger.JitterSpanMs - 1);
            await fixture.Host.World.InvokeAsync(() =>
            {
                PlayerbotSuspensions suspensions = brain.Suspensions;
                Assert.True(suspensions.IsQuestUnreachable(777, thinkAt)); // not immediate
                Assert.True(suspensions.IsQuestUnreachable(777, thinkAt + jitter - 1));
                Assert.False(suspensions.IsQuestUnreachable(777, thinkAt + jitter)); // but within the stagger
                return true;
            });
        }
        finally { await StopAsync(fixture, brain); }
    }

    /// <summary>Set the completed quest aside until the brain abandons it (the destinations' repeated set-asides).</summary>
    private static async Task DriveToAbandonAsync(ReturnFixture fixture, PlayerbotBrain brain)
    {
        for (int round = 1; round < PlayerbotWorldDestinations.AbandonAfterSetAsides; round++)
        {
            await ThinkAsync(fixture, brain, 10);
            Assert.True(await IsSetAsideAsync(fixture, brain), $"set-aside {round}");
            await fixture.Host.World.AdvanceClockAsync(PlayerbotSuspensions.SuspendMs + 100);
        }

        await ThinkAsync(fixture, brain, 10);
        Assert.NotEqual(QuestStatus.Complete, await StatusAsync(fixture));
    }

    private static async Task MoveGiverAsync(ReturnFixture fixture, float distance)
    {
        await fixture.Host.World.InvokeAsync(() =>
        {
            var player = fixture.Session.Player!;
            var giver = (Creature)player.Map!.FindObject(QuestInteractionFixture.Guid)!;
            giver.SetPosition(player.X + distance, player.Y, player.Z, 0);
            return true;
        });
        if (distance < 10)
            await fixture.Host.WaitForWorldAsync(() => fixture.Session.Player!.VisibleObjects.Contains(QuestInteractionFixture.Guid), "giver in sight");
        else
            await fixture.Host.WaitForWorldAsync(() => !fixture.Session.Player!.VisibleObjects.Contains(QuestInteractionFixture.Guid), "giver out of sight");
    }

    [Fact]
    public async Task AQuestAbandonedTwice_IsNotTakenAgainWithinTheTenMinutesOfTheSecondAbandon()
    {
        await using var fixture = await ReturnFixture.CreateAsync(farGiver: true, manualClock: true);
        PlayerbotBrain brain = await StartAsync(fixture, new ScriptedRoute(climb: 23.5f));
        try
        {
            await DriveToAbandonAsync(fixture, brain); // first abandon: the quest is marked for a minute
            Assert.Equal(QuestStatus.None, await StatusAsync(fixture));

            // The minute ends and the giver is in reach: the bot takes the quest again (the cycle the bound exists for).
            // (the whole cycle takes more than an hour of world time: the strikes must outlast it)
            await fixture.Host.World.AdvanceClockAsync(2 * 3_600_000u);
            await MoveGiverAsync(fixture, 1);
            await ThinkAsync(fixture, brain, 10);
            Assert.Equal(QuestStatus.Incomplete, await StatusAsync(fixture));
            await fixture.Host.World.InvokeAsync(() =>
            {
                fixture.Feature.Services.KilledMonsterCredit(fixture.Session.Player!, 90, ObjectGuid.WithEntry(HighGuid.Unit, 90, 42));
                return true;
            });
            await MoveGiverAsync(fixture, 150);

            await DriveToAbandonAsync(fixture, brain); // second abandon: the mark is ten minutes now
            Assert.Equal(QuestStatus.None, await StatusAsync(fixture));
            Assert.Equal(2, await fixture.Host.World.InvokeAsync(() => brain.Suspensions.Ledger.Strikes(UnreachableKey(QuestId))));
            uint secondAbandon = fixture.Host.World.NowMs;
            await MoveGiverAsync(fixture, 1);
            for (int block = 0; block < 5; block++)
            {
                await ThinkAsync(fixture, brain, 50); // about 105 s each
                Assert.Equal(QuestStatus.None, await StatusAsync(fixture));
                Assert.True(unchecked(fixture.Host.World.NowMs - secondAbandon) < 600_000);
            }

            // The mark does end: the bound is ten minutes, not for ever.
            await fixture.Host.World.AdvanceClockAsync(secondAbandon + 601_000 - fixture.Host.World.NowMs);
            await ThinkAsync(fixture, brain, 10);
            Assert.Equal(QuestStatus.Incomplete, await StatusAsync(fixture));
        }
        finally { await StopAsync(fixture, brain); }
    }
}

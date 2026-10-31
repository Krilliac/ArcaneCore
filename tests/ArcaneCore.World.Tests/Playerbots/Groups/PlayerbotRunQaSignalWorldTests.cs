using System.Diagnostics.Metrics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Data.World.Instances;
using ArcaneCore.Kernel.Instances;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Instances;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Playerbots.Qa;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;
using static ArcaneCore.World.Tests.Playerbots.Groups.RunFixtures;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// Runs given up and the signals reported to Bot QA, and a due run starting with the slots full of quest groups. Split from the former <c>PlayerbotRunWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotRunWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotRunQaSignalWorldTests : PlayerbotRunWorldTestBase
{
    [Theory]
    [InlineData("travel-timeout", "timeout")]
    [InlineData("no-path-to-entrance", "given_up")]
    public async Task ARunGivenUpForAContentReason_ReportsOneRunFailedSignalToBotQa_WithNoBotOrGroupIdentity_AndCountsItByOutcome(string reason, string outcome)
    {
        var bus = new PlayerbotQaSignalBus(64);
        (List<string> outcomes, MeterListener listener) = ListenToRuns();
        using MeterListener _ = listener;
        await using GroupTestWorld world = await StartWithQaAsync(bus);
        (Guid Id, string Name)[] members = await AddPartyAsync(world, "Qat");
        world.Coordinator.RunRoll = () => 0d;
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.Run is not null)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        world.Coordinator.RunRoll = () => 1d;
        // A run lasts under a minute here and MaxRunMinutes cannot go below 10 (validated), so the fault the group AI raises is injected the way its own are
        // (RunFault, applied by Advance): the same Finish, disband and report path as a real gather or travel timeout or a no-path.
        await world.OnWorldAsync(() =>
        {
            group.RunFault = reason;
            return true;
        });

        Assert.True(await world.RunUntilAsync(240_000, () => world.Coordinator.Groups.Count == 0), await world.TraceAsync());

        var signals = new List<QaSignal>();
        bus.Drain(signals, 16);
        Assert.True(signals.Count == 1, await world.TraceAsync());
        QaSignal signal = signals[0];
        Assert.Equal(QaSignalKind.RunFailed, signal.Kind);
        Assert.Equal(reason, signal.Reason);
        Assert.Equal(DungeonEntry.Deadmines, signal.Map);
        Assert.False(string.IsNullOrEmpty(signal.Stage));
        Assert.Null(signal.BotKey);
        Assert.Equal(0, signal.BotClass);
        string everything = signal.Message + "|" + signal.Detail + "|" + signal.Stage + "|" + signal.Reason + "|" + PlayerbotQaFingerprint.Key(signal);
        Assert.All(members, m => Assert.DoesNotContain(m.Name, everything, StringComparison.Ordinal));
        Assert.Equal($"{reason} in {signal.Detail} at {signal.Stage}", PlayerbotQaAggregator.Subject(signal, null));
        Assert.Equal(1, world.Coordinator.RunTotals.GivenUp);
        lock (outcomes) Assert.Contains(outcome, outcomes);
    }

    [Theory]
    [InlineData("no-path-to-entrance", true)]
    [InlineData("no-path-to-boss", true)]
    [InlineData("gather-timeout", true)]
    [InlineData("travel-timeout", true)]
    [InlineData("enter-refused-or-timeout", true)]
    [InlineData("regroup-timeout", true)]
    [InlineData("run-timeout", true)]
    [InlineData("objective-missing", true)]
    [InlineData("stalled", true)]
    [InlineData("wiped", true)]
    [InlineData("leader-stopped", false)]
    [InlineData("leader-left", false)]
    [InlineData("leader-gone", false)]
    [InlineData("too-few", false)]
    [InlineData("invites-unanswered", false)]
    [InlineData("disbanded", false)]
    [InlineData("aborted", false)]
    public void OnlyContentAndCodeReasons_AreFindings(string reason, bool finding)
        => Assert.Equal(finding, PlayerbotGroupCoordinator.PointsAtContentOrCode(reason));

    [Fact]
    public void ARealStepTimeout_GivesUpTheMachineAtItsStep_AndIsCountedAsATimeout()
    {
        var run = new PlayerbotRunMachine(new PlayerbotRunPlan(36, "The Deadmines", 0, 1, false, 5, 1, 36, default, default, 10, [], []), 3);
        run.Apply(PlayerbotRunSignal.Timeout("travel"));

        Assert.Equal(PlayerbotRunStep.GiveUp, run.Step);
        Assert.Equal(PlayerbotRunStep.Travel, run.FailedStep);
        Assert.Equal("travel-timeout", run.Reason);
        Assert.Equal(PlayerbotRunMeters.Timeout, PlayerbotRunMeters.OutcomeOf(run.Complete, run.Reason));
        Assert.Equal(PlayerbotRunMeters.GivenUp, PlayerbotRunMeters.OutcomeOf(false, "no-path-to-boss"));
        Assert.Equal(PlayerbotRunMeters.Completed, PlayerbotRunMeters.OutcomeOf(true, null));
    }

    [Fact]
    public async Task AGivenUpRun_WithBotQaOff_ChangesNothing()
    {
        await using GroupTestWorld world = await StartAsync();
        await AddPartyAsync(world, "Qao");
        world.Coordinator.RunRoll = () => 0d;
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.Run is not null)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        world.Coordinator.RunRoll = () => 1d;

        await world.OnWorldAsync(() =>
        {
            world.Coordinator.Forget(group.LeaderBotId);
            return true;
        });

        Assert.Empty(world.Coordinator.Groups);
        Assert.Equal((0, 1), world.Coordinator.RunTotals);
    }

    /// <summary>
    /// Quest groups never take the last group slot: with MaxGroups 2 and four bots waiting on an elite quest for two (two pairs, one goal),
    /// the second pair waits while a due dungeon run takes the slot the first pair left. Match() runs before StartRuns() in every scan, so
    /// before the reserve a quest group refilled every slot the moment one freed and no run ever started (w29: four quest groups held the
    /// slots 97.6% of the time, zero runs formed).
    /// </summary>
    [Fact]
    public async Task WithTheSlotsFullOfQuestGroups_ADueDungeonRun_StillStarts()
    {
        await using GroupTestWorld world = await StartAsync(o => { o.MaxBots = 12; o.Groups.MaxGroups = 2; }, quests: [EntranceEliteQuest]);
        world.Coordinator.RunRoll = () => 1d; // no run until the quest bots hold the slots
        var pairs = new List<(Guid Id, string Name)>();
        foreach (string name in new[] { "Slota", "Slotb", "Slotc", "Slotd" }) pairs.Add(await world.AddBotAsync(name, Human, Mage, 13, [Fireball], EntranceArea));
        Assert.True(await world.RunUntilAsync(60_000, () => pairs.All(b => world.HasQuest(b.Id, EntranceEliteQuest))), await world.TraceAsync());
        Assert.True(await world.RunUntilAsync(60_000, () => world.Coordinator.Groups.Count >= 1), await world.TraceAsync());
        await world.RunAsync(10_000); // the second pair would have formed by now without the reserve

        var party = await AddPartyAsync(world, "Slotrun");
        world.Coordinator.RunRoll = () => 0d;
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.Run is not null)),
            "no dungeon run started while quest groups held the slots" + Environment.NewLine + await world.TraceAsync());
        // No quest group ended to free the slot: the run took the one that was kept for it.
        Assert.Equal((0, 0), (world.Coordinator.Totals.Completed, world.Coordinator.Totals.Failed));
        Assert.True(world.Coordinator.Groups.Count(g => g.Run is null) <= 1, await world.TraceAsync());
        Assert.True(world.Coordinator.Groups.Count <= 2, await world.TraceAsync());
        Assert.Equal(1, world.Coordinator.QuestGroupLimit);
        foreach (var m in party) Assert.True(world.Coordinator.GroupOf(m.Id)?.Run is not null, await world.TraceAsync());
    }
}

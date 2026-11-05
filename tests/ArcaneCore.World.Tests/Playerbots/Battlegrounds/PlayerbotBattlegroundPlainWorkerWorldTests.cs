using ArcaneCore.World.Battlegrounds;
using ArcaneCore.World.Playerbots.Battlegrounds;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Battlegrounds.PlayerbotBattlegroundWorldTests;

namespace ArcaneCore.World.Tests.Playerbots.Battlegrounds;

/// <summary>
/// The coordinator's <c>MayQueue</c> guard (<c>BattlegroundFeature.HoldsQueueState</c>): on a cluster worker that is not the battleground holder the local manager keeps no queue state,
/// so a join would land in the holder's queue and this coordinator would never see the slot or the invitation. No bot may start a queue there. Unproven until the integration gate.
/// </summary>
public sealed class PlayerbotBattlegroundPlainWorkerWorldTests
{
    /// <summary>The worker-to-holder link of a plain worker whose holder is somewhere else: it takes everything and answers nothing.</summary>
    private sealed class AbsentHolderLink : IBattlegroundHolderLink
    {
        public void Submit(string workerId, BattlegroundCommand command)
        {
        }
    }

    [Fact]
    public async Task AFreeBotOnAPlainWorker_NeverStartsAQueue()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync();
        BattlegroundFeature feature = world.Battlegrounds();
        feature.ConfigureWorker("worker-plain", new AbsentHolderLink());
        Assert.False(feature.HoldsQueueState);

        await RunAsync(world, "bg-plain-worker-no-queue", async context =>
        {
            ScenarioBot bot = await AutonomousAsync(context, "Bgplainwka", race: 1);
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);

            await context.StepAsync("for two game minutes the bot queues nowhere", async () =>
            {
                await context.IdleAsync(TimeSpan.FromMinutes(2));
                ScenarioContext.ExpectEqual(0, await context.ReadAsync(() => coordinator.Totals.Queued), "bots queued");
                ScenarioContext.Expect(!await context.ReadAsync(() => Manager(context).StateOf(bot.Guid).InAnyQueue), "the local manager shows a queue");
            });
        });
    }
}

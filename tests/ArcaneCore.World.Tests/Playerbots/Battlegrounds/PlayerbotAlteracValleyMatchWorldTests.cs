using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.World.Battlegrounds;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Battlegrounds;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Battlegrounds.PlayerbotBattlegroundWorldTests;
using Content = ArcaneCore.World.Tests.Playerbots.Battlegrounds.BattlegroundBotTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Battlegrounds;

/// <summary>
/// Bots alone must be able to start Alterac Valley. The live row asks 20 players per team (battleground_template: min 20, max 40) while
/// <c>MaxBotsPerBattleground</c> defaults to 20, i.e. 10 per team, so the queue never reached its minimum. This is the same shape at a small
/// scale: a bot cap of 4 (2 per team) against a template minimum of 3 per team. The fixture's other templates keep a minimum of 1, which hid it.
/// </summary>
public sealed class PlayerbotAlteracValleyMatchWorldTests
{
    private const uint MinPerTeam = 3;
    private const int BotsPerTeam = 3;

    private static async Task<ScenarioBot[]> LoginTeamsAsync(ScenarioContext context)
    {
        var bots = new List<ScenarioBot>();
        for (int i = 0; i < BotsPerTeam; i++) bots.Add(await AutonomousAsync(context, "Bgavmina" + (char)('a' + i), race: 1));
        for (int i = 0; i < BotsPerTeam; i++) bots.Add(await AutonomousAsync(context, "Bgavminh" + (char)('a' + i), race: 2));
        return [.. bots];
    }

    [Fact]
    public async Task Av_CapBelowTwiceTheTemplateMinimum_IsRaised_SoBotsAloneStartTheMatch_AndPlayTheObjective()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAlteracValleyAsync(o => o.Battlegrounds.MaxBotsPerBattleground = 4, MinPerTeam);
        await RunAsync(world, "bg-av-raised-cap", async context =>
        {
            ScenarioBot[] bots = await LoginTeamsAsync(context);
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            BattlegroundFeature feature = context.Services.GetRequiredService<BattlegroundFeature>();

            await context.StepAsync("all six bots are in one Alterac Valley match on map 30", async () =>
            {
                await WaitExplainedAsync(context, "all bots in the match", () => bots.All(b => coordinator.Find(b.BotId)?.Phase == PlayerbotBattlegroundPhase.InMatch),
                    TimeSpan.FromMinutes(5), bots);
                Battleground?[] matches = await context.ReadAsync(() => bots.Select(b => feature.BattlegroundOf(b.Guid)).ToArray());
                ScenarioContext.Expect(matches.All(m => m is AlteracValley), "a bot is not in an Alterac Valley match");
                ScenarioContext.Expect(matches.All(m => ReferenceEquals(m, matches[0])), "the bots are spread over several matches");
                await context.ReadAsync(() =>
                {
                    foreach (ScenarioBot bot in bots)
                    {
                        ScenarioContext.Expect(matches[0]!.PlayerTeam(bot.Guid) is not null, $"{bot.Name} has no team in the match");
                        ScenarioContext.ExpectEqual(30u, bot.RequirePlayer().MapId, $"{bot.Name}'s map");
                    }

                    return true;
                });
                ScenarioContext.ExpectEqual(BotsPerTeam * 2, await context.ReadAsync(() => matches[0]!.PlayerCount), "players in the match");
            });

            var av = (AlteracValley)(await context.BattlegroundOfAsync(bots[0]))!;
            await context.StepAsync("the match reaches InProgress", () => context.WaitUntilAsync("in progress", () => av.Status == BattlegroundStatus.InProgress, StartWait));

            await context.StepAsync("a bot takes an objective (a graveyard assault is scored)", () => WaitExplainedAsync(context, "a graveyard assaulted",
                () => bots.Any(b => av.ScoreOf(b.Guid) is AvScore { GraveyardsAssaulted: > 0 }) || av.NodeState(AlteracValley.NodeIcebloodGrave) == AlteracValley.PointAssaulted,
                TimeSpan.FromMinutes(5), bots));
            ScenarioContext.Expect(!await context.ReadAsync(() => coordinator.Refusals.Contains((BattlegroundType.AlteracValley, PlayerbotBattlegroundRefusal.CapBelowMinimum))),
                "AV was refused with the raise on");
        });
    }

    [Fact]
    public async Task Av_RaiseCapOff_RefusesTheTypeWithCapBelowMinimum_AndNoBotQueues()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAlteracValleyAsync(o =>
        {
            o.Battlegrounds.MaxBotsPerBattleground = 4;
            o.Battlegrounds.RaiseCapToMatchMinimum = false;
        }, MinPerTeam);
        await RunAsync(world, "bg-av-cap-below-minimum", async context =>
        {
            ScenarioBot[] bots = await LoginTeamsAsync(context);
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);

            await context.StepAsync("the coordinator refuses AV once", () => WaitExplainedAsync(context, "CapBelowMinimum logged",
                () => coordinator.Refusals.Contains((BattlegroundType.AlteracValley, PlayerbotBattlegroundRefusal.CapBelowMinimum)), TimeSpan.FromMinutes(2), bots));

            await context.StepAsync("for two more game minutes no bot queues and no match exists", async () =>
            {
                await context.IdleAsync(TimeSpan.FromMinutes(2));
                bool anyQueued = await context.ReadAsync(() => bots.Any(b => Manager(context).StateOf(b.Guid).InAnyQueue || Manager(context).StateOf(b.Guid).InBattleground));
                ScenarioContext.Expect(!anyQueued, "a bot queued for or entered Alterac Valley although the cap is below the minimum");
                ScenarioContext.ExpectEqual(0, await context.ReadAsync(() => coordinator.Totals.Queued), "bots queued");
                ScenarioContext.ExpectEqual(1, await context.ReadAsync(() => coordinator.Refusals.Count(r => r.Type == BattlegroundType.AlteracValley
                    && r.Why == PlayerbotBattlegroundRefusal.CapBelowMinimum)), "the refusal is recorded once");
            });
        });
    }
}

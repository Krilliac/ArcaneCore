using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using ArcaneCore.World.Playerbots.Battlegrounds;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Battlegrounds.PlayerbotBattlegroundWorldTests;

namespace ArcaneCore.World.Tests.Playerbots.Battlegrounds;

/// <summary>
/// <c>RaiseCapToFillTemplate = false</c> keeps the configured <c>MaxBotsPerBattleground</c> as it is, with no raise and no refusal: the
/// behaviour before the option. An operator's cap of 10 against Arathi Basin's z2815 minimum of 8 per team lets 5 bots a team queue, which
/// helps real players reach the minimum, and the type is not refused.
/// </summary>
public sealed class PlayerbotBattlegroundOptionsRaiseOffWorldTests
{
    [Fact]
    public async Task RaiseOff_CapTen_ArathiBasinStaysEligible_AndBotsQueueAtMostFivePerTeam()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartArathiBasinAsync(o =>
        {
            o.MaxBots = 16;
            o.Battlegrounds.MaxBotsPerBattleground = 10;
            o.Battlegrounds.RaiseCapToFillTemplate = false;
        }, abMinPlayersPerTeam: 8);
        await RunAsync(world, "bg-ab-raise-off-cap-ten", async context =>
        {
            var bots = new List<ScenarioBot>();
            for (int i = 0; i < 6; i++) bots.Add(await AutonomousAsync(context, "Bgabroa" + (char)('a' + i), race: 1));
            for (int i = 0; i < 6; i++) bots.Add(await AutonomousAsync(context, "Bgabroh" + (char)('a' + i), race: 2));
            ScenarioBot[] all = [.. bots];
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);

            await context.StepAsync("ten bots queue for Arathi Basin, five a team", () => WaitExplainedAsync(context, "ten bots queued",
                () => coordinator.Totals.Queued == 10, TimeSpan.FromMinutes(3), all));

            await context.StepAsync("for two more game minutes no eleventh bot queues, nothing is refused and the cap is not raised", async () =>
            {
                await context.IdleAsync(TimeSpan.FromMinutes(2));
                ScenarioContext.ExpectEqual(10, await context.ReadAsync(() => coordinator.Totals.Queued), "bots queued");
                (int alliance, int horde) = await context.ReadAsync(() => (
                    all.Count(b => b.RequirePlayer().Team == Team.Alliance && Manager(context).StateOf(b.Guid).InAnyQueue),
                    all.Count(b => b.RequirePlayer().Team == Team.Horde && Manager(context).StateOf(b.Guid).InAnyQueue)));
                ScenarioContext.ExpectEqual((5, 5), (alliance, horde), "bots queued per team");
                ScenarioContext.Expect(!await context.ReadAsync(() => coordinator.Refusals.Any(r => r.Type == BattlegroundType.ArathiBasin)), "Arathi Basin was refused");
                ScenarioContext.ExpectEqual(0, await context.ReadAsync(() => Manager(context).RunningBattlegrounds.Count()), "matches (8 a team needed)");
                ScenarioContext.ExpectEqual(0, await context.ReadAsync(() => coordinator.RaiseWarnings), "raise warnings with the raise off");
            });
        });
    }
}

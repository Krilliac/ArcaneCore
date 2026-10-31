using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
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
/// Alterac Valley matches with the roll-off, a cap below the minimum, and RaiseCap off. Split from the former <c>PlayerbotAlteracValleyMatchWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotAlteracValleyMatchWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotAlteracValleyMatchCapWorldTests : PlayerbotAlteracValleyMatchWorldTestBase
{
    /// <summary>
    /// The open slots alone pull bots in (<c>PlayerbotBattlegroundDemand.FillNeed</c>): once the match runs, the chance roll is turned all but
    /// off (1e-9 a minute), so no bot queues by its roll. The match has 2 open slots a team; 3 free bots a team log in and exactly 2 a team set
    /// off (the bots on their way count against the open slots), the third stays where it is. A leaver's slot then pulls exactly one more.
    /// </summary>
    [Fact]
    public async Task Av_WithTheRollOff_OpenSlotsPullExactlyThatManyBots_AndALeaverPullsOneMore()
    {
        // Template: 3 minimum, 5 maximum per team; cap 4 (below 2 x 3) raised to 2 x 5 = 10.
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAlteracValleyAsync(o =>
        {
            o.MaxBots = 16;
            o.Battlegrounds.MaxBotsPerBattleground = 4;
        }, avMinPlayersPerTeam: 3, avMaxPlayersPerTeam: 5);
        await RunAsync(world, "bg-av-open-slots-pull", async context =>
        {
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            BattlegroundFeature feature = context.Services.GetRequiredService<BattlegroundFeature>();
            ScenarioBot[] first = await LoginAsync(context, "Bgavrx", perTeam: 3);

            Battleground match = await context.StepAsync("three bots per team start the match; then the chance roll is turned all but off", async () =>
            {
                await WaitInMatchAsync(context, coordinator, "the first six bots in the match", first);
                Battleground? m = await context.ReadAsync(() => feature.BattlegroundOf(first[0].Guid));
                ScenarioContext.Expect(m is AlteracValley, "no Alterac Valley match");
                ScenarioContext.ExpectEqual((3, 3), await context.ReadAsync(() => TeamCounts(m!)), "players per team at the start");
                ScenarioContext.ExpectEqual(6, await context.ReadAsync(() => coordinator.Totals.Queued), "bots queued");
                await context.ReadAsync(() => coordinator.Options.QueueChancePerMinute = 1e-9f);
                return m!;
            });

            ScenarioBot[] extras = await LoginAsync(context, "Bgavry", perTeam: 3);
            var setOff = new HashSet<Guid>();
            await context.StepAsync("exactly two bots per team set off for the two open slots per team and fill the match to five a side", async () =>
            {
                await WaitExplainedAsync(context, "the match holds five a side", () =>
                {
                    foreach (ScenarioBot b in Followed(coordinator, extras)) setOff.Add(b.BotId);
                    return TeamCounts(match) == (5, 5);
                }, TimeSpan.FromMinutes(5), extras);
                ScenarioContext.ExpectEqual(4, setOff.Count, "extra bots that set off");
                ScenarioContext.ExpectEqual(2, extras.Count(b => b.Name.Contains("Bgavrya", StringComparison.Ordinal) && setOff.Contains(b.BotId)), "Alliance extras that set off");
                ScenarioContext.ExpectEqual(10, await context.ReadAsync(() => coordinator.Totals.Queued), "bots queued");
            });

            ScenarioBot[] idle = extras.Where(b => !setOff.Contains(b.BotId)).ToArray();
            await context.StepAsync("for a game minute the third bot of each team stays where it is: the match is full and the roll is off", async () =>
            {
                ScenarioContext.ExpectEqual(2, idle.Length, "extras left idle");
                await context.IdleAsync(TimeSpan.FromMinutes(1));
                ScenarioContext.Expect(await context.ReadAsync(() => !Followed(coordinator, idle).Any() && idle.All(b => !Manager(context).StateOf(b.Guid).InAnyQueue)),
                    "an idle extra set off or queued with no open slot");
                ScenarioContext.ExpectEqual(10, await context.ReadAsync(() => coordinator.Totals.Queued), "bots queued");
            });

            ScenarioBot leaver = first[0]; // Alliance
            ScenarioBot idleAlliance = idle.Single(b => b.Name.Contains("Bgavrya", StringComparison.Ordinal));
            ScenarioBot idleHorde = idle.Single(b => b.Name.Contains("Bgavryh", StringComparison.Ordinal));
            await context.StepAsync("an Alliance bot leaves the match", async () =>
            {
                await context.ScriptAsync(leaver);
                for (int attempt = 0; attempt < 12 && await context.ReadAsync(() => match.PlayerTeam(leaver.Guid) is not null); attempt++)
                {
                    // The handler refuses a leave in combat while the match runs (BattleGroundHandler.cpp:510-525): ask again.
                    ScenarioContext.Expect(await leaver.LeaveBattlefieldAsync(30), "leave refused");
                    await context.IdleAsync(TimeSpan.FromSeconds(5));
                }

                ScenarioContext.Expect(await context.ReadAsync(() => match.PlayerTeam(leaver.Guid) is null), "the leaver is still in the match");
                ScenarioContext.Expect(await context.ReadAsync(() => match.Status < BattlegroundStatus.WaitLeave), "the match ended");
            });

            await context.StepAsync("the idle Alliance bot takes the freed slot; the idle Horde bot stays", async () =>
            {
                await WaitExplainedAsync(context, "the idle Alliance bot plays in the match", () => coordinator.Find(idleAlliance.BotId)?.Phase == PlayerbotBattlegroundPhase.InMatch
                    && ReferenceEquals(feature.BattlegroundOf(idleAlliance.Guid), match), TimeSpan.FromMinutes(5), idleAlliance);
                ScenarioContext.ExpectEqual((5, 5), await context.ReadAsync(() => TeamCounts(match)), "players per team after the backfill");
                ScenarioContext.ExpectEqual(11, await context.ReadAsync(() => coordinator.Totals.Queued), "bots queued");
                ScenarioContext.Expect(await context.ReadAsync(() => coordinator.Find(idleHorde.BotId) is null && !Manager(context).StateOf(idleHorde.Guid).InAnyQueue),
                    "the idle Horde bot set off with no open Horde slot");
            });
        });
    }

    [Fact]
    public async Task Av_CapBelowTheMinimum_IsOverriddenWithOneWarning()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAlteracValleyAsync(o => o.Battlegrounds.MaxBotsPerBattleground = 4, MinPerTeam);
        await RunAsync(world, "bg-av-raise-warning", async context =>
        {
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            await AutonomousAsync(context, "Bgavwarn", race: 1); // the coordinator scans while bots run
            await context.StepAsync("the first scan warns once that the cap is raised", async () =>
            {
                await context.WaitUntilAsync("a warning", () => coordinator.RaiseWarnings == 1, TimeSpan.FromSeconds(30));
                await context.IdleAsync(TimeSpan.FromSeconds(20));
                ScenarioContext.ExpectEqual(1, await context.ReadAsync(() => coordinator.RaiseWarnings), "warnings with nothing changed");
            });

            await context.StepAsync("a reload that changes the cap warns again; one that turns the raise off does not", async () =>
            {
                await context.ReadAsync(() => coordinator.Options.MaxBotsPerBattleground = 2);
                await context.WaitUntilAsync("a second warning", () => coordinator.RaiseWarnings == 2, TimeSpan.FromSeconds(30));
                await context.ReadAsync(() => coordinator.Options.RaiseCapToFillTemplate = false);
                await context.IdleAsync(TimeSpan.FromSeconds(20));
                ScenarioContext.ExpectEqual(2, await context.ReadAsync(() => coordinator.RaiseWarnings), "warnings after the raise was turned off");
            });
        });
    }

    [Fact]
    public async Task Av_RaiseCapOff_KeepsTheConfiguredCap_BotsQueueUpToIt_NoRefusal_AndNoMatchStarts()
    {
        // The behaviour before RaiseCapToFillTemplate: cap 4 allows 2 bots a team, below the template minimum of 3, so bots queue but no
        // match starts from bots alone (they would help a real player's queue).
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAlteracValleyAsync(o =>
        {
            o.Battlegrounds.MaxBotsPerBattleground = 4;
            o.Battlegrounds.RaiseCapToFillTemplate = false;
        }, MinPerTeam);
        await RunAsync(world, "bg-av-raise-off", async context =>
        {
            ScenarioBot[] bots = await LoginTeamsAsync(context);
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);

            await context.StepAsync("two bots a team queue for AV, the configured cap", () => WaitExplainedAsync(context, "four bots queued",
                () => coordinator.Totals.Queued == 4, TimeSpan.FromMinutes(2), bots));

            await context.StepAsync("for two more game minutes no fifth bot queues, nothing is refused and no match exists", async () =>
            {
                await context.IdleAsync(TimeSpan.FromMinutes(2));
                ScenarioContext.ExpectEqual(4, await context.ReadAsync(() => coordinator.Totals.Queued), "bots queued");
                (int alliance, int horde) = await context.ReadAsync(() => (
                    bots.Count(b => b.RequirePlayer().Team == Team.Alliance && Manager(context).StateOf(b.Guid).InAnyQueue),
                    bots.Count(b => b.RequirePlayer().Team == Team.Horde && Manager(context).StateOf(b.Guid).InAnyQueue)));
                ScenarioContext.ExpectEqual((2, 2), (alliance, horde), "bots queued per team");
                ScenarioContext.Expect(!await context.ReadAsync(() => coordinator.Refusals.Any(r => r.Type == BattlegroundType.AlteracValley)), "AV was refused");
                ScenarioContext.ExpectEqual(0, await context.ReadAsync(() => Manager(context).RunningBattlegrounds.Count()), "matches");
                ScenarioContext.ExpectEqual(0, await context.ReadAsync(() => coordinator.RaiseWarnings), "raise warnings with the raise off");
            });
        });
    }
}

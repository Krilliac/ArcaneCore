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
/// Bots alone must be able to start Alterac Valley and fill it. The live row asks 20 players per team and takes 40 (battleground_template:
/// min 20, max 40) while <c>MaxBotsPerBattleground</c> defaults to 20, i.e. 10 per team, so the queue never reached its minimum.
/// <c>RaiseCapToFillTemplate</c> raises such a type's cap to 2 x the template maximum (80), and the server's queue invites queued players
/// into a preparing or running match with free slots (BattlegroundQueue.CheckFreeSlots, vmangos FillPlayersToBG), so bots keep queuing
/// until each team holds the maximum and backfill a leaver's slot. These are the same shapes at a small scale: a bot cap of 4 (2 per team)
/// against a template minimum of 3 per team. The fixture's other templates keep a minimum of 1, which hid it.
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
                ScenarioContext.ExpectEqual(0, await context.ReadAsync(() => coordinator.Totals.Declined), "bots declined an invitation at the exact cap");
            });

            var av = (AlteracValley)(await context.BattlegroundOfAsync(bots[0]))!;
            await context.StepAsync("the match reaches InProgress", () => context.WaitUntilAsync("in progress", () => av.Status == BattlegroundStatus.InProgress, StartWait));

            await context.StepAsync("a bot takes an objective (a graveyard assault is scored)", () => WaitExplainedAsync(context, "a graveyard assaulted",
                () => bots.Any(b => av.ScoreOf(b.Guid) is AvScore { GraveyardsAssaulted: > 0 }) || av.NodeState(AlteracValley.NodeIcebloodGrave) == AlteracValley.PointAssaulted,
                TimeSpan.FromMinutes(5), bots));
            ScenarioContext.Expect(!await context.ReadAsync(() => coordinator.Refusals.Any(r => r.Type == BattlegroundType.AlteracValley)),
                "AV was refused with the raise on");
        });
    }

    private static async Task<ScenarioBot[]> LoginAsync(ScenarioContext context, string prefix, int perTeam)
    {
        var bots = new List<ScenarioBot>();
        for (int i = 0; i < perTeam; i++) bots.Add(await AutonomousAsync(context, prefix + "a" + (char)('a' + i), race: 1));
        for (int i = 0; i < perTeam; i++) bots.Add(await AutonomousAsync(context, prefix + "h" + (char)('a' + i), race: 2));
        return [.. bots];
    }

    /// <summary>The match's participants per team (Alliance, Horde); world thread.</summary>
    private static (int Alliance, int Horde) TeamCounts(Battleground match)
    {
        int alliance = 0, horde = 0;
        foreach ((ObjectGuid _, Team team) in match.Participants())
        {
            if (team == Team.Alliance) alliance++;
            else horde++;
        }

        return (alliance, horde);
    }

    private static Task WaitInMatchAsync(ScenarioContext context, PlayerbotBattlegroundCoordinator coordinator, string what, ScenarioBot[] bots)
        => WaitExplainedAsync(context, what, () => bots.All(b => coordinator.Find(b.BotId)?.Phase == PlayerbotBattlegroundPhase.InMatch), TimeSpan.FromMinutes(5), bots);

    [Fact]
    public async Task Av_RunningMatch_IsFilledByBots_BeyondTheMinimum_ToTheTemplateMaximumPerTeam()
    {
        // Template: 3 minimum, 5 maximum per team. Cap 4 is below 2 x 3, so it is raised to 2 x 5 = 10: the match takes 5 bots per team.
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAlteracValleyAsync(o =>
        {
            o.MaxBots = 16;
            o.Battlegrounds.MaxBotsPerBattleground = 4;
        }, avMinPlayersPerTeam: 3, avMaxPlayersPerTeam: 5);
        await RunAsync(world, "bg-av-fill-to-max", async context =>
        {
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            BattlegroundFeature feature = context.Services.GetRequiredService<BattlegroundFeature>();
            ScenarioBot[] first = await LoginAsync(context, "Bgavfil", perTeam: 3);

            Battleground match = await context.StepAsync("three bots per team start an Alterac Valley match at the template minimum", async () =>
            {
                await WaitInMatchAsync(context, coordinator, "the first six bots in the match", first);
                Battleground?[] matches = await context.ReadAsync(() => first.Select(b => feature.BattlegroundOf(b.Guid)).ToArray());
                ScenarioContext.Expect(matches.All(m => m is AlteracValley && ReferenceEquals(m, matches[0])), "the first bots are not in one Alterac Valley match");
                ScenarioContext.ExpectEqual((3, 3), await context.ReadAsync(() => TeamCounts(matches[0]!)), "players per team at the start");
                return matches[0]!;
            });

            ScenarioBot[] more = await LoginAsync(context, "Bgavmor", perTeam: 2);
            await context.StepAsync("two more bots per team queue and are invited into the same match, not a new one", async () =>
            {
                await WaitInMatchAsync(context, coordinator, "the later four bots in the match", more);
                Battleground?[] matches = await context.ReadAsync(() => more.Select(b => feature.BattlegroundOf(b.Guid)).ToArray());
                ScenarioContext.Expect(matches.All(m => ReferenceEquals(m, match)), "a later bot is not in the running match");
                ScenarioContext.ExpectEqual((5, 5), await context.ReadAsync(() => TeamCounts(match)), "players per team after the fill");
                ScenarioContext.ExpectEqual(10, await context.ReadAsync(() => match.PlayerCount), "players in the match");
                ScenarioContext.ExpectEqual(1, await context.ReadAsync(() => Manager(context).RunningBattlegrounds.Count(b => b.Type == BattlegroundType.AlteracValley)),
                    "Alterac Valley matches");
                ScenarioContext.ExpectEqual(0, await context.ReadAsync(() => coordinator.Totals.Declined), "invitations declined");
            });

            await context.StepAsync("the status line names the raised cap", async () =>
            {
                string status = (await context.ReadAsync(() => coordinator.Report()))[0];
                ScenarioContext.Expect(status.Contains("AlteracValley raised to 10", StringComparison.Ordinal), $"status line: {status}");
            });
        });
    }

    [Fact]
    public async Task Av_MidMatchLeaver_SlotIsBackfilledByABot()
    {
        // Template: 3 minimum, 4 maximum per team; cap 4 (below 2 x 3) raised to 2 x 4 = 8.
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAlteracValleyAsync(o =>
        {
            o.MaxBots = 16;
            o.Battlegrounds.MaxBotsPerBattleground = 4;
        }, avMinPlayersPerTeam: 3, avMaxPlayersPerTeam: 4);
        await RunAsync(world, "bg-av-backfill-leaver", async context =>
        {
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            BattlegroundFeature feature = context.Services.GetRequiredService<BattlegroundFeature>();
            ScenarioBot[] bots = await LoginAsync(context, "Bgavlv", perTeam: 4);

            Battleground match = await context.StepAsync("four bots per team fill one Alterac Valley match", async () =>
            {
                await WaitInMatchAsync(context, coordinator, "all eight bots in the match", bots);
                Battleground? m = await context.ReadAsync(() => feature.BattlegroundOf(bots[0].Guid));
                ScenarioContext.Expect(m is AlteracValley && await context.ReadAsync(() => bots.All(b => ReferenceEquals(feature.BattlegroundOf(b.Guid), m))),
                    "the bots are not in one Alterac Valley match");
                ScenarioContext.ExpectEqual((4, 4), await context.ReadAsync(() => TeamCounts(m!)), "players per team");
                return m!;
            });

            ScenarioBot spare = await AutonomousAsync(context, "Bgavlvspare", race: 1);
            ScenarioBot leaver = bots[0];
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

            await context.StepAsync("a bot takes the freed Alliance slot of the same match", async () =>
            {
                await WaitExplainedAsync(context, "the spare bot plays in the match", () => coordinator.Find(spare.BotId)?.Phase == PlayerbotBattlegroundPhase.InMatch
                    && ReferenceEquals(feature.BattlegroundOf(spare.Guid), match), TimeSpan.FromMinutes(5), spare);
                ScenarioContext.ExpectEqual((4, 4), await context.ReadAsync(() => TeamCounts(match)), "players per team after the backfill");
                ScenarioContext.ExpectEqual(1, await context.ReadAsync(() => Manager(context).RunningBattlegrounds.Count(b => b.Type == BattlegroundType.AlteracValley)),
                    "Alterac Valley matches");
            });
        });
    }

    /// <summary>The bots of <paramref name="candidates"/> the coordinator follows now (any phase); world thread.</summary>
    private static IEnumerable<ScenarioBot> Followed(PlayerbotBattlegroundCoordinator coordinator, IEnumerable<ScenarioBot> candidates)
        => candidates.Where(b => coordinator.Find(b.BotId) is not null);

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

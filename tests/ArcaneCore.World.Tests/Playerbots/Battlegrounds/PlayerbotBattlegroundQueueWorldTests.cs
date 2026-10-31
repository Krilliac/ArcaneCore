using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Battlegrounds;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Battlegrounds;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;
using Content = ArcaneCore.World.Tests.Playerbots.Battlegrounds.BattlegroundBotTestContent;
using ArcaneCore.World.Bots;

namespace ArcaneCore.World.Tests.Playerbots.Battlegrounds;

/// <summary>
/// Queueing, the queue wait, the caps and invitations. Split from the former <c>PlayerbotBattlegroundWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotBattlegroundWorldTests"/>.
/// </summary>
public sealed class PlayerbotBattlegroundQueueWorldTests : PlayerbotBattlegroundWorldTests
{
    // --- the queue: hello, join, port, play, leave -------------------------------------------------------------------------

    [Fact]
    public async Task Wsg_TwoAutonomousBots_Queue_TakeTheInvitation_PortIn_AndLeaveWhenTheMatchEnds()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync(o => o.Battlegrounds.MaxBotsPerBattleground = 4);
        await RunAsync(world, "bg-queue-flow", async context =>
        {
            ScenarioBot ally = await AutonomousAsync(context, "Bgflowally", race: 1);
            ScenarioBot horde = await AutonomousAsync(context, "Bgflowhorde", race: 2);
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            BattlegroundFeature feature = context.Services.GetRequiredService<BattlegroundFeature>();

            await context.StepAsync("both bots walk to their battlemaster, say hello and join the Warsong Gulch queue", async () =>
            {
                await context.WaitUntilAsync("both bots are queued or past it", () => coordinator.Totals.Queued == 2, TimeSpan.FromSeconds(60));
                // The hello's answer (SMSG_BATTLEFIELD_LIST) and the join's (SMSG_BATTLEFIELD_STATUS) reached each bot like a client.
                ScenarioContext.Expect(ally.Received(WorldOpcode.SmsgBattlefieldList, p => p).Count > 0, "no battlefield list for the Alliance bot");
                ScenarioContext.Expect(horde.Received(WorldOpcode.SmsgBattlefieldList, p => p).Count > 0, "no battlefield list for the Horde bot");
                ScenarioContext.Expect(ally.Received(WorldOpcode.SmsgBattlefieldStatus, ScenarioBattlegrounds.BattlefieldStatus).Any(s => s.Status == BattlegroundStatus.WaitQueue
                    || s.Status == BattlegroundStatus.WaitJoin), "no queue status for the Alliance bot");
            });

            Battleground match = await context.StepAsync("both bots take the invitation and port into one match", async () =>
            {
                await context.WaitUntilAsync("both bots play the match", () => coordinator.Find(ally.BotId)?.Phase == PlayerbotBattlegroundPhase.InMatch
                    && coordinator.Find(horde.BotId)?.Phase == PlayerbotBattlegroundPhase.InMatch, TimeSpan.FromSeconds(60));
                Battleground? a = await context.ReadAsync(() => feature.BattlegroundOf(ally.Guid));
                Battleground? h = await context.ReadAsync(() => feature.BattlegroundOf(horde.Guid));
                ScenarioContext.Expect(a is WarsongGulch && ReferenceEquals(a, h), "the bots are not in one Warsong Gulch match");
                ScenarioContext.ExpectEqual(2, coordinator.Totals.Entered, "invitations taken");
                ScenarioContext.ExpectEqual((PlayerbotGoalKind?)PlayerbotGoalKind.Battleground, coordinator.GoalOf(ally.BotId)?.Goal, "the Alliance bot's goal");
                // No new queue once they are back (live, as .reload config changes it): what follows is about this match alone.
                coordinator.Options.QueueChancePerMinute = 0f;
                return a!;
            });

            await context.StepAsync("the gates open", () => context.WaitUntilAsync("the match is in progress", () => match.Status == BattlegroundStatus.InProgress, StartWait));

            long mark = ally.Mark();
            await context.StepAsync("the match ends: both bots leave through CMSG_LEAVE_BATTLEFIELD and are back where they queued", async () =>
            {
                await context.ReadAsync(() =>
                {
                    match.EndBattleground(Team.Alliance);
                    return true;
                });
                await ally.WaitForPacketAsync(WorldOpcode.SmsgBattlefieldWin, p => p, since: mark);
                await WaitExplainedAsync(context, "both bots are back on their continent", () => ally.Session!.Player is { IsInWorld: true, MapId: 0 } a
                    && horde.Session!.Player is { IsInWorld: true, MapId: 0 } h && feature.BattlegroundOf(a.Guid) is null && feature.BattlegroundOf(h.Guid) is null,
                    TimeSpan.FromSeconds(60), ally, horde);
                await context.WaitUntilAsync("the coordinator let both go", () => !coordinator.Holds(ally.BotId) && !coordinator.Holds(horde.BotId));
                ScenarioContext.ExpectEqual(2, coordinator.Totals.Left, "bots that left after the match");
                // They left the match well before the server's two-minute removal, by their own request (BattleGroundHandler.cpp:509-525).
                ScenarioContext.Expect(match.Status == BattlegroundStatus.WaitLeave, "the match was already removed: the bots did not leave by themselves");
                // A leave after the end is no desertion (vmangos Player::LeaveBattleground casts Deserter only while the match runs).
                SpellFeature spells = context.Services.GetRequiredService<SpellFeature>();
                ScenarioContext.Expect(!await context.ReadAsync(() => spells.System.HasAura(ally.RequirePlayer(), BattlegroundConstants.SpellDeserter)), "the Alliance bot deserted");
                (float x, float y) = await ally.ReadAsync(p => (p.X, p.Y));
                ScenarioContext.Expect(MathF.Abs(x - StartX) < 15f && MathF.Abs(y - StartY) < 15f, $"the Alliance bot is not back at its battlemaster ({x}, {y})");
            });

            await context.StepAsync("the brain drives both bots again", () => context.WaitUntilAsync("the bots' goals are their brains'", () =>
                context.Services.GetRequiredService<ManagedPlayerbotFeature>().Snapshot().Where(s => s.BotId == ally.BotId || s.BotId == horde.BotId)
                    .All(s => s.Goal != PlayerbotGoalKind.Battleground && s.Group is null)));
        });
    }

    [Fact]
    public async Task Queue_LeftAfterMaxQueueWaitMinutes_AndTheTypeIsSetAside()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync(o => o.Battlegrounds.MaxQueueWaitMinutes = 1);
        await RunAsync(world, "bg-queue-timeout", async context =>
        {
            ScenarioBot ally = await AutonomousAsync(context, "Bgwaitally", race: 1);
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            await context.StepAsync("the lone bot queues (no Horde: no match)", () => context.WaitUntilAsync("queued",
                () => coordinator.Find(ally.BotId)?.Phase == PlayerbotBattlegroundPhase.Queued && Manager(context).StateOf(ally.Guid).InAnyQueue, TimeSpan.FromSeconds(60)));
            await context.StepAsync("after a minute it leaves the queue through CMSG_BATTLEFIELD_PORT (action 0)", async () =>
            {
                await context.WaitUntilAsync("the queue is left", () => coordinator.Totals.QueueTimeouts == 1, TimeSpan.FromSeconds(90));
                ScenarioContext.Expect(!await context.ReadAsync(() => Manager(context).StateOf(ally.Guid).InAnyQueue), "still queued");
                ScenarioContext.Expect(ally.Received(WorldOpcode.SmsgBattlefieldStatus, ScenarioBattlegrounds.BattlefieldStatus).Any(s => s.MapId == 0), "no empty status after the leave");
            });
            await context.StepAsync("it does not queue for the same type again for a while", async () =>
            {
                await context.IdleAsync(TimeSpan.FromSeconds(30));
                ScenarioContext.ExpectEqual(1, coordinator.Totals.Queued, "queues joined");
                ScenarioContext.Expect(!await context.ReadAsync(() => Manager(context).StateOf(ally.Guid).InAnyQueue), "queued again at once");
            });
        });
    }

    // --- caps and invitations ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Caps_HoldAgainstTheManagersOwnCounts_OneBotPerTeamUnderACapOfTwo()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync(o => o.Battlegrounds.MaxBotsPerBattleground = 2,
            BattlegroundType.WarsongGulch, BattlegroundType.ArathiBasin);
        await RunAsync(world, "bg-caps", async context =>
        {
            // Three bots a side, two battleground types offered (Warsong Gulch and Arathi Basin): under a cap of two per battleground and one
            // per team, at most two bots a side ever get anything, and one bot a side is left to its brain.
            ScenarioBot[] bots =
            [
                await AutonomousAsync(context, "Bgcapallya", 1), await AutonomousAsync(context, "Bgcapallyb", 1), await AutonomousAsync(context, "Bgcapallyc", 1),
                await AutonomousAsync(context, "Bgcaphordea", 2), await AutonomousAsync(context, "Bgcaphordeb", 2), await AutonomousAsync(context, "Bgcaphordec", 2),
            ];
            BattlegroundManager manager = Manager(context);
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            var violations = new List<string>();
            BattlegroundType[] types = [BattlegroundType.WarsongGulch, BattlegroundType.ArathiBasin];

            // Every tick, per type: the bots the manager holds (queued for the type, or participants of its running matches) never exceed
            // one per team (min(MaxPlayersPerTeam, ceil(2 / 2))), so never the cap of two. Done when both types run a match of one bot a side.
            bool Check()
            {
                bool full = true;
                foreach (BattlegroundType type in types)
                {
                    (int queuedA, int queuedH) = manager.QueuedTeamCounts(type);
                    int inA = 0, inH = 0;
                    foreach (Battleground match in manager.RunningBattlegrounds.Where(b => b.Type == type))
                        foreach ((ObjectGuid _, Team team) in match.Participants())
                            if (team == Team.Alliance) inA++;
                            else inH++;
                    if (queuedA + inA > 1 || queuedH + inH > 1)
                        violations.Add(FormattableString.Invariant($"{type}: queued {queuedA}/{queuedH}, in matches {inA}/{inH} at {context.World.NowMs}"));
                    full &= inA == 1 && inH == 1;
                }

                return full;
            }

            await context.StepAsync("each type ends up with one bot a side in its match, never more", () => context.WaitUntilAsync("both matches hold one bot a side", Check, StartWait));
            await context.StepAsync("for another minute nobody else queues", async () =>
            {
                await context.World.AdvanceClockUntilAsync(60_000, () =>
                {
                    Check();
                    return false;
                });
                ScenarioContext.Expect(violations.Count == 0, "cap violations: " + string.Join("; ", violations));
                ScenarioContext.ExpectEqual(4, coordinator.Totals.Queued, "queues joined");
                ScenarioContext.ExpectEqual(2, bots.Count(b => !coordinator.Holds(b.BotId)), "bots left to their brains");
                foreach (BattlegroundType type in types)
                    ScenarioContext.ExpectEqual((0, 0), await context.ReadAsync(() => manager.QueuedTeamCounts(type)), $"{type} still queued");
            });
        });
    }

    [Fact]
    public async Task Invitation_IntoAMatchAtTheCap_IsDeclined()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync(o => o.Battlegrounds.MaxBotsPerBattleground = 2);
        PlayerbotBattlegroundOptions options = world.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ArcaneCore.World.Playerbots.PlayerbotOptions>>().Value.Battlegrounds;
        await RunAsync(world, "bg-decline", async context =>
        {
            ScenarioBot ally = await AutonomousAsync(context, "Bgdeclally", race: 1);
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            await context.StepAsync("the Alliance bot queues alone", () => context.WaitUntilAsync("queued",
                () => coordinator.Find(ally.BotId)?.Phase == PlayerbotBattlegroundPhase.Queued, TimeSpan.FromSeconds(60)));

            // The cap drops to zero (.reload config) while the bot waits: its invitation is into a match that holds the cap already.
            options.MaxBotsPerBattleground = 0;
            ScenarioBot horde = await ScriptedHordeQueuesAsync(context, "Bgdeclhorde", BattlegroundType.WarsongGulch, Content.HordeWsgMaster);
            await context.StepAsync("the invitation comes and the bot declines it (CMSG_BATTLEFIELD_PORT, action 0)", async () =>
            {
                await context.WaitUntilAsync("declined", () => coordinator.Totals.Declined == 1, TimeSpan.FromSeconds(60));
                ScenarioContext.Expect(!await context.ReadAsync(() => Manager(context).StateOf(ally.Guid).InAnyQueue), "still queued");
                ScenarioContext.Expect(await context.ReadAsync(() => context.Services.GetRequiredService<BattlegroundFeature>().BattlegroundOf(ally.Guid)) is null, "entered anyway");
                ScenarioContext.ExpectEqual(0, coordinator.Totals.Entered, "invitations taken");
            });
        });
    }

    // --- real players --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RealPlayer_BesideTheBattlemaster_IsNeverQueuedNorDriven()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync();
        await using WorldTestClient real = await world.EnterWorldAsync("BGREALONE", "Bgrealone", AccountSecurity.Player);
        ObjectGuid realGuid = await world.Host.OnWorldAsync(() => world.Host.World.OnlinePlayers.Single(p => p.Name == "Bgrealone").Guid);
        await RunAsync(world, "bg-real-player", async context =>
        {
            ScenarioBot ally = await AutonomousAsync(context, "Bgrealally", race: 1);
            ScenarioBot horde = await AutonomousAsync(context, "Bgrealhorde", race: 2);
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            (float x0, float y0) = await context.ReadAsync(() => context.World.FindOnlinePlayer(realGuid) is { } p ? (p.X, p.Y) : (0f, 0f));
            await context.StepAsync("the bots queue and enter; the real player beside the battlemaster does not", async () =>
            {
                await context.WaitUntilAsync("both bots are in the match", () => coordinator.Find(ally.BotId)?.Phase == PlayerbotBattlegroundPhase.InMatch
                    && coordinator.Find(horde.BotId)?.Phase == PlayerbotBattlegroundPhase.InMatch, TimeSpan.FromSeconds(90));
                BattlegroundPlayerState state = await context.ReadAsync(() => Manager(context).StateOf(realGuid));
                ScenarioContext.Expect(!state.InAnyQueue && !state.InBattleground, "the real player was queued");
                ScenarioContext.Expect(coordinator.Report().All(line => !line.Contains("Bgrealone", StringComparison.Ordinal)), "the coordinator follows the real player");
                (float x, float y) = await context.ReadAsync(() => context.World.FindOnlinePlayer(realGuid) is { } p ? (p.X, p.Y) : (float.NaN, float.NaN));
                ScenarioContext.Expect(x == x0 && y == y0, "the real player moved");
            });
        });
    }
}

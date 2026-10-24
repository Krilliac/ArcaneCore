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
/// Autonomous managed bots in battlegrounds (<see cref="PlayerbotBattlegroundCoordinator"/>, docs/areas/playbots-battlegrounds.md), end to
/// end on the real world handlers and the manual clock: the coordinator picks free bots, walks them to a battlemaster, queues them through
/// CMSG_BATTLEMASTER_HELLO / _JOIN, takes the invitation through CMSG_BATTLEFIELD_PORT, plays the objective (a Warsong Gulch capture, an
/// Arathi Basin banner opened with spell 21651) and leaves with CMSG_LEAVE_BATTLEFIELD. Opponents that must not fight back are scripted
/// managed bots (the coordinator never takes a scripted bot); real players are socket clients. Content: <see cref="BattlegroundBotTestContent"/>.
/// </summary>
public sealed class PlayerbotBattlegroundWorldTests
{
    private sealed class Scenario(string name, Func<ScenarioContext, Task> body) : IPlayerbotScenario
    {
        public string Name => name;

        public string Description => name;

        public Task RunAsync(ScenarioContext context) => body(context);
    }

    /// <summary>The start countdown is two minutes of game time; a wait for the gates gets three.</summary>
    internal static readonly TimeSpan StartWait = TimeSpan.FromMinutes(3);

    internal static async Task RunAsync(ScenarioTestWorld world, string name, Func<ScenarioContext, Task> body)
    {
        ScenarioReport report = await world.RunAsync(new Scenario(name, body), new ScenarioRunOptions
        {
            StepTimeout = TimeSpan.FromSeconds(60),
            MaxDuration = TimeSpan.FromMinutes(15),
        });
        Assert.True(report.Passed, report + Environment.NewLine + world.Trace());
    }

    internal static BattlegroundManager Manager(ScenarioContext context) => context.Services.GetRequiredService<BattlegroundFeature>().Manager;

    internal static PlayerbotBattlegroundCoordinator Coordinator(ScenarioContext context) => context.Services.GetRequiredService<PlayerbotBattlegroundCoordinator>();

    /// <summary>
    /// <see cref="ScenarioContext.WaitUntilAsync"/>, and on a timeout the facts of <paramref name="bots"/> (map, place, binding, the coordinator's
    /// view) in the failure: the scenario's own trace is taken after the run stopped every bot.
    /// </summary>
    internal static async Task WaitExplainedAsync(ScenarioContext context, string what, Func<bool> condition, TimeSpan timeout, params ScenarioBot[] bots)
    {
        try
        {
            await context.WaitUntilAsync(what, condition, timeout);
        }
        catch (ScenarioTimeoutException timedOut)
        {
            string facts = await context.ReadAsync(() => string.Join(" | ", bots.Select(b => b.Session?.Player is { } p
                ? FormattableString.Invariant($"{b.Name} map={p.MapId} inWorld={p.IsInWorld} alive={p.IsAlive} at=({p.X:F1},{p.Y:F1},{p.Z:F1}) bound={Manager(context).StateOf(p.Guid).InBattleground} queued={Manager(context).StateOf(p.Guid).InAnyQueue} {Coordinator(context).Describe(b.BotId)}")
                : $"{b.Name} offline")));
            throw new ScenarioAssertionException($"{timedOut.Message}; {facts}");
        }
    }

    /// <summary>An autonomous bot: logged in (scripted, so the run records its packets), then handed to its brain and the coordinators.</summary>
    internal static async Task<ScenarioBot> AutonomousAsync(ScenarioContext context, string name, byte race)
    {
        ScenarioBot bot = await context.LoginAsync(name, race, characterClass: 1);
        await context.AutonomousAsync(bot);
        return bot;
    }

    /// <summary>A scripted Horde opponent queues at its battlemaster for <paramref name="type"/> (it never fights; the coordinator never drives it).</summary>
    internal static async Task<ScenarioBot> ScriptedHordeQueuesAsync(ScenarioContext context, string name, BattlegroundType type, uint master)
    {
        ScenarioBot horde = await context.LoginAsync(name, race: 2, characterClass: 1);
        await context.StepAsync($"{name} queues for {type}", async () =>
        {
            ObjectGuid guid = ObjectGuid.WithEntry(HighGuid.Unit, master, master);
            System.Numerics.Vector3 at = Content.MasterAt(master);
            await context.PlaceAsync(horde, 0, at.X - 1.5f, at.Y, at.Z, 0f);
            long mark = horde.Mark();
            ScenarioContext.Expect(await horde.BattlemasterHelloAsync(guid), "hello refused");
            await horde.WaitForPacketAsync(WorldOpcode.SmsgBattlefieldList, p => p, since: mark);
            ScenarioContext.Expect(await horde.JoinBattlegroundAsync(guid, BattlegroundManager.MapOfType(type)), "join refused");
            await context.WaitUntilAsync($"{name} is queued", () => Manager(context).StateOf(horde.Guid).InAnyQueue);
        });
        return horde;
    }

    /// <summary>
    /// Flag a scripted player for PvP so the bots may attack it. A battleground's players are flagged by the zone rule (PvpAreaTracker: an
    /// unowned zone on a battleground map enforces PvP) but this fixture has no area table, so the player asks for PvP like a client does
    /// (CMSG_TOGGLE_PVP: it stays flagged, no timer). Only the tests that want a fight call it: a flagged Horde player parked out of the way would
    /// be hunted by the Alliance bots' duty plan.
    /// </summary>
    internal static Task FlagForPvpAsync(ScenarioBot bot) => bot.ReadAsync(p => { p.Map!.Combat.TogglePvp(p, true); return true; });

    /// <summary>The scripted bot takes its invitation and ports in, then is parked where it is out of everyone's way.</summary>
    internal static Task PortScriptedAsync(ScenarioContext context, ScenarioBot bot, uint map, (float X, float Y, float Z)? park)
        => context.StepAsync($"{bot.Name} ports in", async () =>
        {
            await context.WaitUntilAsync($"{bot.Name} is invited", () => Manager(context).StateOf(bot.Guid).InvitedInstance((BattlegroundQueueType)(byte)BattlegroundManager.TypeOfMap(map)) != 0, StartWait);
            ScenarioContext.Expect(await bot.PortBattlegroundAsync(map), "port refused");
            await context.WaitUntilAsync($"{bot.Name} is in the match", () => bot.Session!.Player is { IsInWorld: true } p && p.MapId == map
                && context.Services.GetRequiredService<BattlegroundFeature>().BattlegroundOf(p.Guid)?.PlayerTeam(p.Guid) is not null);
            if (park is { } at) await context.PlaceAsync(bot, map, at.X, at.Y, at.Z);
        });

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

    [Fact]
    public async Task RealPlayerWaiting_GetsABotForTheOtherTeam_ButIsNeverPortedByIt()
    {
        // No chance to queue at all: only a real player's waiting queue brings a bot (PreferFillingRealPlayerQueues).
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync(o => o.Battlegrounds.QueueChancePerMinute = 0f);
        await using WorldTestClient real = await world.EnterWorldAsync("BGREALTWO", "Bgrealtwo", AccountSecurity.Player);
        ObjectGuid realGuid = await world.Host.OnWorldAsync(() => world.Host.World.OnlinePlayers.Single(p => p.Name == "Bgrealtwo").Guid);
        var join = new PacketWriter(17);
        join.WriteUInt64(ObjectGuid.WithEntry(HighGuid.Unit, Content.AllianceWsgMaster, Content.AllianceWsgMaster).Value);
        join.WriteUInt32(489);
        join.WriteUInt32(0);
        join.WriteByte(0);
        await real.SendAsync(WorldOpcode.CmsgBattlemasterJoin, join.ToArray());
        await RunAsync(world, "bg-real-waiting", async context =>
        {
            await context.StepAsync("the real player waits in the Warsong Gulch queue", () => context.WaitUntilAsync("queued",
                () => Manager(context).QueuedGroupOf(realGuid, BattlegroundType.WarsongGulch) is not null));
            ScenarioBot horde = await AutonomousAsync(context, "Bgfillhorde", race: 2);
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            await context.StepAsync("a Horde bot queues to fill the real player's game and enters it", () => context.WaitUntilAsync("the bot is in the match",
                () => coordinator.Find(horde.BotId)?.Phase == PlayerbotBattlegroundPhase.InMatch, TimeSpan.FromSeconds(90)));
            await context.StepAsync("the real player is invited, and only it decides: it is not ported by the coordinator", async () =>
            {
                await context.IdleAsync(TimeSpan.FromSeconds(20));
                BattlegroundPlayerState state = await context.ReadAsync(() => Manager(context).StateOf(realGuid));
                ScenarioContext.Expect(state.InvitedInstance(BattlegroundQueueType.WarsongGulch) != 0, "the real player has no invitation");
                ScenarioContext.Expect(!state.InBattleground, "the real player was ported in");
                ScenarioContext.ExpectEqual(1, coordinator.Totals.Entered, "invitations the coordinator took");
            });
        });
    }

    [Fact]
    public async Task ManagedBotWaiting_IsNoRealPlayer_ItsQueueBringsNoBot()
    {
        // No chance to queue: only a real player's waiting queue brings a bot. A scripted managed bot waiting is no real player.
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync(o => o.Battlegrounds.QueueChancePerMinute = 0f);
        await RunAsync(world, "bg-managed-waiting", async context =>
        {
            ScenarioBot horde = await AutonomousAsync(context, "Bgnofilhorde", race: 2);
            ScenarioBot ally = await context.LoginAsync("Bgnofillally", race: 1, characterClass: 1);
            await context.StepAsync("the scripted Alliance bot waits in the Warsong Gulch queue", async () =>
            {
                System.Numerics.Vector3 at = Content.MasterAt(Content.AllianceWsgMaster);
                await context.PlaceAsync(ally, 0, at.X - 1.5f, at.Y, at.Z, 0f);
                ScenarioContext.Expect(await ally.JoinBattlegroundAsync(ObjectGuid.WithEntry(HighGuid.Unit, Content.AllianceWsgMaster, Content.AllianceWsgMaster), 489), "join refused");
                await context.WaitUntilAsync("queued", () => Manager(context).QueuedGroupOf(ally.Guid, BattlegroundType.WarsongGulch) is not null);
            });
            await context.StepAsync("no Horde bot comes to fill it", async () =>
            {
                await context.IdleAsync(TimeSpan.FromSeconds(30));
                ScenarioContext.ExpectEqual(0, Coordinator(context).Totals.Queued, "queues joined");
                ScenarioContext.Expect(!await context.ReadAsync(() => Manager(context).StateOf(horde.Guid).InAnyQueue), "the Horde bot queued");
            });
        });
    }

    // --- a real player's party ---------------------------------------------------------------------------------------

    /// <summary>The real player <paramref name="inviter"/> puts <paramref name="bot"/> into its party (world thread): <c>.playerbot invite</c>.</summary>
    private static bool InviteIntoParty(ScenarioContext context, ObjectGuid inviter, ScenarioBot bot)
        => context.Services.GetRequiredService<ManagedPlayerbotFeature>().InviteToGroup(context.World.FindOnlinePlayer(inviter)!, bot.Name).Success;

    [Fact]
    public async Task PartyBot_OfARealPlayer_BesideTheBattlemaster_NeverQueues()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync();
        await using WorldTestClient real = await world.EnterWorldAsync("BGPARTYONE", "Bgpartyone", AccountSecurity.Player);
        ObjectGuid realGuid = await world.Host.OnWorldAsync(() => world.Host.World.OnlinePlayers.Single(p => p.Name == "Bgpartyone").Guid);
        await RunAsync(world, "bg-party-bot", async context =>
        {
            // No queue chance while the bot settles; then, between two ticks, it is invited into the real player's party, the chance goes to
            // 100% and the next tick scans: the scan runs before the bot's party AI notices its master (its think comes later in the tick), so
            // only the coordinator's own group check keeps it out (MayQueue refuses it at the scan, Taken would release it from any later phase).
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            coordinator.Options.QueueChancePerMinute = 0f;
            ScenarioBot bot = await AutonomousAsync(context, "Bgpartybot", race: 1);
            await context.IdleAsync(TimeSpan.FromSeconds(3));
            bool invited = false;
            await context.StepAsync("the real player takes the bot into its party right before a scan", () => context.ReadAsync(() =>
            {
                invited = InviteIntoParty(context, realGuid, bot);
                coordinator.Options.QueueChancePerMinute = 1f;
                coordinator.ScanAtNextTick();
                return true;
            }));
            ScenarioContext.Expect(invited, "the invitation failed");
            await context.StepAsync("at a 100% chance beside the battlemaster it never queues", async () =>
            {
                bool taken = false;
                await context.World.AdvanceClockUntilAsync(30_000, () =>
                {
                    taken |= coordinator.Holds(bot.BotId);
                    return false;
                });
                ScenarioContext.Expect(!taken, "the coordinator took the party bot");
                ScenarioContext.ExpectEqual(0, coordinator.Totals.Queued, "queues joined");
                ScenarioContext.Expect(!await context.ReadAsync(() => Manager(context).StateOf(bot.Guid).InAnyQueue), "the party bot is queued");
            });
        });
    }

    [Fact]
    public async Task QueuedBot_ThatJoinsARealPlayersParty_LeavesTheQueue_AndIsNeverPorted()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync();
        await using WorldTestClient real = await world.EnterWorldAsync("BGPARTYTWO", "Bgpartytwo", AccountSecurity.Player);
        ObjectGuid realGuid = await world.Host.OnWorldAsync(() => world.Host.World.OnlinePlayers.Single(p => p.Name == "Bgpartytwo").Guid);
        await RunAsync(world, "bg-party-race", async context =>
        {
            // A Horde opponent waits first, so the Alliance bot is invited as soon as it is queued.
            ScenarioBot horde = await ScriptedHordeQueuesAsync(context, "Bgracehorde", BattlegroundType.WarsongGulch, Content.HordeWsgMaster);
            ScenarioBot ally = await AutonomousAsync(context, "Bgraceally", race: 1);
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            bool invitedIntoParty = false;
            string? setup = null;
            await context.StepAsync("in the tick its invitation arrives, the queued bot joins the real player's party", () => context.WaitUntilAsync("the party invitation",
                () =>
                {
                    PlayerbotBattlegroundCoordinator.Tracked? t = coordinator.Find(ally.BotId);
                    if (t?.Phase is PlayerbotBattlegroundPhase.Entering or PlayerbotBattlegroundPhase.InMatch)
                    {
                        setup = "the bot ported in the tick its invitation arrived: no window to join a party first";
                        return true;
                    }

                    if (t?.Phase != PlayerbotBattlegroundPhase.Queued
                        || Manager(context).StateOf(ally.Guid).InvitedInstance(BattlegroundQueueType.WarsongGulch) == 0) return false;
                    // Accepted now; the bot's party AI notices only at its next think, after the coordinator's tick.
                    invitedIntoParty = InviteIntoParty(context, realGuid, ally);
                    return true;
                }, TimeSpan.FromSeconds(60)));
            ScenarioContext.Expect(setup is null, setup ?? string.Empty);
            ScenarioContext.Expect(invitedIntoParty, "the party invitation failed");
            await context.StepAsync("it declines the invitation (CMSG_BATTLEFIELD_PORT action 0) and is never ported", async () =>
            {
                bool ported = false;
                await context.World.AdvanceClockUntilAsync(20_000, () =>
                {
                    ported |= Manager(context).StateOf(ally.Guid).InBattleground;
                    return false;
                });
                ScenarioContext.Expect(!ported, "the party bot was ported into the match");
                ScenarioContext.ExpectEqual(0, coordinator.Totals.Entered, "invitations taken");
                ScenarioContext.Expect(!await context.ReadAsync(() => Manager(context).StateOf(ally.Guid).InAnyQueue), "still queued");
                ScenarioContext.Expect(!coordinator.Holds(ally.BotId), "the coordinator still holds the party bot");
            });
        });
    }

    [Fact]
    public async Task BotWithBattlegroundBusiness_LeashedByFollow_IsLetGo_AndStaysLeashed()
    {
        // .playerbot follow (ManagedPlayerbotFeature.Leash) on a bot the coordinator drives: the coordinator's drive comes first at each think,
        // so the leash holds only because the leashed bot is no longer free and the coordinator's update (before the think) releases it.
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync();
        await using WorldTestClient real = await world.EnterWorldAsync("BGLEASH", "Bgleashgm", AccountSecurity.Player);
        ObjectGuid realGuid = await world.Host.OnWorldAsync(() => world.Host.World.OnlinePlayers.Single(p => p.Name == "Bgleashgm").Guid);
        await RunAsync(world, "bg-leash", async context =>
        {
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            ScenarioBot bot = await AutonomousAsync(context, "Bgleashbot", race: 1);
            await context.StepAsync("the coordinator drives the bot to its battlemaster", () => context.WaitUntilAsync("driven",
                () => coordinator.Drives(bot.BotId), TimeSpan.FromSeconds(30)));
            PlayerbotOperationResult leashed = await context.ReadAsync(() => context.Services.GetRequiredService<ManagedPlayerbotFeature>()
                .Leash(bot.BotId, context.World.FindOnlinePlayer(realGuid)!, stay: false));
            ScenarioContext.Expect(leashed.Success, $"the leash was refused ({leashed.Code})");
            await context.StepAsync("for ten seconds the coordinator holds nothing of the bot and it never goes for a battleground", async () =>
            {
                bool held = false;
                await context.World.AdvanceClockUntilAsync(10_000, () =>
                {
                    held |= coordinator.Holds(bot.BotId);
                    return false;
                });
                ScenarioContext.Expect(!held, "the coordinator took the leashed bot back");
                PlayerbotStatus? status = await context.ReadAsync(() => context.Services.GetRequiredService<ManagedPlayerbotFeature>().Snapshot().SingleOrDefault(s => s.BotId == bot.BotId));
                ScenarioContext.Expect(status is { } s && s.Goal != PlayerbotGoalKind.Battleground && s.Risk != "decision=battleground",
                    $"the leashed bot still reports battleground business ({status?.Goal} {status?.Risk})");
            });
        });
    }

    // --- the objectives ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Wsg_AutonomousBot_TakesTheEnemyFlag_AndCapturesItAtItsOwnFlagRoom()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync();
        await RunAsync(world, "bg-wsg-capture", async context =>
        {
            ScenarioBot ally = await AutonomousAsync(context, "Bgcapally", race: 1);
            ScenarioBot horde = await ScriptedHordeQueuesAsync(context, "Bgcaphorde", BattlegroundType.WarsongGulch, Content.HordeWsgMaster);
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            await PortScriptedAsync(context, horde, 489, (Content.HordeParking.X, Content.HordeParking.Y, Content.HordeParking.Z));
            await context.StepAsync("the Alliance bot is in the match", () => context.WaitUntilAsync("in the match",
                () => coordinator.Find(ally.BotId)?.Phase == PlayerbotBattlegroundPhase.InMatch, TimeSpan.FromSeconds(60)));
            var wsg = (WarsongGulch)(await context.BattlegroundOfAsync(ally))!;
            await context.StepAsync("the gates open", () => context.WaitUntilAsync("in progress", () => wsg.Status == BattlegroundStatus.InProgress, StartWait));

            bool carried = false;
            long mark = ally.Mark();
            await context.StepAsync("the bot takes the Horde flag from its stand (CMSG_GAMEOBJ_USE) and carries it", () => context.WaitUntilAsync("carrying",
                () => carried = wsg.FlagPicker(Team.Horde) == ally.Guid, TimeSpan.FromSeconds(60)));
            await context.StepAsync("the bot walks into its own flag room (trigger 3646) and captures", async () =>
            {
                await context.WaitUntilAsync("captured", () => wsg.TeamScore(Team.Alliance) == 1, TimeSpan.FromSeconds(60));
                ScenarioContext.Expect(carried, "the flag was never carried");
                ScenarioContext.ExpectEqual(1u, await context.ReadAsync(() => ((WsgScore)wsg.ScoreOf(ally.Guid)!).FlagCaptures), "the bot's flag captures");
                (uint field, uint value) = await ally.WaitForPacketAsync(WorldOpcode.SmsgUpdateWorldState, ScenarioBattlegrounds.WorldState,
                    s => s.Field == WarsongGulch.WorldStateFlagCapturesAlliance, mark);
                ScenarioContext.ExpectEqual(1u, value, $"world state {field}");
            });
        });
    }

    [Fact]
    public async Task Ab_AutonomousBot_ClaimsTheStables()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync(null, BattlegroundType.ArathiBasin);
        await RunAsync(world, "bg-ab-stables", async context =>
        {
            ScenarioBot ally = await AutonomousAsync(context, "Bgabally", race: 1);
            ScenarioBot horde = await ScriptedHordeQueuesAsync(context, "Bgabhorde", BattlegroundType.ArathiBasin, Content.HordeAbMaster);
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            await PortScriptedAsync(context, horde, 529, park: null);
            await context.StepAsync("the Alliance bot is in the match", () => context.WaitUntilAsync("in the match",
                () => coordinator.Find(ally.BotId)?.Phase == PlayerbotBattlegroundPhase.InMatch, TimeSpan.FromSeconds(60)));
            var ab = (ArathiBasin)(await context.BattlegroundOfAsync(ally))!;
            await context.StepAsync("the gates open", () => context.WaitUntilAsync("in progress", () => ab.Status == BattlegroundStatus.InProgress, StartWait));

            long mark = ally.Mark();
            await context.StepAsync("the bot walks to the stables and casts 21651 at the neutral banner: the stables are claimed", async () =>
            {
                try
                {
                    await context.WaitUntilAsync("claimed", () => ab.NodeStatus(ArathiBasin.NodeStables) == ArathiBasin.StatusAllianceContested, TimeSpan.FromSeconds(90));
                }
                catch (ScenarioTimeoutException timedOut)
                {
                    string facts = await context.ReadAsync(() =>
                    {
                        Player p = ally.RequirePlayer();
                        var objects = p.Map!.FindUpdater<Game.GameObjects.GameObjectMapSystem>()!;
                        string banners = string.Join(",", objects.GameObjects.Where(g => g.Spawn is not null && g.Map == p.Map && g.IsSpawned)
                            .Select(g => FormattableString.Invariant($"{g.Entry}/{g.Spawn!.Guid}@{MathF.Sqrt(MathF.Pow(g.X - p.X, 2) + MathF.Pow(g.Y - p.Y, 2) + MathF.Pow(g.Z - p.Z, 2)):F1}")));
                        return FormattableString.Invariant($"status={ab.NodeStatus(ArathiBasin.NodeStables)} casts={coordinator.Find(ally.BotId)?.AI?.BannerCasts} activity={coordinator.Find(ally.BotId)?.AI?.Activity} at=({p.X:F1},{p.Y:F1},{p.Z:F1}) banners={banners} spellGo={ally.Received(WorldOpcode.SmsgSpellGo, SpellIdOf, mark).Count(i => i == PlayerbotBattlegroundAI.BannerSpell)}");
                    });
                    throw new ScenarioAssertionException(timedOut.Message + "; " + facts);
                }
                // The claim came from the bot's own cast: SMSG_SPELL_START and SMSG_SPELL_GO of 21651 reached it, and the match credits it.
                ScenarioContext.Expect(ally.Received(WorldOpcode.SmsgSpellGo, SpellIdOf, mark).Contains(PlayerbotBattlegroundAI.BannerSpell), "no SMSG_SPELL_GO of 21651");
                ScenarioContext.Expect(ally.Received(WorldOpcode.SmsgSpellStart, SpellIdOf, mark).Contains(PlayerbotBattlegroundAI.BannerSpell), "no SMSG_SPELL_START of 21651");
                ScenarioContext.ExpectEqual(1u, await context.ReadAsync(() => ((AbScore)ab.ScoreOf(ally.Guid)!).BasesAssaulted), "the bot's bases assaulted");
                ScenarioContext.Expect(coordinator.Find(ally.BotId)?.AI?.BannerCasts >= 1, "the AI submitted no banner cast");
            });
        });
    }

    [Fact]
    public async Task Ab_WithTheRealGooberNeutralBanner_AutonomousBotClaimsTheStables_ThroughTheTriggerSpell()
    {
        // The real data's neutral banner (classic-db 180087: a goober locked by 1479 whose data10 is 23932 "A-Mid Trigger") is routed
        // (MatchRuntime.RoutesBannerUse), so the coordinator accepts Arathi Basin. A direct use of it is refused (the goober's use runs only
        // through the opening spell), so the bot must cast Opening 21651 at it: the open-lock reaches the goober, whose use casts 23932 at the
        // bot, whose script makes the node click (MatchRuntime.OnBannerTriggered).
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync(null, NeutralBannerShape.RealGoober, BattlegroundType.ArathiBasin);
        await RunAsync(world, "bg-ab-goober", async context =>
        {
            ScenarioBot ally = await AutonomousAsync(context, "Bggooberally", race: 1);
            ScenarioBot horde = await ScriptedHordeQueuesAsync(context, "Bggoobhorde", BattlegroundType.ArathiBasin, Content.HordeAbMaster);
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            await PortScriptedAsync(context, horde, 529, park: null);
            await context.StepAsync("Arathi Basin is accepted: the Alliance bot queues and is in the match", async () =>
            {
                await WaitExplainedAsync(context, "in the match", () => coordinator.Find(ally.BotId)?.Phase == PlayerbotBattlegroundPhase.InMatch,
                    TimeSpan.FromSeconds(60), ally);
                ScenarioContext.Expect(!coordinator.Refusals.Any(r => r.Type == BattlegroundType.ArathiBasin), "Arathi Basin was refused");
            });
            var ab = (ArathiBasin)(await context.BattlegroundOfAsync(ally))!;
            await context.StepAsync("the gates open", () => context.WaitUntilAsync("in progress", () => ab.Status == BattlegroundStatus.InProgress, StartWait));

            long mark = ally.Mark();
            await context.StepAsync("the bot casts 21651 at the neutral goober banner, which casts 23932 at it: the stables are claimed", async () =>
            {
                await WaitExplainedAsync(context, "claimed", () => ab.NodeStatus(ArathiBasin.NodeStables) == ArathiBasin.StatusAllianceContested,
                    TimeSpan.FromSeconds(90), ally);
                ScenarioContext.Expect(ally.Received(WorldOpcode.SmsgSpellStart, SpellIdOf, mark).Contains(PlayerbotBattlegroundAI.BannerSpell), "no SMSG_SPELL_START of 21651");
                ScenarioContext.Expect(ally.Received(WorldOpcode.SmsgSpellGo, SpellIdOf, mark).Contains(PlayerbotBattlegroundAI.BannerSpell), "no SMSG_SPELL_GO of 21651");
                ScenarioContext.Expect(ally.Received(WorldOpcode.SmsgSpellGo, SpellIdOf, mark).Contains(Content.StablesTrigger), "no SMSG_SPELL_GO of the trigger spell 23932");
                ScenarioContext.Expect(coordinator.Find(ally.BotId)?.AI?.BannerCasts >= 1, "the AI submitted no banner cast");
                ScenarioContext.ExpectEqual(1u, await context.ReadAsync(() => ((AbScore)ab.ScoreOf(ally.Guid)!).BasesAssaulted), "the bot's bases assaulted");
            });
        });
    }

    [Fact]
    public async Task Ab_WithANeutralGooberWhoseSpellIsNoBannerTrigger_IsRefused_NoBotQueues()
    {
        // A goober banner whose spell (data10) is not a banner trigger spell is not routed to the node click: a 21651 cast at it would claim
        // nothing, so Arathi Basin is refused (MissingContent) and no bot queues, though a battlemaster stands beside it.
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync(null, NeutralBannerShape.UnroutedGoober, BattlegroundType.ArathiBasin);
        await RunAsync(world, "bg-ab-unrouted", async context =>
        {
            ScenarioBot ally = await AutonomousAsync(context, "Bgunrouteda", race: 1);
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            await context.StepAsync("Arathi Basin is refused for missing content and nothing queues", async () =>
            {
                await context.WaitUntilAsync("the refusal is noted", () => coordinator.Refusals.Contains((BattlegroundType.ArathiBasin, PlayerbotBattlegroundRefusal.MissingContent)),
                    TimeSpan.FromSeconds(10));
                await context.IdleAsync(TimeSpan.FromSeconds(30));
                ScenarioContext.ExpectEqual(0, coordinator.Totals.Queued, "queues joined");
                ScenarioContext.Expect(!coordinator.Holds(ally.BotId), "the bot went for a battleground");
                ScenarioContext.ExpectEqual((0, 0), await context.ReadAsync(() => Manager(context).QueuedTeamCounts(BattlegroundType.ArathiBasin)), "Arathi Basin queue");
            });
        });
    }

    /// <summary>SMSG_SPELL_START / SMSG_SPELL_GO: packed caster item guid, packed caster guid, u32 spell.</summary>
    private static uint SpellIdOf(byte[] payload)
    {
        var reader = new PacketReader(payload);
        _ = reader.ReadPackedGuid();
        _ = reader.ReadPackedGuid();
        return reader.ReadUInt32();
    }

    // --- Alterac Valley ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Av_TwoAutonomousBots_Queue_PortIntoOneMatch_AndPlayTheObjective()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync(null, BattlegroundType.AlteracValley);
        await RunAsync(world, "bg-av-flow", async context =>
        {
            ScenarioBot ally = await AutonomousAsync(context, "Bgavally", race: 1);
            ScenarioBot horde = await AutonomousAsync(context, "Bgavhorde", race: 2);
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            BattlegroundFeature feature = context.Services.GetRequiredService<BattlegroundFeature>();

            Battleground match = await context.StepAsync("both bots queue for Alterac Valley and port into the match", async () =>
            {
                await WaitExplainedAsync(context, "both bots are in the Alterac Valley match",
                    () => coordinator.Find(ally.BotId)?.Phase == PlayerbotBattlegroundPhase.InMatch
                        && coordinator.Find(horde.BotId)?.Phase == PlayerbotBattlegroundPhase.InMatch,
                    TimeSpan.FromSeconds(60), ally, horde);
                ScenarioContext.Expect(!coordinator.Refusals.Any(r => r.Type == BattlegroundType.AlteracValley), "Alterac Valley was refused");
                Battleground? a = await context.ReadAsync(() => feature.BattlegroundOf(ally.Guid));
                Battleground? h = await context.ReadAsync(() => feature.BattlegroundOf(horde.Guid));
                ScenarioContext.Expect(a is AlteracValley && ReferenceEquals(a, h), "the bots are not in one Alterac Valley match");
                coordinator.Options.QueueChancePerMinute = 0f;
                return a!;
            });

            await context.StepAsync("the gates open and the bots advance in Alterac Valley", async () =>
            {
                await context.WaitUntilAsync("the match is in progress", () => match.Status == BattlegroundStatus.InProgress, StartWait);
                await WaitExplainedAsync(context, "the Alliance bot advances or fights in Alterac Valley",
                    () => coordinator.Find(ally.BotId)?.AI?.Activity is "av-advance" or "av-assault" or "av-claim-banner" or "fighting",
                    TimeSpan.FromSeconds(30), ally, horde);
            });
        });
    }

    /// <summary>
    /// A health pool no test guard can empty. The guard fight is melee with the real hit table (<see cref="SharedCombatRandom"/>) and a thin
    /// margin: the bot (60 health) walks to the guard (20 health, no melee damage in the fixture, so the combat fallback of 0 to 5 per swing
    /// every 2 s) and kills it in 28 to 40 s of game time while losing 24 to 54 health, over 20 runs of an instrumented copy of the test (it
    /// won all 20, the closest with 6 health left; the bot closed in, kept swinging and never dithered). A bot that loses the tail of that
    /// distribution dies at the start cave and the wait for the guard's death runs out its game budget at once, which is what one CI run
    /// showed; what attacked it there was not observed, and the CI trace's second dead bot (the parked Horde one) is not explained. The
    /// protection hides a stray attacker, so <see cref="ExpectOnlyTheGuardHurtTheBot"/> fails the test if anything but the guard hit the bot.
    /// </summary>
    private static Task FortifyAsync(ScenarioContext context, ScenarioBot bot) => context.ReadAsync(() =>
    {
        Player player = bot.RequirePlayer();
        player.MaxHealth = 5000;
        player.Health = 5000;
        return true;
    });

    /// <summary>Every melee hit the bot took since <paramref name="since"/> came from the guard: no stray hostile in the test world.</summary>
    private static void ExpectOnlyTheGuardHurtTheBot(ScenarioBot bot, Game.Creatures.Creature guard, long since)
    {
        ulong[] attackers = bot.Received(WorldOpcode.SmsgAttackerstateupdate, ScenarioDecoders.AttackerState, since)
            .Where(h => h.Victim == bot.Guid.Value).Select(h => h.Attacker).Distinct().ToArray();
        ScenarioContext.Expect(attackers.All(a => a == guard.Guid.Value),
            $"something other than the guard hit the bot (attacker guids: {string.Join(", ", attackers.Select(a => a.ToString("X")))})");
    }

    /// <summary>
    /// That protection is checked against a guard far stronger than the fixture's: hitting for 90 to 100 it empties an unprotected bot in one
    /// swing, yet the protected bot still kills it within the 90 s the test allows (the kill took 14 to 20 of the guard's 2 s swings: about
    /// 2000 damage, 4000 if every one were a critical hit, against 5000 health), stays alive and the guard dies.
    /// </summary>
    [Fact]
    public async Task Av_AutonomousBot_KillsTheGuard_WithoutDying_EvenWhenTheGuardHitsFarHarderThanTheFixture()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync(null, BattlegroundType.AlteracValley);
        await RunAsync(world, "bg-av-guard-duel", async context =>
        {
            ScenarioBot ally = await AutonomousAsync(context, "Bgavduel", race: 1);
            ScenarioBot horde = await ScriptedHordeQueuesAsync(context, "Bgavduelh", BattlegroundType.AlteracValley, Content.HordeAvMaster);
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            await PortScriptedAsync(context, horde, 30, (-100f, 300f, Content.AvFloor));
            await context.StepAsync("the Alliance bot is in the match", () => context.WaitUntilAsync("in the match",
                () => coordinator.Find(ally.BotId)?.Phase == PlayerbotBattlegroundPhase.InMatch, TimeSpan.FromSeconds(60)));
            var av = (AlteracValley)(await context.BattlegroundOfAsync(ally))!;
            coordinator.Options.QueueChancePerMinute = 0f;
            long since = ally.Mark();
            await FortifyAsync(context, ally);
            await context.StepAsync("the gates open", () => context.WaitUntilAsync("in progress", () => av.Status == BattlegroundStatus.InProgress, StartWait));

            Game.Creatures.Creature guard = await context.ReadAsync(() =>
            {
                Game.Creatures.Creature found = ally.RequirePlayer().Map!.FindUpdater<Game.Creatures.CreatureMapSystem>()!
                    .Creatures.Single(c => c.Entry == Content.AvGuardEntry);
                found.SetFloat(UpdateFields.UnitFieldMindamage, 90f);
                found.SetFloat(UpdateFields.UnitFieldMaxdamage, 100f);
                return found;
            });
            await context.StepAsync("the bot kills the hard-hitting guard and lives", async () =>
            {
                await WaitExplainedAsync(context, "the guard is dead", () => !guard.IsAlive, TimeSpan.FromSeconds(90), ally);
                ScenarioContext.Expect(await context.ReadAsync(() => ally.RequirePlayer().IsAlive), "the bot died in the guard fight");
                ScenarioContext.ExpectEqual(5000u, await context.ReadAsync(() => ally.RequirePlayer().MaxHealth), "the bot's max health was reset after the fortification");
                ExpectOnlyTheGuardHurtTheBot(ally, guard, since);
            });
        });
    }

    /// <summary>
    /// The Alterac Valley objective flow through the real paths: the Alliance bot leaves its start cave for the Horde-held Iceblood graveyard
    /// (node 4, the first actionable step of its lane), kills the hostile guard before the banner, casts 21651 at the Horde banner (178364) and
    /// the match turns the opened banner into the assault (node 4 Alliance-owned and assaulted, its graveyards-assaulted score 1). Killed
    /// there, it is released to its team's nearest held graveyard (the start cave: Iceblood is only assaulted) and waits within the spirit
    /// guide's wave for the resurrection instead of running back to its body 60 yards away.
    /// </summary>
    [Fact]
    public async Task Av_AutonomousBot_KillsTheGuard_AssaultsTheIcebloodGraveyard_AndWhenKilledWaitsForTheSpiritGuide()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync(null, BattlegroundType.AlteracValley);
        await RunAsync(world, "bg-av-assault", async context =>
        {
            ScenarioBot ally = await AutonomousAsync(context, "Bgavassa", race: 1);
            ScenarioBot horde = await ScriptedHordeQueuesAsync(context, "Bgavassh", BattlegroundType.AlteracValley, Content.HordeAvMaster);
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            await PortScriptedAsync(context, horde, 30, (-100f, 300f, Content.AvFloor));
            await context.StepAsync("the Alliance bot is in the match", () => context.WaitUntilAsync("in the match",
                () => coordinator.Find(ally.BotId)?.Phase == PlayerbotBattlegroundPhase.InMatch, TimeSpan.FromSeconds(60)));
            var av = (AlteracValley)(await context.BattlegroundOfAsync(ally))!;
            coordinator.Options.QueueChancePerMinute = 0f;
            long since = ally.Mark();
            await FortifyAsync(context, ally);
            await context.StepAsync("the gates open", () => context.WaitUntilAsync("in progress", () => av.Status == BattlegroundStatus.InProgress, StartWait));

            Game.Creatures.Creature guard = await context.ReadAsync(() => ally.RequirePlayer().Map!.FindUpdater<Game.Creatures.CreatureMapSystem>()!
                .Creatures.Single(c => c.Entry == Content.AvGuardEntry));
            await context.StepAsync("the bot fights and kills the hostile guard on its way", () => WaitExplainedAsync(context, "the guard is dead",
                () => !guard.IsAlive, TimeSpan.FromSeconds(90), ally));
            ExpectOnlyTheGuardHurtTheBot(ally, guard, since);

            long mark = ally.Mark();
            await context.StepAsync("the bot casts 21651 at the Horde banner and Iceblood is assaulted by the Alliance", async () =>
            {
                await WaitExplainedAsync(context, "Iceblood assaulted",
                    () => av.NodeOwner(AlteracValley.NodeIcebloodGrave) == 0 && av.NodeState(AlteracValley.NodeIcebloodGrave) == AlteracValley.PointAssaulted,
                    TimeSpan.FromSeconds(90), ally);
                ScenarioContext.ExpectEqual(1, await context.ReadAsync(() => av.NodeTotalOwner(AlteracValley.NodeIcebloodGrave)), "the total owner (Horde until taken)");
                ScenarioContext.Expect(ally.Received(WorldOpcode.SmsgSpellGo, SpellIdOf, mark).Contains(PlayerbotBattlegroundAI.BannerSpell), "no SMSG_SPELL_GO of 21651");
                ScenarioContext.ExpectEqual(1u, await context.ReadAsync(() => ((AvScore)av.ScoreOf(ally.Guid)!).GraveyardsAssaulted), "the bot's graveyards assaulted");
                ScenarioContext.Expect(coordinator.Find(ally.BotId)?.AI?.BannerCasts >= 1, "the AI submitted no banner cast");
            });

            System.Numerics.Vector3 body = default;
            await context.StepAsync("killed at the graveyard, the bot waits at the spirit guide and never runs to its body", async () =>
            {
                body = await context.ReadAsync(() =>
                {
                    Player p = ally.RequirePlayer();
                    p.Health = 0;
                    p.Map!.Combat.KillPlayer(p);
                    return new System.Numerics.Vector3(p.X, p.Y, p.Z);
                });
                await WaitExplainedAsync(context, "the ghost is at the spirit guide", () => coordinator.Find(ally.BotId)?.AI?.Activity == "spirit-guide",
                    TimeSpan.FromSeconds(60), ally);
                float closestToBody = float.MaxValue, farthestFromGuide = 0f;
                for (int second = 0; second < 40; second++)
                {
                    await context.IdleAsync(TimeSpan.FromSeconds(1));
                    (bool ghost, System.Numerics.Vector3 at) = await context.ReadAsync(() =>
                    {
                        Player p = ally.RequirePlayer();
                        return ((p.Flags & PlayerFlags.Ghost) != 0, new System.Numerics.Vector3(p.X, p.Y, p.Z));
                    });
                    ScenarioContext.Expect(ghost, $"the bot is no ghost after {second} s (it reclaimed its body)");
                    closestToBody = MathF.Min(closestToBody, System.Numerics.Vector3.Distance(at, body));
                    farthestFromGuide = MathF.Max(farthestFromGuide, System.Numerics.Vector3.Distance(at, Content.AvSpiritGuideAt));
                }

                ScenarioContext.Expect(farthestFromGuide <= 20f, $"the ghost left the spirit guide's wave ({farthestFromGuide:F1} yd)");
                ScenarioContext.Expect(closestToBody >= 30f, $"the ghost ran towards its body ({closestToBody:F1} yd)");
                ScenarioContext.ExpectEqual("spirit-guide", coordinator.Find(ally.BotId)?.AI?.Activity, "activity");
            });
        });
    }
}

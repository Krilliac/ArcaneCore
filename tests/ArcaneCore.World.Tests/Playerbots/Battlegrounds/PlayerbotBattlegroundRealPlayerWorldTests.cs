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
/// Real players and party bots beside the battlemaster, and a Warsong Gulch capture. Split from the former <c>PlayerbotBattlegroundWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotBattlegroundWorldTests"/>.
/// </summary>
public sealed class PlayerbotBattlegroundRealPlayerWorldTests : PlayerbotBattlegroundWorldTests
{
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
}

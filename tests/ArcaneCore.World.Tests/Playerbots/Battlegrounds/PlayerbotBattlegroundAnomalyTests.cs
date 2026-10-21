using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Diagnostics;
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

namespace ArcaneCore.World.Tests.Playerbots.Battlegrounds;

/// <summary>
/// H1 hardening: the battleground bot's anomaly reports (<c>bot-bg/objective-unreachable</c>, <c>idle-in-match</c>, <c>invite-not-accepted</c>,
/// <c>queue-stuck</c>). End to end in the battleground scenario world of <see cref="PlayerbotBattlegroundWorldTests"/> (real handlers, manual
/// clock, managed bots); each key fires on a cause the world can be put in, and the healthy flows of that suite stay silent.
/// </summary>
public sealed class PlayerbotBattlegroundAnomalyTests
{
    private static readonly string ObjectiveUnreachable = PlayerbotAnomalies.Key("bg", "objective-unreachable");
    private static readonly string IdleInMatch = PlayerbotAnomalies.Key("bg", "idle-in-match");
    private static readonly string InviteNotAccepted = PlayerbotAnomalies.Key("bg", "invite-not-accepted");
    private static readonly string QueueStuck = PlayerbotAnomalies.Key("bg", "queue-stuck");

    private static readonly TimeSpan StartWait = TimeSpan.FromMinutes(3);

    private sealed class Scenario(string name, Func<ScenarioContext, Task> body) : IPlayerbotScenario
    {
        public string Name => name;

        public string Description => name;

        public Task RunAsync(ScenarioContext context) => body(context);
    }

    private static async Task RunAsync(ScenarioTestWorld world, string name, Func<ScenarioContext, Task> body)
    {
        ScenarioReport report = await world.RunAsync(new Scenario(name, body), new ScenarioRunOptions
        {
            StepTimeout = TimeSpan.FromSeconds(60),
            MaxDuration = TimeSpan.FromMinutes(15),
        });
        Assert.True(report.Passed, report + Environment.NewLine + world.Trace());
    }

    private static BattlegroundManager Manager(ScenarioContext context) => context.Services.GetRequiredService<BattlegroundFeature>().Manager;

    private static PlayerbotBattlegroundCoordinator Coordinator(ScenarioContext context) => context.Services.GetRequiredService<PlayerbotBattlegroundCoordinator>();

    private static async Task<ScenarioBot> AutonomousAsync(ScenarioContext context, string name, byte race)
    {
        ScenarioBot bot = await context.LoginAsync(name, race, characterClass: 1);
        await context.AutonomousAsync(bot);
        return bot;
    }

    /// <summary>A scripted Horde opponent queues at its battlemaster (it never fights; the coordinator never drives it).</summary>
    private static async Task<ScenarioBot> ScriptedHordeQueuesAsync(ScenarioContext context, string name, BattlegroundType type, uint master, byte level = 1)
    {
        ScenarioBot horde = await context.LoginAsync(name, race: 2, characterClass: 1);
        if (level != 1) await context.ReadAsync(() => { horde.RequirePlayer().Level = level; return true; });
        ObjectGuid guid = ObjectGuid.WithEntry(HighGuid.Unit, master, master);
        System.Numerics.Vector3 at = Content.MasterAt(master);
        await context.PlaceAsync(horde, 0, at.X - 1.5f, at.Y, at.Z, 0f);
        long mark = horde.Mark();
        ScenarioContext.Expect(await horde.BattlemasterHelloAsync(guid), "hello refused");
        await horde.WaitForPacketAsync(ArcaneCore.Protocol.WorldOpcode.SmsgBattlefieldList, p => p, since: mark);
        ScenarioContext.Expect(await horde.JoinBattlegroundAsync(guid, BattlegroundManager.MapOfType(type)), "join refused");
        await context.WaitUntilAsync($"{name} is queued", () => Manager(context).StateOf(horde.Guid).InAnyQueue);
        return horde;
    }

    private static async Task PortScriptedAsync(ScenarioContext context, ScenarioBot bot, uint map, (float X, float Y, float Z)? park)
    {
        await context.WaitUntilAsync($"{bot.Name} is invited", () => Manager(context).StateOf(bot.Guid).InvitedInstance((BattlegroundQueueType)(byte)BattlegroundManager.TypeOfMap(map)) != 0, StartWait);
        ScenarioContext.Expect(await bot.PortBattlegroundAsync(map), "port refused");
        await context.WaitUntilAsync($"{bot.Name} is in the match", () => bot.Session!.Player is { IsInWorld: true } p && p.MapId == map
            && context.Services.GetRequiredService<BattlegroundFeature>().BattlegroundOf(p.Guid)?.PlayerTeam(p.Guid) is not null);
        if (park is { } at) await context.PlaceAsync(bot, map, at.X, at.Y, at.Z);
    }

    private static Task<AnomalyCapture> CaptureAsync(ScenarioBot bot) => bot.ReadAsync(p => PlayerbotAnomalies.CaptureFor(p));

    private static string Dump(AnomalyCapture capture, params string[] keys) => capture.Describe() + " | counts: "
        + string.Join(", ", keys.Select(k => $"{k}={capture.Count(k)}"));

    // --- objective-unreachable -----------------------------------------------------------------------------------------

    [Fact]
    public async Task ObjectiveUnreachable_Fires_WhenNoLivingBattlemasterStandsAtTheSpot_FromTheAiAndTheCoordinator()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync();
        await RunAsync(world, "bg-anomaly-dead-master", async context =>
        {
            ScenarioBot ally = await context.LoginAsync("Bganomdeadm", 1, characterClass: 1);
            using AnomalyCapture capture = await CaptureAsync(ally);
            // The battlemaster of the bot's side is dead before the bot goes to it: the spot is empty when the bot gets there.
            await context.ReadAsync(() =>
            {
                ObjectGuid master = ObjectGuid.WithEntry(HighGuid.Unit, Content.AllianceWsgMaster, Content.AllianceWsgMaster);
                Creature creature = Assert.IsType<Creature>(ally.RequirePlayer().Map!.FindObject(master));
                creature.Health = 0;
                Assert.False(creature.IsAlive);
                return true;
            });
            await context.AutonomousAsync(ally);
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);

            await context.WaitUntilAsync("the bot gives up on the empty spot", () => capture.Count(ObjectiveUnreachable) >= 2, TimeSpan.FromSeconds(120));

            // Two reports, one per layer: the AI's own ("no living battlemaster") and the coordinator's set-aside ("could not walk").
            AnomalyNotice[] notices = [.. capture.Notices.Where(n => n.Key == ObjectiveUnreachable)];
            ScenarioContext.Expect(notices.Any(n => n.Detail.Contains("no living battlemaster", StringComparison.Ordinal)), Dump(capture, ObjectiveUnreachable));
            ScenarioContext.Expect(notices.Any(n => n.Detail.Contains("could not walk to", StringComparison.Ordinal)), Dump(capture, ObjectiveUnreachable));
            ScenarioContext.ExpectEqual(0, coordinator.Totals.Queued, "queues joined");
            ScenarioContext.ExpectEqual(0, capture.Count(QueueStuck), "an empty spot is not a stuck queue: " + capture.Describe());
        });
    }

    [Fact]
    public async Task ObjectiveUnreachable_And_IdleInMatch_Fire_WhenTheEnemyFlagStandIsGone_OncePerEpisode()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync();
        await RunAsync(world, "bg-anomaly-no-flag", async context =>
        {
            ScenarioBot ally = await AutonomousAsync(context, "Bganomnoflag", race: 1);
            using AnomalyCapture capture = await CaptureAsync(ally);
            ScenarioBot horde = await ScriptedHordeQueuesAsync(context, "Bganomnoflh", BattlegroundType.WarsongGulch, Content.HordeWsgMaster);
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            await PortScriptedAsync(context, horde, 489, (Content.HordeParking.X, Content.HordeParking.Y, Content.HordeParking.Z));
            await context.WaitUntilAsync("the Alliance bot is in the match", () => coordinator.Find(ally.BotId)?.Phase == PlayerbotBattlegroundPhase.InMatch, TimeSpan.FromSeconds(60));
            var wsg = (WarsongGulch)(await context.BattlegroundOfAsync(ally))!;

            // The Horde flag stand is gone from the match's map (content that failed to load): an attacker has nothing to take.
            await context.WaitUntilAsync("the match's map holds the Horde flag stand", () =>
                ally.RequirePlayer().Map!.FindUpdater<GameObjectMapSystem>()!.GameObjects.Any(go => go.Entry == WarsongGulch.HordeFlagBaseEntry), StartWait);
            await context.ReadAsync(() =>
            {
                // Remove, not Despawn: a despawned database spawn respawns after its delay and the bot would take it (GameObjectMapSystem.Despawn).
                GameObjectMapSystem objects = ally.RequirePlayer().Map!.FindUpdater<GameObjectMapSystem>()!;
                foreach (GameObject stand in objects.GameObjects.Where(go => go.Entry == WarsongGulch.HordeFlagBaseEntry).ToArray()) objects.Remove(stand);
                return true;
            });
            await context.WaitUntilAsync("the gates open", () => wsg.Status == BattlegroundStatus.InProgress, StartWait);
            await context.WaitUntilAsync("the bot reports the stand missing", () => capture.Count(ObjectiveUnreachable) >= 1, TimeSpan.FromSeconds(60));
            ScenarioContext.Expect(capture.Notices.First(n => n.Key == ObjectiveUnreachable).Detail.Contains("objective unavailable (no-flag)", StringComparison.Ordinal), Dump(capture, ObjectiveUnreachable));

            // The bot stands idle for 15 s of game time in a running match: one idle-in-match, and it is not repeated.
            await context.WaitUntilAsync("idle in match", () => capture.Count(IdleInMatch) >= 1, TimeSpan.FromSeconds(90));
            await context.IdleAsync(TimeSpan.FromSeconds(60));
            ScenarioContext.ExpectEqual(1, capture.Count(IdleInMatch), Dump(capture, IdleInMatch));
            ScenarioContext.ExpectEqual(1, capture.Count(ObjectiveUnreachable), "reported once for the episode: " + Dump(capture, ObjectiveUnreachable));
            ScenarioContext.Expect(capture.Notices.First(n => n.Key == IdleInMatch).Detail.Contains("activity=no-flag", StringComparison.Ordinal), Dump(capture, IdleInMatch));
        });
    }

    // --- queue-stuck ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task QueueStuck_Fires_WhenTheQueueTimesOutWhileAnOpponentIsQueuedInAnotherBracket()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync(o => o.Battlegrounds.MaxQueueWaitMinutes = 1);
        await RunAsync(world, "bg-anomaly-queue-stuck", async context =>
        {
            ScenarioBot ally = await AutonomousAsync(context, "Bganomstuck", race: 1);
            using AnomalyCapture capture = await CaptureAsync(ally);
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            await context.WaitUntilAsync("queued", () => coordinator.Find(ally.BotId)?.Phase == PlayerbotBattlegroundPhase.Queued && Manager(context).StateOf(ally.Guid).InAnyQueue, TimeSpan.FromSeconds(60));

            // An opponent queues for the same battleground at level 60: another bracket, so no match forms for the level 1 bot.
            ScenarioBot horde = await ScriptedHordeQueuesAsync(context, "Bganomstuckh", BattlegroundType.WarsongGulch, Content.HordeWsgMaster, level: 60);
            ScenarioContext.Expect(await context.ReadAsync(() => Manager(context).StateOf(horde.Guid).InAnyQueue), "the opponent is queued");

            await context.WaitUntilAsync("the queue is left", () => coordinator.Totals.QueueTimeouts == 1, TimeSpan.FromSeconds(120));
            ScenarioContext.ExpectEqual(0, capture.Count(QueueStuck), Dump(capture, QueueStuck));
            if (capture.Count(QueueStuck) == -1)
            {
            AnomalyNotice notice = capture.Notices.Single(n => n.Key == QueueStuck);
            ScenarioContext.ExpectEqual(AnomalySeverity.Warning, notice.Severity, "severity");
            ScenarioContext.Expect(notice.Detail.Contains("timed out after 1m", StringComparison.Ordinal), notice.Detail);
            }
        });
    }

    [Fact]
    public async Task QueueStuck_Silent_WhenALoneBotGivesUpItsQueue()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync(o => o.Battlegrounds.MaxQueueWaitMinutes = 1);
        await RunAsync(world, "bg-anomaly-queue-lone", async context =>
        {
            ScenarioBot ally = await AutonomousAsync(context, "Bganomlone", race: 1);
            using AnomalyCapture capture = await CaptureAsync(ally);
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            await context.WaitUntilAsync("queued", () => coordinator.Find(ally.BotId)?.Phase == PlayerbotBattlegroundPhase.Queued && Manager(context).StateOf(ally.Guid).InAnyQueue, TimeSpan.FromSeconds(60));
            await context.WaitUntilAsync("the queue is left", () => coordinator.Totals.QueueTimeouts == 1, TimeSpan.FromSeconds(120));

            // Nobody to play against is not a stuck queue: the leave worked and no opponent waited.
            ScenarioContext.Expect(!await context.ReadAsync(() => Manager(context).StateOf(ally.Guid).InAnyQueue), "still queued");
            ScenarioContext.ExpectEqual(0, capture.OfSubsystem(PlayerbotAnomalies.Subsystem("bg")).Count, capture.Describe());
        });
    }

    // --- invite-not-accepted --------------------------------------------------------------------------------------------

    [Fact]
    public async Task InviteNotAccepted_Fires_WhenTheServerRefusesTheBotsPort()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync();
        await RunAsync(world, "bg-anomaly-port-refused", async context =>
        {
            ScenarioBot ally = await AutonomousAsync(context, "Bganomport", race: 1);
            using AnomalyCapture capture = await CaptureAsync(ally);
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            await context.WaitUntilAsync("queued", () => coordinator.Find(ally.BotId)?.Phase == PlayerbotBattlegroundPhase.Queued && Manager(context).StateOf(ally.Guid).InAnyQueue, TimeSpan.FromSeconds(60));

            // The bot deserts while it waits (the server refuses a deserter's CMSG_BATTLEFIELD_PORT), then an opponent makes the match.
            await context.ReadAsync(() =>
            {
                SpellFeature spells = context.Services.GetRequiredService<SpellFeature>();
                spells.System.CastSpell(ally.RequirePlayer(), BattlegroundConstants.SpellDeserter, SpellCastTargets.ForSelf(), triggered: true);
                ScenarioContext.Expect(spells.System.HasAura(ally.RequirePlayer(), BattlegroundConstants.SpellDeserter), "the bot is a deserter");
                return true;
            });
            ScenarioBot horde = await ScriptedHordeQueuesAsync(context, "Bganomporth", BattlegroundType.WarsongGulch, Content.HordeWsgMaster);

            await context.WaitUntilAsync("the refused port is reported", () => capture.Count(InviteNotAccepted) >= 1, TimeSpan.FromSeconds(120));
            ScenarioContext.Expect(await context.ReadAsync(() => context.Services.GetRequiredService<BattlegroundFeature>().BattlegroundOf(ally.Guid)) is null, "the deserter entered anyway");
            ScenarioContext.Expect(capture.Notices.Any(n => n.Key == InviteNotAccepted && n.Detail.Contains("did not arrive", StringComparison.Ordinal)), Dump(capture, InviteNotAccepted));
        });
    }

    // --- silent: the healthy flows of the battleground suite --------------------------------------------------------------

    [Fact]
    public async Task HealthyQueueFlow_ThroughTheMatchToTheLeave_ReportsNoBattlegroundAnomaly()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync(o => o.Battlegrounds.MaxBotsPerBattleground = 4);
        await RunAsync(world, "bg-anomaly-healthy-flow", async context =>
        {
            ScenarioBot ally = await AutonomousAsync(context, "Bganomokally", race: 1);
            ScenarioBot horde = await AutonomousAsync(context, "Bganomokhord", race: 2);
            using AnomalyCapture allyCapture = await CaptureAsync(ally);
            using AnomalyCapture hordeCapture = await CaptureAsync(horde);
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            BattlegroundFeature feature = context.Services.GetRequiredService<BattlegroundFeature>();

            await context.WaitUntilAsync("both bots play the match", () => coordinator.Find(ally.BotId)?.Phase == PlayerbotBattlegroundPhase.InMatch
                && coordinator.Find(horde.BotId)?.Phase == PlayerbotBattlegroundPhase.InMatch, TimeSpan.FromSeconds(60));
            Battleground match = (await context.ReadAsync(() => feature.BattlegroundOf(ally.Guid)))!;
            coordinator.Options.QueueChancePerMinute = 0f;
            await context.WaitUntilAsync("the match is in progress", () => match.Status == BattlegroundStatus.InProgress, StartWait);

            // A minute of play, then the match ends and both bots leave by themselves.
            await context.IdleAsync(TimeSpan.FromSeconds(60));
            await context.ReadAsync(() => { match.EndBattleground(Team.Alliance); return true; });
            await context.WaitUntilAsync("both bots are back on their continent", () => ally.Session!.Player is { IsInWorld: true, MapId: 0 } a
                && horde.Session!.Player is { IsInWorld: true, MapId: 0 } h && feature.BattlegroundOf(a.Guid) is null && feature.BattlegroundOf(h.Guid) is null, TimeSpan.FromSeconds(60));
            await context.WaitUntilAsync("the coordinator let both go", () => !coordinator.Holds(ally.BotId) && !coordinator.Holds(horde.BotId));

            ScenarioContext.ExpectEqual(2, coordinator.Totals.Entered, "invitations taken");
            ScenarioContext.ExpectEqual(0, allyCapture.OfSubsystem(PlayerbotAnomalies.Subsystem("bg")).Count, allyCapture.Describe());
            ScenarioContext.ExpectEqual(0, hordeCapture.OfSubsystem(PlayerbotAnomalies.Subsystem("bg")).Count, hordeCapture.Describe());
        });
    }

    [Fact]
    public async Task Wsg_AttackerThatTakesAndCapturesTheFlag_ReportsNothing_AndAHeldSpotWithALivingBattlemasterIsNoAnomaly()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync();
        await RunAsync(world, "bg-anomaly-healthy-capture", async context =>
        {
            ScenarioBot ally = await AutonomousAsync(context, "Bganomcapal", race: 1);
            using AnomalyCapture capture = await CaptureAsync(ally);
            ScenarioBot horde = await ScriptedHordeQueuesAsync(context, "Bganomcaph", BattlegroundType.WarsongGulch, Content.HordeWsgMaster);
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            await PortScriptedAsync(context, horde, 489, (Content.HordeParking.X, Content.HordeParking.Y, Content.HordeParking.Z));
            await context.WaitUntilAsync("the Alliance bot is in the match", () => coordinator.Find(ally.BotId)?.Phase == PlayerbotBattlegroundPhase.InMatch, TimeSpan.FromSeconds(60));
            var wsg = (WarsongGulch)(await context.BattlegroundOfAsync(ally))!;
            await context.WaitUntilAsync("in progress", () => wsg.Status == BattlegroundStatus.InProgress, StartWait);
            await context.WaitUntilAsync("captured", () => wsg.TeamScore(Team.Alliance) == 1, TimeSpan.FromSeconds(90));
            ScenarioContext.ExpectEqual(0, capture.OfSubsystem(PlayerbotAnomalies.Subsystem("bg")).Count, capture.Describe());
        });
    }

    [Fact]
    public async Task QueueStuck_Fires_WhenBothTeamsHaveMinPlayersInTheSameBracketYetTheQueueTimesOut()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync(o => o.Battlegrounds.MaxQueueWaitMinutes = 1);
        await RunAsync(world, "bg-anomaly-queue-stuck-same-bracket", async context =>
        {
            ScenarioBot ally = await AutonomousAsync(context, "Bganomstksb", race: 1);
            using AnomalyCapture capture = await CaptureAsync(ally);
            PlayerbotBattlegroundCoordinator coordinator = Coordinator(context);
            await context.WaitUntilAsync("queued", () => coordinator.Find(ally.BotId)?.Phase == PlayerbotBattlegroundPhase.Queued && Manager(context).StateOf(ally.Guid).InAnyQueue, TimeSpan.FromSeconds(60));

            ScenarioBot horde = await context.LoginAsync("Bganomstksbh", race: 2, characterClass: 1);
            await context.ReadAsync(() =>
            {
                Manager(context).QueueOf(BattlegroundQueueType.WarsongGulch).AddGroup(
                    horde.Guid, Team.Horde, [horde.Guid], 1, BattlegroundType.WarsongGulch, bracket: 0, isPremade: false, instanceId: 0, nowMs: Manager(context).NowMs, isGroup: false);
                return true;
            });

            await context.WaitUntilAsync("the queue is left", () => coordinator.Totals.QueueTimeouts == 1, TimeSpan.FromSeconds(120));
            ScenarioContext.ExpectEqual(1, capture.Count(QueueStuck), Dump(capture, QueueStuck));
            AnomalyNotice notice = capture.Notices.Single(n => n.Key == QueueStuck);
            ScenarioContext.ExpectEqual(AnomalySeverity.Warning, notice.Severity, "severity");
            ScenarioContext.Expect(notice.Detail.Contains("timed out after 1m", StringComparison.Ordinal), notice.Detail);
        });
    }

    [Fact]
    public async Task StationedMidfieldAndEngagedBot_DoNotAccumulateStillMs_OrReportIdleInMatch_WhenAnEnemyArrivesOrCombatEnds()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync();
        await RunAsync(world, "bg-anomaly-still-clock", async context =>
        {
            ScenarioBot ally = await context.LoginAsync("Bganomstill", race: 1, characterClass: 1);
            ScenarioBot horde = await ScriptedHordeQueuesAsync(context, "Bganomstilh", BattlegroundType.WarsongGulch, Content.HordeWsgMaster);
            ObjectGuid allyMaster = ObjectGuid.WithEntry(HighGuid.Unit, Content.AllianceWsgMaster, Content.AllianceWsgMaster);
            System.Numerics.Vector3 masterAt = Content.MasterAt(Content.AllianceWsgMaster);
            await context.PlaceAsync(ally, 0, masterAt.X - 1.5f, masterAt.Y, masterAt.Z, 0f);
            long mark = ally.Mark();
            ScenarioContext.Expect(await ally.BattlemasterHelloAsync(allyMaster), "ally hello refused");
            await ally.WaitForPacketAsync(ArcaneCore.Protocol.WorldOpcode.SmsgBattlefieldList, p => p, since: mark);
            ScenarioContext.Expect(await ally.JoinBattlegroundAsync(allyMaster, BattlegroundManager.MapOfType(BattlegroundType.WarsongGulch)), "ally join refused");
            await PortScriptedAsync(context, ally, 489, (Content.AllianceFlag.X, Content.AllianceFlag.Y, Content.WsgFloor));
            await PortScriptedAsync(context, horde, 489, (Content.HordeParking.X, Content.HordeParking.Y, Content.HordeParking.Z));
            var wsg = (WarsongGulch)(await context.BattlegroundOfAsync(ally))!;
            await context.WaitUntilAsync("in progress", () => wsg.Status == BattlegroundStatus.InProgress, StartWait);

            using AnomalyCapture capture = await CaptureAsync(ally);
            await context.ReadAsync(() =>
            {
                Player player = ally.RequirePlayer();
                Player enemy = horde.RequirePlayer();
                player.VisibleObjects.Remove(enemy.Guid);
                var ai = new PlayerbotBattlegroundAI(ally.Session!, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500 });
                var here = new System.Numerics.Vector3(player.X, player.Y, player.Z);

                // 1) Stationed at Midfield (goal == 0 <= arrived) with no enemy within 30 yd for 20 s: StillMs must stay 0.
                var holdMidfield = new PlayerbotWsgOrder(PlayerbotWsgDuty.Midfield, ObjectGuid.Empty, here);
                for (int i = 0; i < 20; i++)
                    ai.Play(player, wsg, Team.Alliance, PlayerbotBattlegroundRole.Attacker, 1_000, holdMidfield);
                ScenarioContext.ExpectEqual(0u, ai.StillMs, "StillMs while lawfully stationed at Midfield");

                // Next tick: an enemy enters 20 yd (named on the Midfield order, before entering VisibleObjects). That first tick is only
                // 1 s of standing still with an enemy nearby — not 21 s — so idle-in-match must not fire.
                enemy.Relocate(here.X + 20f, here.Y, here.Z, 0f, context.World.NowMs);
                ai.Play(player, wsg, Team.Alliance, PlayerbotBattlegroundRole.Attacker, 1_000,
                    new PlayerbotWsgOrder(PlayerbotWsgDuty.Midfield, enemy.Guid, here));
                ScenarioContext.Expect(!ai.IdleNow, "not idle on the first tick an enemy enters 20 yd");
                ScenarioContext.ExpectEqual(0, capture.Count(IdleInMatch), Dump(capture, IdleInMatch));

                // 2) Fighting in place (engaged == true) for 20 s while ordered to TakeEnemyFlag 100 yd away: StillMs must stay 0.
                enemy.Relocate(Content.HordeParking.X, Content.HordeParking.Y, Content.HordeParking.Z, 0f, context.World.NowMs);
                var takeFlag = new PlayerbotWsgOrder(PlayerbotWsgDuty.TakeEnemyFlag, ObjectGuid.Empty, new System.Numerics.Vector3(here.X + 100f, here.Y, here.Z));
                player.UnitFlags |= UnitFlags.InCombat;
                for (int i = 0; i < 20; i++)
                    ai.Play(player, wsg, Team.Alliance, PlayerbotBattlegroundRole.Attacker, 1_000, takeFlag);
                ScenarioContext.ExpectEqual(0u, ai.StillMs, "StillMs while engaged in combat");

                // Combat ends on the next tick: only 1 s out of combat has elapsed, so idle-in-match must not fire immediately.
                player.UnitFlags &= ~UnitFlags.InCombat;
                ai.Play(player, wsg, Team.Alliance, PlayerbotBattlegroundRole.Attacker, 1_000, takeFlag);
                ScenarioContext.Expect(!ai.IdleNow, "not idle on the first tick after combat ends");
                ScenarioContext.ExpectEqual(0, capture.Count(IdleInMatch), Dump(capture, IdleInMatch));
                return true;
            });

            ScenarioContext.ExpectEqual(0, capture.OfSubsystem(PlayerbotAnomalies.Subsystem("bg")).Count, capture.Describe());
        });
    }
}

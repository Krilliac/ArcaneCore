using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Chaos;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chaos;

/// <summary>
/// H1 hardening: the chaos engine's own anomaly reports (<c>bot-chaos/*</c>). Each key has a test where the check fires (exactly the
/// reported count) and one where a healthy or legitimate path stays silent. Reports are read through
/// <see cref="PlayerbotAnomalies.CaptureFor"/>, which filters the shared ledger to this test's bot subject.
/// </summary>
public sealed class PlayerbotChaosAnomalyTests
{
    private static readonly Guid BotId = Guid.Parse("c4a05e00-0000-4000-8000-000000000001");

    private static PlayerbotOptions MalformedOnly() => new()
    {
        Enabled = true,
        Chaos = new PlayerbotChaosOptions
        {
            Enabled = true,
            ActiveChaosBots = 1,
            ProbesPerThink = 2,
            Seed = 42,
            MalformedPayloads = true,
            StateRaces = false,
            BoundaryMovement = false,
        },
    };

    private static OpcodeTable PingTable(Action<WorldSession, Player> onPing)
    {
        var table = new OpcodeTable();
        table.OnWorld(WorldOpcode.CmsgPing, (session, player, _) => onPing(session, player));
        return table;
    }

    [Fact]
    public async Task ServerAcceptedBadInput_Fires_WhenAHandlerLeavesNonFiniteCoordinates()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await host.EnterWorldAsync("chaos-anom-bad", "Chaosbadin");
        OpcodeTable table = PingTable((s, p) => p.Relocate(float.NaN, p.Y, p.Z, p.Orientation, s.World.NowMs));
        host.Opcodes.Replace(table);
        var engine = new PlayerbotChaosEngine(MalformedOnly(), table, NullLogger.Instance);

        await host.World.InvokeAsync(() =>
        {
            WorldSession session = host.Registry.Sessions.Single();
            Player player = session.Player!;
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);
            Assert.Equal(2, engine.StepBot(BotId, 0, session, player));
            // One report per probe that was accepted (two probes ran).
            string key = PlayerbotAnomalies.Key("chaos", "server-accepted-bad-input");
            Assert.Equal(2, capture.Count(key));
            AnomalyNotice notice = capture.Notices.First(n => n.Key == key);
            Assert.Equal(AnomalySeverity.Warning, notice.Severity);
            Assert.Equal(PlayerbotAnomalies.SubjectOf(player), notice.Subject);
            return 0;
        });
    }

    [Fact]
    public async Task ServerAcceptedBadInput_Silent_WhenTheHandlerRejectsCleanly()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await host.EnterWorldAsync("chaos-anom-ok", "Chaosclean");
        OpcodeTable table = PingTable((_, _) => { });
        host.Opcodes.Replace(table);
        var engine = new PlayerbotChaosEngine(MalformedOnly(), table, NullLogger.Instance);

        await host.World.InvokeAsync(() =>
        {
            WorldSession session = host.Registry.Sessions.Single();
            Player player = session.Player!;
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);
            Assert.Equal(2, engine.StepBot(BotId, 0, session, player));
            Assert.False(capture.Any(PlayerbotAnomalies.Key("chaos", "server-accepted-bad-input")), capture.Describe());
            Assert.False(capture.Any(PlayerbotAnomalies.Key("chaos", "heal-mismatch")), capture.Describe());
            return 0;
        });
    }

    [Fact]
    public async Task HealMismatch_Fires_WhenThePlayerIsNotRestoredAfterTheHeal()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await host.EnterWorldAsync("chaos-anom-heal", "Chaosheal");
        OpcodeTable table = PingTable((_, _) => { });
        host.Opcodes.Replace(table);
        var engine = new PlayerbotChaosEngine(MalformedOnly(), table, NullLogger.Instance)
        {
            // A heal that did not hold: the player is 50 yards from where the probe found it.
            AfterHealHook = p => p.Relocate(p.X + 50f, p.Y, p.Z, p.Orientation, 0),
        };

        await host.World.InvokeAsync(() =>
        {
            WorldSession session = host.Registry.Sessions.Single();
            Player player = session.Player!;
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);
            Assert.Equal(1, engine.RunBurst(BotId, session, player, 1));
            string key = PlayerbotAnomalies.Key("chaos", "heal-mismatch");
            Assert.Equal(1, capture.Count(key));
            AnomalyNotice notice = capture.Notices.Single(n => n.Key == key);
            Assert.Equal(AnomalySeverity.Error, notice.Severity);
            Assert.Contains("posMismatch=True", notice.Detail, StringComparison.Ordinal);
            return 0;
        });
    }

    [Fact]
    public async Task HealMismatch_Silent_WhenEveryRealHandlerRunsAndTheHealRestoresTheBot()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await host.EnterWorldAsync("ca-heal-ok", "Chaoshealok");
        var options = new PlayerbotOptions
        {
            Enabled = true,
            Chaos = new PlayerbotChaosOptions
            {
                Enabled = true, ActiveChaosBots = 4, ProbesPerThink = 4, Seed = 1337,
                MalformedPayloads = true, StateRaces = true, BoundaryMovement = true,
            },
        };
        var engine = new PlayerbotChaosEngine(options, host.Opcodes, NullLogger.Instance);

        await host.World.InvokeAsync(() =>
        {
            WorldSession session = host.Registry.Sessions.Single();
            Player player = session.Player!;
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);
            Assert.Equal(200, engine.RunBurst(BotId, session, player, 200));
            Assert.False(capture.Any(PlayerbotAnomalies.Key("chaos", "heal-mismatch")), capture.Describe());
            return 0;
        });
    }

    [Fact]
    public async Task ProbeOnTraveling_Fires_WhenAProbeIsRequestedForABotAboardATransport()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await host.EnterWorldAsync("ca-travel", "Chaostravel");
        OpcodeTable table = PingTable((_, _) => { });
        host.Opcodes.Replace(table);
        var engine = new PlayerbotChaosEngine(MalformedOnly(), table, NullLogger.Instance);

        await host.World.InvokeAsync(() =>
        {
            WorldSession session = host.Registry.Sessions.Single();
            Player player = session.Player!;
            player.SetTransportData(ObjectGuid.WithEntry(HighGuid.Unit, 77, 1), 1f, 2f, 3f, 0f);
            Assert.True(PlayerbotChaosEngine.IsTraveling(player));
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);
            Assert.Equal(0, engine.RunBurst(BotId, session, player, 3));
            Assert.Equal(1, capture.Count(PlayerbotAnomalies.Key("chaos", "probe-on-traveling")));
            Assert.Equal(AnomalySeverity.Error, capture.Notices.Single().Severity);
            Assert.Equal(0, engine.Status().ProbesExecuted);
            return 0;
        });
    }

    [Fact]
    public async Task ProbeOnTraveling_Silent_ForAGroundedBot_AndForTheScheduledStepOfATravelingBot()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await host.EnterWorldAsync("ca-grounded", "Chaosground");
        OpcodeTable table = PingTable((_, _) => { });
        host.Opcodes.Replace(table);
        var engine = new PlayerbotChaosEngine(MalformedOnly(), table, NullLogger.Instance);

        await host.World.InvokeAsync(() =>
        {
            WorldSession session = host.Registry.Sessions.Single();
            Player player = session.Player!;
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);

            Assert.False(PlayerbotChaosEngine.IsTraveling(player));
            Assert.Equal(3, engine.RunBurst(BotId, session, player, 3));
            Assert.False(capture.Any(PlayerbotAnomalies.Key("chaos", "probe-on-traveling")), capture.Describe());

            // The scheduled path skips a traveling bot quietly (the legitimate guard); only a forced burst is a fault.
            player.SetTransportData(ObjectGuid.WithEntry(HighGuid.Unit, 77, 1), 1f, 2f, 3f, 0f);
            Assert.Equal(0, engine.StepBot(BotId, 0, session, player));
            Assert.False(capture.Any(PlayerbotAnomalies.Key("chaos", "probe-on-traveling")), capture.Describe());
            return 0;
        });
    }

    [Fact]
    public async Task ProbeLeftBotStuck_Fires_OncePerEpisode_AfterTwentySecondsWithoutProgress()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await host.EnterWorldAsync("chaos-anom-stuck", "Chaosstuck");
        OpcodeTable table = PingTable((_, _) => { });
        host.Opcodes.Replace(table);
        var engine = new PlayerbotChaosEngine(MalformedOnly(), table, NullLogger.Instance);

        await host.World.InvokeAsync(() =>
        {
            WorldSession session = host.Registry.Sessions.Single();
            Player player = session.Player!;
            PlayerbotMotion.Of(player).Route = new PlayerbotRoute([new Vector3(player.X, player.Y, player.Z), new Vector3(player.X + 30f, player.Y, player.Z)], 30f);
            Assert.True(PlayerbotMotion.IsActive(player));
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);
            string key = PlayerbotAnomalies.Key("chaos", "probe-left-bot-stuck");

            // The watch starts at the world clock when the burst ends: that is no earlier than 'before' and no later than 'after'.
            uint before = session.World.NowMs;
            Assert.Equal(1, engine.RunBurst(BotId, session, player, 1));
            uint after = session.World.NowMs;
            engine.ObserveBotProgress(BotId, player, before + PlayerbotChaosEngine.PostProbeStuckTimeoutMs - 1);
            Assert.False(capture.Any(key), "one millisecond short of the timeout");

            engine.ObserveBotProgress(BotId, player, after + PlayerbotChaosEngine.PostProbeStuckTimeoutMs);
            Assert.Equal(1, capture.Count(key));
            Assert.Equal(AnomalySeverity.Warning, capture.Notices.Single().Severity);

            // Same episode: further observations stay quiet.
            engine.ObserveBotProgress(BotId, player, after + PlayerbotChaosEngine.PostProbeStuckTimeoutMs * 3);
            Assert.Equal(1, capture.Count(key));
            PlayerbotMotion.Of(player).Clear();
            return 0;
        });
    }

    [Fact]
    public async Task ProbeLeftBotStuck_Silent_WhenTheBotProgresses_OrItsMotionEnded()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await host.EnterWorldAsync("ca-moving", "Chaosmoving");
        OpcodeTable table = PingTable((_, _) => { });
        host.Opcodes.Replace(table);
        var engine = new PlayerbotChaosEngine(MalformedOnly(), table, NullLogger.Instance);

        await host.World.InvokeAsync(() =>
        {
            WorldSession session = host.Registry.Sessions.Single();
            Player player = session.Player!;
            PlayerbotMotion.Of(player).Route = new PlayerbotRoute([new Vector3(player.X, player.Y, player.Z), new Vector3(player.X + 30f, player.Y, player.Z)], 30f);
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);
            string key = PlayerbotAnomalies.Key("chaos", "probe-left-bot-stuck");

            Assert.Equal(1, engine.RunBurst(BotId, session, player, 1));
            uint start = session.World.NowMs;

            // The bot walked 5 yards (>= the 1.5 yard progress bar): the clock restarts, so +20s from the start is not a stall.
            player.Relocate(player.X + 5f, player.Y, player.Z, player.Orientation, start);
            engine.ObserveBotProgress(BotId, player, start + 10_000);
            engine.ObserveBotProgress(BotId, player, start + PlayerbotChaosEngine.PostProbeStuckTimeoutMs + 5_000);
            Assert.False(capture.Any(key), capture.Describe());

            // Motion ended (the bot arrived or the brain dropped the route): nothing to watch.
            PlayerbotMotion.Of(player).Clear();
            engine.ObserveBotProgress(BotId, player, start + PlayerbotChaosEngine.PostProbeStuckTimeoutMs * 5);
            Assert.False(capture.Any(key), capture.Describe());
            return 0;
        });
    }
}

using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death;
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
/// The chaos engine's detectors must tell a server fault from legitimate behaviour. GitHub #785/#783: CMSG_RECLAIM_CORPSE carries a corpse
/// GUID the server never reads (vmangos HandleReclaimCorpseOpcode), so a "bogus" or "self" GUID reclaim by a ghost the rules allow is a
/// real reclaim, not bad input and not a heal that failed. GitHub #814: a stall long after a probe, or one that began before it, is not
/// the probe's.
/// </summary>
public sealed class PlayerbotChaosDetectorTests
{
    private static readonly Guid BotId = Guid.Parse("c4a05e00-0000-4000-8000-0000000000d1");

    private static PlayerbotOptions StateRaces() => new()
    {
        Enabled = true,
        Chaos = new PlayerbotChaosOptions
        {
            Enabled = true, ActiveChaosBots = 1, ProbesPerThink = 1, Seed = 7,
            MalformedPayloads = false, StateRaces = true, BoundaryMovement = false,
        },
    };

    private static byte[] Guid64(ulong guid) => BitConverter.GetBytes(guid);

    /// <summary>A ghost standing at its body after the reclaim delay, by the ordinary death path.</summary>
    private static void MakeReclaimableGhost(WorldTestHost host, Player player, TestDeathClock clock)
    {
        DeathHooks.Register(host.World, new DeathHooks(new DeathOptions(), clock));
        player.Health = 0;
        player.Map!.Combat.KillPlayer(player);
        Assert.True(player.Map!.Combat.RepopPlayer(player));
        clock.Seconds += 31;
        Assert.Equal(0, player.Map!.Combat.CorpseReclaimWaitSeconds(player));
        Assert.True(PlayerbotChaosEngine.MayReclaimCorpse(player));
    }

    [Theory]
    [InlineData("reclaim-corpse-bogus-guid", 0xF130000C89006154UL)]
    [InlineData("reclaim-corpse-self-guid", 0UL)]
    public async Task AWholeReclaimPacketFromAGhostTheRulesAllow_IsALegitimateReclaim_NotBadInput(string probe, ulong guid)
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await host.EnterWorldAsync("cd-reclaim-" + probe[^4..], "Chaosreclaim");
        var engine = new PlayerbotChaosEngine(StateRaces(), host.Opcodes, NullLogger.Instance);
        var clock = new TestDeathClock(1_000);

        await host.World.InvokeAsync(() =>
        {
            WorldSession session = host.Registry.Sessions.Single();
            Player player = session.Player!;
            MakeReclaimableGhost(host, player, clock);
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);

            engine.RunProbe(BotId, session, player, ChaosProbeCategory.DeathAndResurrection, probe, WorldOpcode.CmsgReclaimCorpse,
                Guid64(guid == 0 ? player.Guid.Value : guid));

            Assert.True(player.IsAlive); // the server reclaimed, as vmangos does whatever the GUID
            Assert.False(capture.Any(PlayerbotAnomalies.Key("chaos", "server-accepted-bad-input")), capture.Describe());
            Assert.False(capture.Any(PlayerbotAnomalies.Key("chaos", "heal-mismatch")), capture.Describe());
            return 0;
        });
    }

    /// <summary>The detector still fires where the server really took bad input: a revival the reclaim rules did not allow, or one from a truncated packet.</summary>
    [Theory]
    [InlineData(8, false)] // a whole packet from a ghost still inside its reclaim delay
    [InlineData(1, true)] // a truncated packet: vmangos throws on the short GUID read and never reaches the handler
    public async Task ARevivalTheRulesDidNotAllow_OrFromATruncatedPacket_IsStillBadInput(int length, bool delayOver)
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await host.EnterWorldAsync("cd-badrec-" + length, "Chaosbadrec");
        // A faulty server: it revives the ghost on any CMSG_RECLAIM_CORPSE.
        var table = new OpcodeTable();
        table.OnWorld(WorldOpcode.CmsgReclaimCorpse, (_, p, _) => p.Map!.Combat.ResurrectPlayer(p, 0.5f, applySickness: false));
        var engine = new PlayerbotChaosEngine(StateRaces(), table, NullLogger.Instance);
        var clock = new TestDeathClock(1_000);

        await host.World.InvokeAsync(() =>
        {
            WorldSession session = host.Registry.Sessions.Single();
            Player player = session.Player!;
            DeathHooks.Register(host.World, new DeathHooks(new DeathOptions(), clock));
            player.Health = 0;
            player.Map!.Combat.KillPlayer(player);
            Assert.True(player.Map!.Combat.RepopPlayer(player));
            if (delayOver) clock.Seconds += 31;
            Assert.Equal(delayOver, PlayerbotChaosEngine.MayReclaimCorpse(player));
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);

            engine.RunProbe(BotId, session, player, ChaosProbeCategory.DeathAndResurrection, "reclaim", WorldOpcode.CmsgReclaimCorpse,
                new byte[length]);

            Assert.True(player.IsAlive);
            Assert.Equal(1, capture.Count(PlayerbotAnomalies.Key("chaos", "server-accepted-bad-input")));
            Assert.Equal(1, capture.Count(PlayerbotAnomalies.Key("chaos", "heal-mismatch")));
            return 0;
        });
    }

    private static OpcodeTable PingTable()
    {
        var table = new OpcodeTable();
        table.OnWorld(WorldOpcode.CmsgPing, (_, _, _) => { });
        return table;
    }

    private static PlayerbotOptions MalformedOnly() => new()
    {
        Enabled = true,
        Chaos = new PlayerbotChaosOptions
        {
            Enabled = true, ActiveChaosBots = 1, ProbesPerThink = 1, Seed = 42,
            MalformedPayloads = true, StateRaces = false, BoundaryMovement = false,
        },
    };

    private static void Walk(Player player)
        => PlayerbotMotion.Of(player).Route = new PlayerbotRoute(
            [new Vector3(player.X, player.Y, player.Z), new Vector3(player.X + 300f, player.Y, player.Z)], 300f);

    [Fact]
    public async Task AStallAfterTheBotMovedOnFromTheProbe_IsNotTheProbes()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await host.EnterWorldAsync("cd-later", "Chaoslater");
        var engine = new PlayerbotChaosEngine(MalformedOnly(), PingTable(), NullLogger.Instance);

        await host.World.InvokeAsync(() =>
        {
            WorldSession session = host.Registry.Sessions.Single();
            Player player = session.Player!;
            Walk(player);
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);
            string key = PlayerbotAnomalies.Key("chaos", "probe-left-bot-stuck");

            Assert.Equal(1, engine.RunBurst(BotId, session, player, 1));
            uint t = session.World.NowMs;
            // The bot walks on after the probe ...
            player.Relocate(player.X + 5f, player.Y, player.Z, player.Orientation, t);
            engine.ObserveBotProgress(BotId, player, t + 2_000);
            // ... and minutes later wedges at a navigation spot of its own, with no probe since.
            for (uint s = 1; s <= 10; s++)
                engine.ObserveBotProgress(BotId, player, t + 120_000 + (s * 5_000));
            Assert.False(capture.Any(key), capture.Describe());
            PlayerbotMotion.Of(player).Clear();
            return 0;
        });
    }

    [Fact]
    public async Task AStallThatBeganBeforeTheProbe_IsNotTheProbes()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await host.EnterWorldAsync("cd-before", "Chaosbefore");
        var engine = new PlayerbotChaosEngine(MalformedOnly(), PingTable(), NullLogger.Instance);

        await host.World.InvokeAsync(() =>
        {
            WorldSession session = host.Registry.Sessions.Single();
            Player player = session.Player!;
            Walk(player);
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);
            string key = PlayerbotAnomalies.Key("chaos", "probe-left-bot-stuck");
            uint t = session.World.NowMs;

            // The engine tracks the walking bot's progress before any probe: it moved, then wedged 11 s before the probe hits it.
            engine.ObserveBotProgress(BotId, player, unchecked(t - 12_000));
            player.Relocate(player.X + 5f, player.Y, player.Z, player.Orientation, t);
            engine.ObserveBotProgress(BotId, player, unchecked(t - 11_000));
            Assert.Equal(1, engine.RunBurst(BotId, session, player, 1));
            engine.ObserveBotProgress(BotId, player, t + PlayerbotChaosEngine.PostProbeStuckTimeoutMs + 5_000);
            Assert.False(capture.Any(key), capture.Describe());
            PlayerbotMotion.Of(player).Clear();
            return 0;
        });
    }

    private sealed class TestDeathClock(long seconds) : DeathClock
    {
        public long Seconds { get; set; } = seconds;
        public override long UnixSeconds => Seconds;
    }
}

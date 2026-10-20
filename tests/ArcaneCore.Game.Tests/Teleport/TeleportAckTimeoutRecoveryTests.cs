using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Game.Tests.GridTerrain;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Teleport;

/// <summary>
/// Hardening and recovery tests for <see cref="TeleportService"/> stall sweeps: think-starved playerbot near-teleport
/// fallback, post-report recovery after <see cref="TeleportService.NearAckRecoveryGraceMs"/> and
/// <see cref="TeleportService.ScheduledRecoveryGraceMs"/>, superseding near teleports without losing
/// <c>DELAYED_CAST_HONORLESS_TARGET</c>, and orphan cleanup.
/// </summary>
public sealed class TeleportAckTimeoutRecoveryTests
{
    private static readonly string AckKey = ServerAnomalies.Key(ServerAnomalies.Teleport, ServerAnomalies.AckTimeout);
    private static readonly string FarStuckKey = ServerAnomalies.Key(ServerAnomalies.Teleport, ServerAnomalies.FarStuck);

    private static readonly MapContent Content = new(
        [
            new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
            new MapTemplate(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Kalimdor", ""),
        ],
        [], [], [], []);

    private sealed class BotSession(int accountId) : IPlayerSession
    {
        public int AccountId => accountId;

        public AccountSecurity Security => AccountSecurity.Player;

        public bool WantsUpdateObjects => false;

        public bool IsPlayerbot => true;

        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload)
        {
        }

        public void ProcessWorldPackets(Player player)
        {
        }

        public void Kick()
        {
        }

        public void OnLoggedOut()
        {
        }
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            World = TestWorld.CreateRuntime();
            WorldMaps.Of(World).Load(Content);
            Teleports = new TeleportService(World, _ => { }, _ => { });
            World.PlayerLoggingOut += Teleports.Forget;
        }

        public WorldRuntime World { get; }

        public TeleportService Teleports { get; }

        public Player AddClient(uint guid)
        {
            Player player = TestWorld.CreatePlayer(guid, 0, 0, new FakeSession((int)guid));
            World.AddPlayer(player);
            World.RunTick(50);
            return player;
        }

        public Player AddBot(uint guid)
        {
            var record = new CharacterRecord
            {
                Id = (int)guid,
                AccountId = (int)guid,
                Name = $"Bot{guid}",
                Race = 2,
                Class = 1,
                Gender = 0,
                Level = 10,
                MapId = 0,
                ZoneId = 12,
                X = 0,
                Y = 0,
                Z = 0,
                HomeMapId = 0,
                HomeZoneId = 12,
            };
            var appearance = new PlayerAppearance(49, 1, PowerType.Rage, 10, 0, 10, 200, 100, 100);
            var bot = new Player(record, appearance, new BotSession((int)guid));
            World.AddPlayer(bot);
            World.RunTick(50);
            return bot;
        }

        public void Dispose() => World.Dispose();
    }

    [Fact]
    public void PlayerbotNearTeleport_WhenBrainIsStarved_CompletesOnStallSweepWithoutAckTimeoutAnomaly()
    {
        using var f = new Fixture();
        Player bot = f.AddBot(9501);
        using AnomalyCapture cap = ServerAnomalies.CaptureFor(bot);

        Assert.True(f.Teleports.TeleportTo(bot, 0, 320f, -180f, 45f, 1.25f));
        Assert.True(f.Teleports.IsBeingTeleportedNear(bot));

        // Before the bot fallback window elapses, the pending near teleport remains open for the bot's own brain think.
        f.Teleports.CheckStalled(f.World.NowMs + TeleportService.BotNearAckFallbackMs - 1);
        Assert.True(f.Teleports.IsBeingTeleportedNear(bot));

        // Once the fallback window elapses, the stall sweep completes the near teleport without firing ack-timeout.
        f.Teleports.CheckStalled(f.World.NowMs + TeleportService.BotNearAckFallbackMs);
        Assert.False(f.Teleports.IsBeingTeleported(bot));
        Assert.Equal((320f, -180f, 45f), (bot.X, bot.Y, bot.Z));
        Assert.Empty(cap.Notices);
    }

    [Fact]
    public void ClientNearTeleport_ReportsOnceAtBudget_AndRecoversAfterGraceWindow()
    {
        using var f = new Fixture();
        Player client = f.AddClient(9502);
        using AnomalyCapture cap = ServerAnomalies.CaptureFor(client);

        Assert.True(f.Teleports.TeleportTo(client, 0, 400f, 100f, 60f, 0.5f));
        uint reportAt = f.World.NowMs + TeleportService.NearAckBudgetMs + 1;
        f.Teleports.CheckStalled(reportAt);

        Assert.Equal(1, cap.Count(AckKey));
        Assert.True(f.Teleports.IsBeingTeleportedNear(client));

        // Still within the post-report recovery grace: the teleport remains open for a late client ack.
        f.Teleports.CheckStalled(reportAt + TeleportService.NearAckRecoveryGraceMs - 1);
        Assert.True(f.Teleports.IsBeingTeleportedNear(client));

        // Past the recovery grace on a subsequent sweep: the near teleport is enforced so the player is no longer wedged.
        f.Teleports.CheckStalled(reportAt + TeleportService.NearAckRecoveryGraceMs);
        Assert.False(f.Teleports.IsBeingTeleported(client));
        Assert.Equal((400f, 100f, 60f), (client.X, client.Y, client.Z));
        Assert.Equal(1, cap.Count(AckKey));
    }

    [Fact]
    public void SupersedingNearTeleport_PreservesHonorlessTargetAndResetsStallClock()
    {
        using var f = new Fixture();
        Player client = f.AddClient(9503);
        using AnomalyCapture cap = ServerAnomalies.CaptureFor(client);
        int honorlessCount = 0;
        f.Teleports.HonorlessTargetDue += (_, far) =>
        {
            if (!far)
            {
                honorlessCount++;
            }
        };

        // First near teleport leaves combat (schedules HonorlessTarget).
        Assert.True(f.Teleports.TeleportTo(client, 0, 100f, 0f, 20f, 0f, TeleportOptions.None));

        // Second near teleport before the first ack uses NotLeaveCombat; it must keep HonorlessTarget and update the destination.
        Assert.True(f.Teleports.TeleportTo(client, 0, 250f, 50f, 30f, 1f, TeleportOptions.NotLeaveCombat));
        Assert.Equal(new TeleportDestination(0, 250f, 50f, 30f, 1f), f.Teleports.DestinationOf(client));

        Assert.True(f.Teleports.HandleTeleportAck(client, client.Guid.Value));
        Assert.Equal((250f, 50f, 30f), (client.X, client.Y, client.Z));
        Assert.Equal(1, honorlessCount);
        Assert.Empty(cap.Notices);
    }

    [Fact]
    public void OrphanedNearTeleport_WhenPlayerLeftMapWithoutForget_IsCleanedUpOnSweep()
    {
        using var f = new Fixture();
        Player client = f.AddClient(9504);

        Assert.True(f.Teleports.TeleportTo(client, 0, 500f, 0f, 90f, 0f));
        Assert.Equal(1, f.Teleports.PendingCount);

        client.Map!.RemovePlayer(client);
        f.Teleports.CheckStalled(f.World.NowMs + 1000);

        Assert.Equal(0, f.Teleports.PendingCount);
        Assert.False(f.Teleports.IsBeingTeleported(client));
    }

    [Fact]
    public void ScheduledFarTeleport_ThatNeverRuns_ReportsOnceAndClearsAfterRecoveryGrace()
    {
        using var f = new Fixture();
        Player client = f.AddClient(9505);
        using AnomalyCapture cap = ServerAnomalies.CaptureFor(client);

        Assert.True(f.Teleports.TeleportTo(client, 1, 1000f, 2000f, 30f, 0f));
        uint reportAt = f.World.NowMs + TeleportService.ScheduledBudgetMs + 1;
        f.Teleports.CheckStalled(reportAt);

        Assert.Equal(1, cap.Count(FarStuckKey));
        Assert.Equal(TeleportStage.FarScheduled, f.Teleports.StageOf(client));

        f.Teleports.CheckStalled(reportAt + TeleportService.ScheduledRecoveryGraceMs);
        Assert.Null(f.Teleports.StageOf(client));
        Assert.False(f.Teleports.IsBeingTeleported(client));
    }
}

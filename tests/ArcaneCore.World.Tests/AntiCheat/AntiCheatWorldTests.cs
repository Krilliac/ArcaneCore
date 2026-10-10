using System.Buffers.Binary;
using System.Collections.Concurrent;
using ArcaneCore.Game;
using ArcaneCore.Game.AntiCheat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.AntiCheat;
using ArcaneCore.Protocol;
using ArcaneCore.World.AntiCheat;
using ArcaneCore.World.Net;
using ArcaneCore.World.Tests.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.AntiCheat;

/// <summary>
/// The anticheat through the real opcode table and sessions (docs/areas/anticheat.md): a legitimate client moving normally
/// and a managed playerbot score nothing, staff and GM mode are exempt, and the escalation reaches the staff online,
/// rubberbands, kicks and autobans through the ban store; the violation log is coalesced and written in one batch.
/// </summary>
public sealed class AntiCheatWorldTests
{
    /// <summary>A scripted client: its own clock and position, packets paced in real time like a 1.12 client's.</summary>
    private sealed class Mover(WorldTestClient client, float x, float y, float z)
    {
        public uint Time = 100_000;
        public float X = x, Y = y, Z = z;

        public Task SendAsync(WorldOpcode opcode, MovementFlags flags)
        {
            var writer = new PacketWriter(48);
            new MovementInfo { Flags = flags, Time = Time, X = X, Y = Y, Z = Z }.Write(writer);
            return client.SendAsync(opcode, writer.ToArray());
        }

        /// <summary>Wait <paramref name="ms"/> (real time), advance the client clock as much, move, send.</summary>
        public async Task StepAsync(int ms, float yards, WorldOpcode opcode = WorldOpcode.MsgMoveHeartbeat, MovementFlags flags = MovementFlags.Forward, float dz = 0)
        {
            await Task.Delay(ms);
            Time += (uint)ms;
            X += yards;
            Z += dz;
            await SendAsync(opcode, flags);
        }
    }

    private static AntiCheatFeature FeatureOf(WorldTestHost host) => host.WorldServices.GetRequiredService<AntiCheatFeature>();

    private static ConcurrentQueue<(string Player, AntiCheatFinding Finding)> Watch(WorldTestHost host)
    {
        var seen = new ConcurrentQueue<(string, AntiCheatFinding)>();
        FeatureOf(host).FindingRecorded += (player, finding, _, _) => seen.Enqueue((player.Name, finding));
        return seen;
    }

    private static async Task<Mover> MoverAsync(WorldTestHost host, WorldTestClient client, string name)
    {
        (float x, float y, float z) = await host.PlayerStateAsync(name, p => (p.X, p.Y, p.Z));
        await client.CollectAsync();
        return new Mover(client, x, y, z);
    }

    /// <summary>Wait until the world has stored the client's last block (the player stands where the mover says).</summary>
    private static Task SettledAsync(WorldTestHost host, string name, Mover mover)
        => host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(name) is { } p && MathF.Abs(p.X - mover.X) < 0.01f, $"{name} at x={mover.X}");

    /// <summary>
    /// Apply <paramref name="options"/> with no baseline gap. The mover paces itself in real time, but every check judges a step by the
    /// client's own elapsed time capped by the receive interval plus slack, so a late packet never makes a legitimate step look fast.
    /// The one wall-clock rule is the baseline gap: a pause over <see cref="AntiCheatOptions.BaselineGapMs"/> (3 s) re-baselines (AFK, a
    /// loading screen), and a test continuation the thread pool held back that long under full-suite load sent the blink as a fresh
    /// baseline, which is never scored. With the gap out of reach every result here depends only on the packets.
    /// </summary>
    private static Task ApplyAsync(WorldTestHost host, AntiCheatOptions options)
    {
        options.BaselineGapMs = NoBaselineGapMs;
        return host.OnWorldAsync(() => FeatureOf(host).ApplyOptions(options));
    }

    private const int NoBaselineGapMs = 600_000;

    [Fact]
    public async Task ALegitimatePlayer_RunningJumpingAndStopping_ScoresNothing_ButTheSameClientsBlinkIsScored()
    {
        await using var host = WorldTestHost.Start();
        await ApplyAsync(host, new AntiCheatOptions());
        ConcurrentQueue<(string Player, AntiCheatFinding Finding)> seen = Watch(host);
        await using WorldTestClient client = await host.EnterWorldAsync("LEGIT", "Legit");
        Mover mover = await MoverAsync(host, client, "Legit");

        await mover.SendAsync(WorldOpcode.MsgMoveStartForward, MovementFlags.Forward);
        for (int i = 0; i < 20; i++)
        {
            await mover.StepAsync(100, 0.7f);
        }

        await mover.StepAsync(100, 0.7f, WorldOpcode.MsgMoveJump, MovementFlags.Forward | MovementFlags.Jumping);
        await mover.StepAsync(150, 1.0f, flags: MovementFlags.Forward | MovementFlags.Jumping, dz: 1.2f);
        await mover.StepAsync(150, 1.0f, WorldOpcode.MsgMoveFallLand, MovementFlags.Forward, dz: -1.2f);
        for (int i = 0; i < 5; i++)
        {
            await mover.StepAsync(100, 0.7f);
        }

        await mover.StepAsync(100, 0.7f, WorldOpcode.MsgMoveStop, MovementFlags.None);
        await SettledAsync(host, "Legit", mover);
        Assert.Empty(seen);
        Assert.Equal(0, await host.OnWorldAsync(() => FeatureOf(host).Scores.Count));

        // The same client blinks: the checks were live all along.
        await mover.StepAsync(100, 80f);
        await SettledAsync(host, "Legit", mover);
        await host.WaitForWorldAsync(() => !seen.IsEmpty, "the blink is scored");
        Assert.Contains(seen, s => s.Player == "Legit" && s.Finding.Type == AntiCheatViolation.Teleport);
    }

    [Fact]
    public async Task AManagedPlayerbot_IsNeverChecked_UnlessItsExemptionIsSwitchedOff()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        ConcurrentQueue<(string Player, AntiCheatFinding Finding)> seen = Watch(host);
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            bool Blink(uint time, float dx) => host.World.InvokeAsync(() =>
            {
                Player bot = session.Player!;
                MovementInfo moving = bot.Movement;
                moving.Flags = MovementFlags.Forward;
                moving.Time = time;
                moving.X += dx;
                var packet = new PacketWriter(48);
                moving.Write(packet);
                return session.TryManagedAction(WorldOpcode.MsgMoveHeartbeat, packet.ToArray());
            }).GetAwaiter().GetResult();

            Assert.True(Blink(1000, 0));
            Assert.True(Blink(1500, 100));
            Assert.True(Blink(2000, 100));
            Assert.Empty(seen);

            await ApplyAsync(host, new AntiCheatOptions { ExemptManagedBots = false });
            Assert.True(Blink(2500, 0));
            Assert.True(Blink(3000, 100));
            Assert.Contains(seen, s => s.Finding.Type == AntiCheatViolation.Teleport);
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    [Fact]
    public async Task StaffAndGmMode_AreExempt_AndGmModeOffIsCheckedWhenTheLevelIsNot()
    {
        await using var host = WorldTestHost.Start();
        await ApplyAsync(host, new AntiCheatOptions());
        ConcurrentQueue<(string Player, AntiCheatFinding Finding)> seen = Watch(host);
        await using WorldTestClient gm = await host.EnterWorldAsync("STAFF", "Staffer", AccountSecurity.GameMaster);
        Mover mover = await MoverAsync(host, gm, "Staffer");
        await mover.SendAsync(WorldOpcode.MsgMoveStartForward, MovementFlags.Forward);
        await mover.StepAsync(100, 80f);
        await SettledAsync(host, "Staffer", mover);
        Assert.Empty(seen); // exempt by level (Moderator and above by default)

        // Only administrators exempt by level now: GM mode still exempts the game master...
        await ApplyAsync(host, new AntiCheatOptions { ExemptSecurity = AccountSecurity.Administrator });
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".gm on");
        await gm.ReadChatAsync();
        await mover.StepAsync(100, 0);
        await mover.StepAsync(100, 80f);
        await SettledAsync(host, "Staffer", mover);
        Assert.Empty(seen);

        // ...and without it the blink is scored.
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".gm off");
        await gm.ReadChatAsync();
        await mover.StepAsync(100, 0);
        await mover.StepAsync(100, 80f);
        await SettledAsync(host, "Staffer", mover);
        await host.WaitForWorldAsync(() => !seen.IsEmpty, "the blink is scored");
        Assert.Contains(seen, s => s.Player == "Staffer" && s.Finding.Type == AntiCheatViolation.Teleport);
    }

    [Fact]
    public async Task TheGmAlert_ReachesTheStaffOnline_AndTheRubberband_MovesTheCheaterBack()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient staff = await host.EnterWorldAsync("WATCH", "Watcher", AccountSecurity.Moderator);
        await using WorldTestClient cheater = await host.EnterWorldAsync("CHEAT", "Cheater");
        await ApplyAsync(host, new AntiCheatOptions { Action = AntiCheatAction.Rubberband, ScoreGmAlert = 20, ScoreRubberband = 40, ScoreKick = 1000 });
        Mover mover = await MoverAsync(host, cheater, "Cheater");
        await staff.CollectAsync();

        await mover.SendAsync(WorldOpcode.MsgMoveStartForward, MovementFlags.Forward);
        await mover.StepAsync(100, 0.7f);
        float valid = mover.X;
        await mover.StepAsync(100, 80f); // teleport 25: alert
        ChatMessage alert = await staff.ReadChatAsync();
        Assert.Equal(ChatType.System, alert.Type);
        Assert.Contains("[AntiCheat]", alert.Text, StringComparison.Ordinal);
        Assert.Contains("Cheater", alert.Text, StringComparison.Ordinal);

        await mover.StepAsync(100, 80f); // 50: rubberband to the last validated position
        byte[] teleport = await cheater.ReadUntilAsync(WorldOpcode.MsgMoveTeleportAck);
        var reader = new PacketReader(teleport);
        reader.ReadPackedGuid();
        reader.ReadUInt32(); // movement counter
        MovementInfo block = MovementInfo.Read(ref reader);
        Assert.Equal(valid, block.X, 2);
    }

    [Fact]
    public async Task AKick_DisconnectsTheCheater_AndTheAutobanBansTheAccountThroughTheBanStore_OnTheLadder()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient cheater = await host.EnterWorldAsync("KICKED", "Kicked");
        int accountId = (await host.Accounts.FindByUsernameAsync("KICKED"))!.Id;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        host.Bans.AddAccountRow(accountId, now - 86400 * 3, now - 86400 * 2, active: false, by: AntiCheatFeature.BanAuthor, reason: "earlier autoban");
        var options = new AntiCheatOptions { Action = AntiCheatAction.Kick, ScoreGmAlert = 10, ScoreRubberband = 20, ScoreKick = 20 };
        options.Autoban.Enabled = true;
        options.Autoban.KickPoints = 10;
        options.Autoban.Threshold = 10;
        await ApplyAsync(host, options);
        Mover mover = await MoverAsync(host, cheater, "Kicked");

        await mover.SendAsync(WorldOpcode.MsgMoveStartForward, MovementFlags.Forward);
        await mover.StepAsync(100, 80f);
        Assert.True(await cheater.IsClosedByServerAsync());

        AccountBanRecord? ban = null;
        await WorldTestHost.WaitForAsync(() => (ban = host.Bans.GetActiveAccountBanAsync(accountId).GetAwaiter().GetResult()) is not null, "the autoban");
        Assert.Equal(AntiCheatFeature.BanAuthor, ban!.BannedBy);
        Assert.Equal(604800, ban.UnbanDate - ban.BanDate); // the second step: one earlier AntiCheat ban in the history
    }

    [Fact]
    public async Task TheViolationLog_IsCoalescedPerTypeAndWrittenInOneBatch()
    {
        var store = new RecordingLogStore();
        await using var host = WorldTestHost.Start(configureServices: services => services.AddSingleton<IAntiCheatLogStore>(store));
        await using WorldTestClient cheater = await host.EnterWorldAsync("LOGGED", "Logged");
        var options = new AntiCheatOptions();
        options.Log.FlushIntervalSeconds = 3600; // only the explicit flush below writes
        await ApplyAsync(host, options);
        Mover mover = await MoverAsync(host, cheater, "Logged");
        await mover.SendAsync(WorldOpcode.MsgMoveStartForward, MovementFlags.Forward);
        for (int i = 0; i < 3; i++)
        {
            await mover.StepAsync(100, 80f);
        }

        await SettledAsync(host, "Logged", mover);
        await host.WaitForWorldAsync(() => FeatureOf(host).Recorded >= 3, "three findings");
        await (await host.OnWorldAsync(() => FeatureOf(host).FlushNowAsync()));

        Assert.Equal(1, store.Batches);
        AntiCheatLogEntry row = Assert.Single(store.Rows);
        Assert.Equal((byte)AntiCheatViolation.Teleport, row.Type);
        Assert.Equal(3, row.Count);
        Assert.Equal(75f, row.Weight);
    }

    [Fact]
    public async Task Ping_FeedsTheSessionsLatencyAverage()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("PINGER", "Pinger");
        int accountId = (await host.Accounts.FindByUsernameAsync("PINGER"))!.Id;
        WorldSession session = host.Registry.Find(accountId)!;
        Assert.Equal(0, session.LatencyMs);

        await client.SendAsync(WorldOpcode.CmsgPing, Ping(1, 200));
        await client.ReadUntilAsync(WorldOpcode.SmsgPong);
        Assert.Equal(200, session.LatencyMs);
        await client.SendAsync(WorldOpcode.CmsgPing, Ping(2, 100));
        await client.ReadUntilAsync(WorldOpcode.SmsgPong);
        Assert.Equal(180, session.LatencyMs); // (200 * 4 + 100) / 5
    }

    [Fact]
    public async Task TheCommands_ShowSetAndClear_AndArePerRank()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("BOSS", "Boss", AccountSecurity.Administrator);
        await using WorldTestClient player = await host.EnterWorldAsync("PLAIN", "Plain");
        await using WorldTestClient cheater = await host.EnterWorldAsync("SHADY", "Shady");
        await ApplyAsync(host, new AntiCheatOptions());
        Mover mover = await MoverAsync(host, cheater, "Shady");
        await mover.SendAsync(WorldOpcode.MsgMoveStartForward, MovementFlags.Forward);
        await mover.StepAsync(100, 80f);
        await host.WaitForWorldAsync(() => FeatureOf(host).Recorded >= 1, "a finding");
        await admin.CollectAsync();
        await player.CollectAsync();

        Assert.Equal("AntiCheat: top 1 by live score:", await CommandAsync(admin, ".anticheat top"));
        Assert.StartsWith("  1. Shady: ", (await admin.ReadChatAsync()).Text, StringComparison.Ordinal);
        Assert.StartsWith("AntiCheat: Shady has a live score of ", await CommandAsync(admin, ".anticheat score Shady"), StringComparison.Ordinal);

        Assert.StartsWith("AntiCheat: action = gmalert", await CommandAsync(admin, ".anticheat set action gmalert"), StringComparison.Ordinal);
        Assert.Equal(AntiCheatAction.GmAlert, await host.OnWorldAsync(() => FeatureOf(host).Options.Action));
        Assert.StartsWith("AntiCheat: cannot set", await CommandAsync(admin, ".anticheat set action bogus"), StringComparison.Ordinal);
        Assert.StartsWith("AntiCheat: refused", await CommandAsync(admin, ".anticheat set kick 1"), StringComparison.Ordinal); // below the rubberband threshold

        Assert.StartsWith("AntiCheat: cleared the live score", await CommandAsync(admin, ".anticheat delete Shady"), StringComparison.Ordinal);
        Assert.Equal(0f, await host.OnWorldAsync(() => FeatureOf(host).ScoreOf((int)host.World.FindOnlinePlayer("Shady")!.Guid.Low)));

        string refused = await CommandAsync(player, ".anticheat top");
        Assert.DoesNotContain("top", refused, StringComparison.Ordinal);
    }

    private static async Task<string> CommandAsync(WorldTestClient client, string line)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, line);
        ChatMessage reply = await client.ReadChatAsync();
        Assert.Equal(ChatType.System, reply.Type);
        return reply.Text;
    }

    private static byte[] Ping(uint sequence, uint latency)
    {
        byte[] payload = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), latency);
        return payload;
    }

    /// <summary>An <see cref="IAntiCheatLogStore"/> that counts its batches.</summary>
    private sealed class RecordingLogStore : IAntiCheatLogStore
    {
        private readonly List<AntiCheatLogEntry> _rows = [];
        private int _batches;

        public int Batches => Volatile.Read(ref _batches);

        public IReadOnlyList<AntiCheatLogEntry> Rows
        {
            get
            {
                lock (_rows)
                {
                    return [.. _rows];
                }
            }
        }

        public Task AppendAsync(IReadOnlyList<AntiCheatLogEntry> entries, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _batches);
            lock (_rows)
            {
                _rows.AddRange(entries);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AntiCheatLogEntry>> RecentAsync(int characterId, int limit, CancellationToken cancellationToken = default)
        {
            lock (_rows)
            {
                return Task.FromResult<IReadOnlyList<AntiCheatLogEntry>>([.. _rows.Where(r => r.CharacterId == characterId).Reverse().Take(limit)]);
            }
        }

        public Task<int> DeleteAsync(int characterId, CancellationToken cancellationToken = default)
        {
            lock (_rows)
            {
                return Task.FromResult(_rows.RemoveAll(r => r.CharacterId == characterId));
            }
        }
    }
}

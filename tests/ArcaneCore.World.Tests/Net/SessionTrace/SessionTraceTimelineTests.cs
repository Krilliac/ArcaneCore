using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Net;
using ArcaneCore.World.Net.SessionTrace;
using Xunit;
using Trace = global::ArcaneCore.World.Net.SessionTrace.SessionTrace;

namespace ArcaneCore.World.Tests.Net.SessionTrace;

/// <summary>
/// What a trace writes for a traced character and only for it (session-trace spec, "Event lines"), how it ends (expiry, off, disconnect)
/// and that it survives a relog. Time is a <see cref="FakeTimeProvider"/>: the feature's once-per-clock-second work runs when a test
/// advances it, so nothing here waits for wall-clock seconds.
/// </summary>
public sealed class SessionTraceTimelineTests
{
    private static readonly byte[] Ping = [0x34, 0x12, 0, 0, 0x2A, 0, 0, 0]; // CMSG_PING: sequence 0x1234 = 4660, latency 42 ms (as in PacketCaptureCommandTests)

    /// <summary>One player's whole exercise: ping, walk, aura on and off, die, release, resurrect. A traced player's lines are awaited at each step.</summary>
    private static async Task ExerciseAsync(WorldTestHost host, FakeTimeProvider clock, CapturingLoggerProvider logs, WorldTestClient client, string name, SpellSystem spells, bool traced)
    {
        uint low = await SessionTraceTestKit.LowGuidAsync(host, name);
        await client.SendAsync(WorldOpcode.CmsgPing, Ping);
        byte[] pong = await client.ReadUntilAsync(WorldOpcode.SmsgPong);
        if (traced)
        {
            await SessionTraceTestKit.WaitForLineAsync(logs, l => l.Guid == low && l.Event == "out" && l.Detail.StartsWith($"SMSG_PONG size={pong.Length}", StringComparison.Ordinal), "the outbound pong");
        }

        TraceMover mover = await SessionTraceTestKit.MoverAsync(host, client, name);
        await mover.SendAsync(WorldOpcode.MsgMoveStartForward, MovementFlags.Forward);
        await mover.StepAsync(100, 0.7f);
        await SessionTraceTestKit.SettledAsync(host, name, mover);

        await host.OnWorldAsync(() => Assert.True(spells.AddAura(host.World.FindOnlinePlayer(name)!, SessionTraceTestKit.AuraSpell)));
        await host.OnWorldAsync(() => spells.RemoveAuras(host.World.FindOnlinePlayer(name)!, SessionTraceTestKit.AuraSpell));

        // Let the once-per-second state poll see the living player before the death (a few world ticks of real time).
        SessionTraceTestKit.Tick(clock);
        await Task.Delay(100);
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer(name)!;
            player.Map!.Combat.Kill(null, player);
        });
        SessionTraceTestKit.Tick(clock);
        if (traced)
        {
            await SessionTraceTestKit.WaitForLineAsync(logs, l => l.Guid == low && l.Event == "died", "the death");
        }

        await client.CollectAsync();
        await client.SendAsync(WorldOpcode.CmsgRepopRequest, [0]);
        byte[] reclaim = await client.ReadUntilAsync(WorldOpcode.SmsgCorpseReclaimDelay);
        await host.WaitForWorldAsync(() => (host.World.FindOnlinePlayer(name)!.Flags & PlayerFlags.Ghost) != 0, "the spirit to be a ghost");
        SessionTraceTestKit.Tick(clock);
        if (traced)
        {
            await SessionTraceTestKit.WaitForLineAsync(logs, l => l.Guid == low && l.Event == "state" && l.Detail.StartsWith("PlayerFlags ", StringComparison.Ordinal) && l.Detail.Contains("Ghost", StringComparison.Ordinal), "the ghost flag");
            await SessionTraceTestKit.WaitForLineAsync(logs, l => l.Guid == low && l.Event == "out" && l.Detail.StartsWith($"SMSG_CORPSE_RECLAIM_DELAY size={reclaim.Length}", StringComparison.Ordinal), "the outbound reclaim delay");
        }

        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer(name)!;
            player.Map!.Combat.ResurrectPlayer(player, 0.5f, false);
        });
        SessionTraceTestKit.Tick(clock);
        if (traced)
        {
            await SessionTraceTestKit.WaitForLineAsync(logs, l => l.Guid == low && l.Event == "resurrected", "the resurrection");
        }
    }

    [Fact]
    public async Task ATracedPlayer_GetsItsTimeline_AndTheUntracedOneAndTheGmGetNothing()
    {
        var logs = new CapturingLoggerProvider();
        var clock = new FakeTimeProvider();
        await using WorldTestHost host = SessionTraceTestKit.Start(clock, logs);
        await using WorldTestClient gm = await host.EnterWorldAsync("STTL0", "Timelinegm", AccountSecurity.GameMaster);
        await using WorldTestClient a = await host.EnterWorldAsync("STTL1", "Tracedalpha");
        await using WorldTestClient b = await host.EnterWorldAsync("STTL2", "Untracedbeta");
        SpellSystem spells = await SessionTraceTestKit.InstallAuraAsync(host);
        uint lowA = await SessionTraceTestKit.LowGuidAsync(host, "Tracedalpha");
        uint lowB = await SessionTraceTestKit.LowGuidAsync(host, "Untracedbeta");
        await gm.CollectAsync();
        Assert.StartsWith("Session trace on for Tracedalpha", await SessionTraceTestKit.CommandAsync(gm, ".debug trace session Tracedalpha on"), StringComparison.Ordinal);

        await ExerciseAsync(host, clock, logs, a, "Tracedalpha", spells, traced: true);
        await ExerciseAsync(host, clock, logs, b, "Untracedbeta", spells, traced: false);
        await host.OnWorldAsync(() => 0); // everything B did has been handled on the world thread

        TraceLine[] lines = SessionTraceTestKit.Lines(logs);
        Assert.NotEmpty(lines);
        Assert.All(lines, l =>
        {
            Assert.Equal(lowA, l.Guid);
            Assert.Equal("Tracedalpha", l.Character);
            Assert.StartsWith("127.0.0.1:", l.Endpoint, StringComparison.Ordinal);
            Assert.DoesNotContain("Untracedbeta", l.Raw, StringComparison.Ordinal);
            Assert.DoesNotContain("Timelinegm", l.Raw, StringComparison.Ordinal);
        });
        Assert.DoesNotContain(lines, l => l.Guid == lowB);

        TraceLine[] mine = lines;
        Assert.Contains(mine, l => l.Event == "in" && l.Detail.StartsWith("CMSG_PING size=8", StringComparison.Ordinal) && l.Detail.Contains("seq=4660 latency=42ms", StringComparison.Ordinal));
        Assert.Contains(mine, l => l.Event == "in" && l.Detail.StartsWith("MSG_MOVE_HEARTBEAT", StringComparison.Ordinal));
        Assert.Contains(mine, l => l.Event == "in" && l.Detail.StartsWith("CMSG_REPOP_REQUEST", StringComparison.Ordinal));
        Assert.Contains(mine, l => l.Event == "move" && l.Detail.StartsWith("MSG_MOVE_HEARTBEAT", StringComparison.Ordinal)
            && l.Detail.Contains(" x=", StringComparison.Ordinal) && l.Detail.Contains(" y=", StringComparison.Ordinal) && l.Detail.Contains(" z=", StringComparison.Ordinal)
            && l.Detail.Contains(" flags=", StringComparison.Ordinal) && l.Detail.Contains(" clientTime=100100", StringComparison.Ordinal) // the mover's clock: 100000 + one 100 ms step
            && l.Detail.Contains(" clockOffsetMs=", StringComparison.Ordinal) && l.Detail.Contains(" drift=", StringComparison.Ordinal));
        TraceLine added = Assert.Single(mine, l => l.Event == "aura+");
        Assert.StartsWith($"spell={SessionTraceTestKit.AuraSpell} caster=", added.Detail, StringComparison.Ordinal);
        TraceLine removed = Assert.Single(mine, l => l.Event == "aura-");
        Assert.Equal($"spell={SessionTraceTestKit.AuraSpell} reason={AuraRemoveMode.Default}", removed.Detail);
        TraceLine death = Assert.Single(mine, l => l.Event == "death");
        Assert.Equal("killer=none", death.Detail);
        int died = Array.FindIndex(mine, l => l.Event == "died");
        int resurrected = Array.FindIndex(mine, l => l.Event == "resurrected");
        Assert.True(died >= 0 && resurrected > died, "died precedes resurrected");
        Assert.Contains(mine, l => l.Event == "state" && l.Detail.StartsWith("DeathState ", StringComparison.Ordinal) && l.Detail.Contains(" -> ", StringComparison.Ordinal));
        Assert.Contains(mine, l => l.Event == "state" && l.Detail.StartsWith("PlayerFlags ", StringComparison.Ordinal) && l.Detail.Contains("Ghost", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ATrace_ExpiresAfterItsMinutes_WritesTheExpiredLine_AndDetachesTheSession()
    {
        var logs = new CapturingLoggerProvider();
        var clock = new FakeTimeProvider();
        await using WorldTestHost host = SessionTraceTestKit.Start(clock, logs);
        await using WorldTestClient gm = await host.EnterWorldAsync("STEXP0", "Expirygm", AccountSecurity.GameMaster);
        await using WorldTestClient target = await host.EnterWorldAsync("STEXP1", "Expiryone");
        SessionTraceFeature feature = SessionTraceTestKit.FeatureOf(host);
        uint low = await SessionTraceTestKit.LowGuidAsync(host, "Expiryone");
        await gm.CollectAsync();
        Assert.StartsWith("Session trace on for Expiryone (guid", await SessionTraceTestKit.CommandAsync(gm, ".debug trace session Expiryone on 1"), StringComparison.Ordinal);

        // Just short of the minute the trace is still on, and the world has had many ticks to say otherwise.
        clock.Advance(TimeSpan.FromSeconds(59));
        await Task.Delay(100);
        Assert.True(await host.OnWorldAsync(() => feature.IsTraced(new ObjectGuid(low))));
        await target.SendAsync(WorldOpcode.CmsgPing, Ping);
        await target.ReadUntilAsync(WorldOpcode.SmsgPong);
        Assert.Single(SessionTraceTestKit.Lines(logs), l => l.Event == "in" && l.Detail.StartsWith("CMSG_PING", StringComparison.Ordinal));

        clock.Advance(TimeSpan.FromSeconds(2)); // 61 s in
        TraceLine expired = await SessionTraceTestKit.WaitForLineAsync(logs, l => l.Guid == low && l.Event == "trace" && l.Detail.StartsWith("expired lines=", StringComparison.Ordinal), "the expired line");
        Assert.Matches(@"^expired lines=\d+ dropped=\d+$", expired.Detail);

        Assert.Empty(await host.OnWorldAsync(() => feature.Active.ToArray()));
        Assert.False(await host.OnWorldAsync(() => feature.IsTraced(new ObjectGuid(low))));
        Assert.Null(SessionTraceTestKit.SessionOf(host, "Expiryone").SessionTrace);
        Assert.Equal("No session traces.", await SessionTraceTestKit.CommandAsync(gm, ".debug trace session list"));

        // Detached: a further ping reaches the pong and writes no line.
        int written = SessionTraceTestKit.Lines(logs).Length;
        await target.SendAsync(WorldOpcode.CmsgPing, Ping);
        await target.ReadUntilAsync(WorldOpcode.SmsgPong);
        Assert.Equal(written, SessionTraceTestKit.Lines(logs).Length);
    }

    [Fact]
    public async Task ADisconnect_WritesTheReason_KeepsTheEntry_AndTheRelogReattachesTheNewSession()
    {
        var logs = new CapturingLoggerProvider();
        var clock = new FakeTimeProvider();
        await using WorldTestHost host = SessionTraceTestKit.Start(clock, logs);
        await using WorldTestClient gm = await host.EnterWorldAsync("STDIS0", "Disconnectgm", AccountSecurity.GameMaster);
        WorldTestClient target = await host.EnterWorldAsync("STDIS1", "Dropone");
        SessionTraceFeature feature = SessionTraceTestKit.FeatureOf(host);
        uint low = await SessionTraceTestKit.LowGuidAsync(host, "Dropone");
        await gm.CollectAsync();
        Assert.StartsWith("Session trace on for Dropone (guid", await SessionTraceTestKit.CommandAsync(gm, ".debug trace session Dropone on"), StringComparison.Ordinal);
        Trace trace = await host.OnWorldAsync(() => Assert.Single(feature.Active));
        Assert.StartsWith("127.0.0.1:", trace.Endpoint, StringComparison.Ordinal);

        // The client drops the link (what a throttle or tamper tool does): the session closes with no kick reason.
        await target.DisposeAsync();
        TraceLine dropped = await SessionTraceTestKit.WaitForLineAsync(logs, l => l.Guid == low && l.Event == "disconnect", "the disconnect line");
        Assert.Equal("reason=connection closed", dropped.Detail);
        await SessionTraceTestKit.WaitForLineAsync(logs, l => l.Guid == low && l.Event == "logout", "the logout line");
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Dropone") is null, "the character left the world");

        // The registry keeps the entry, now offline.
        Assert.True(await host.OnWorldAsync(() => feature.IsTraced(new ObjectGuid(low))));
        Assert.Equal("offline", trace.Endpoint);
        Assert.Contains("offline", Assert.Single(await SessionTraceTestKit.CommandLinesAsync(gm, ".debug trace session list"), r => r.Contains("Dropone", StringComparison.Ordinal)), StringComparison.Ordinal);

        // Relog of the same character: the trace is still on and attaches the new session.
        Account account = (await host.Accounts.FindByUsernameAsync("STDIS1"))!;
        int characterId = (await host.Characters.GetByAccountAsync(account.Id)).Single().Id;
        await using WorldTestClient again = await host.ConnectAsync();
        await again.AuthenticateAsync("STDIS1", account.SessionKey!);
        await again.LoginAsync((ulong)characterId);
        await SessionTraceTestKit.WaitForLineAsync(logs, l => l.Guid == low && l.Event == "attached", "the attach line");
        Assert.StartsWith("127.0.0.1:", trace.Endpoint, StringComparison.Ordinal);
        Assert.Same(trace, SessionTraceTestKit.SessionOf(host, "Dropone").SessionTrace);
        await again.CollectAsync();
        await again.SendAsync(WorldOpcode.CmsgPing, Ping);
        await again.ReadUntilAsync(WorldOpcode.SmsgPong);
        TraceLine[] afterRelog = SessionTraceTestKit.Lines(logs).SkipWhile(l => l.Event != "attached").ToArray();
        Assert.Contains(afterRelog, l => l.Event == "in" && l.Detail.StartsWith("CMSG_PING", StringComparison.Ordinal));

        // A kick records its reason on the line.
        SessionTraceTestKit.SessionOf(host, "Dropone").Kick("session-trace-test-kick");
        TraceLine kicked = await SessionTraceTestKit.WaitForLineAsync(logs, l => l.Guid == low && l.Event == "disconnect" && l.Detail.Contains("session-trace-test-kick", StringComparison.Ordinal), "the kick line");
        Assert.Equal("reason=session-trace-test-kick", kicked.Detail);
    }
}

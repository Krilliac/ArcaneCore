using System.Collections.Concurrent;
using System.Net;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Kernel.Cluster.Auxiliary;
using ArcaneCore.Protocol;
using ArcaneCore.World.Cluster;
using ArcaneCore.World.Cluster.Auxiliary;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Warden;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Gateway.Tests;

/// <summary>
/// Warden on the gateway: the frame set's golden bytes (unchanged by the new frame), the WardenVerdict frame round trip, a verdict from a
/// gateway reaching the worker's verifier, a mismatched posture being refused, and an unusable module making the gateway not ready.
/// </summary>
public sealed class GatewayWardenTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private const string Secret = "warden-link-secret-0123456789";

    // ---- golden bytes of the frames that existed before WardenVerdict ----

    [Fact]
    public void Golden_Close()
        => Assert.Equal(Convert.FromHexString("0F08000000000000000900000006"),
            GatewaySessionWire.EncodeClose(8, 9, ClusterSessionDetachReason.WorkerClosed));

    [Fact]
    public void Golden_Open()
        => Assert.Equal(
            Convert.FromHexString("0B" + "6300000000000000" + "2A00000000000000" + "07000000" + "F3160000" + "02000000" + "4142" + "09000000" + "312E322E332E343A35"),
            GatewaySessionWire.EncodeOpen(99, new GatewaySessionOpenRequest(42, 7, "AB", 5875, "1.2.3.4:5")));

    [Fact]
    public void Golden_OpenReply()
        => Assert.Equal(
            Convert.FromHexString("0C" + "0500000000000000" + "01" + "0C" + "0300000000000000" + "00000000"),
            GatewaySessionWire.EncodeOpenReply(5, GatewaySessionOpenReply.Accept(3)));

    [Fact]
    public void Golden_ClientPacket()
        => Assert.Equal(
            Convert.FromHexString("0D" + "0100000000000000" + "02000000" + "0300000000000000" + "0400000000000000" + "0500000000000000" + "34120000" + "02000000" + "AABB"),
            GatewaySessionWire.EncodePacket(GatewayWorkerFrameType.SessionClientPacket, 1, 2, 3, 4, 5, 0x1234, [0xAA, 0xBB]));

    [Fact]
    public void Golden_LinkChallenge()
    {
        byte[] nonce = [.. Enumerable.Range(0, GatewaySessionWire.NonceBytes).Select(i => (byte)i)];
        Assert.Equal(new byte[] { 0x10 }.Concat(nonce).ToArray(), GatewaySessionWire.EncodeNonceFrame(GatewayWorkerFrameType.LinkChallenge, nonce));
    }

    // ---- the new frame ----

    private static readonly GatewayWardenPosture Posture = new(true, new string('a', 64), "enabled=1;action=1", 1);

    private static ClusterWardenVerdict Verdict(long generation = 3, long epoch = 1, int account = 7, ClusterWardenAction action = ClusterWardenAction.Kick, ushort check = 243, string reason = "failed scan 243")
        => new(generation, epoch, account, (ulong)account, reason, action, check);

    [Fact]
    public void WardenVerdict_FrameTypeIsTheLastValue_AndRoundTrips()
    {
        ClusterWardenVerdict verdict = Verdict();
        byte[] frame = GatewaySessionWire.EncodeWardenVerdict(verdict, Posture);
        Assert.Equal(18, frame[0]);
        Assert.Equal((byte)GatewayWorkerFrameType.WardenVerdict, frame[0]);

        (ClusterWardenVerdict decoded, GatewayWardenPosture posture) = GatewaySessionWire.DecodeWardenVerdict(frame.AsSpan(1));
        Assert.Equal(verdict, decoded);
        Assert.Equal(Posture, posture);
    }

    [Fact]
    public void WardenVerdict_ClampsAnOverlongReason_AndRefusesEveryTruncationAndABadAction()
    {
        string longReason = new string('x', 1000) + "é";
        byte[] frame = GatewaySessionWire.EncodeWardenVerdict(Verdict(reason: longReason), Posture);
        string decodedReason = GatewaySessionWire.DecodeWardenVerdict(frame.AsSpan(1)).Verdict.Reason;
        Assert.Equal(GatewaySessionWire.MaxTextBytes, decodedReason.Length);

        byte[] small = GatewaySessionWire.EncodeWardenVerdict(Verdict(), Posture);
        for (int length = 0; length < small.Length - 1; length++)
        {
            Assert.Throws<InvalidDataException>(() => GatewaySessionWire.DecodeWardenVerdict(small.AsSpan(1, length)));
        }

        byte[] badAction = (byte[])small.Clone();
        badAction[1 + 8 + 8 + 4 + 8 + 2] = 9; // the action byte
        Assert.Throws<InvalidDataException>(() => GatewaySessionWire.DecodeWardenVerdict(badAction.AsSpan(1)));
    }

    // ---- gateway verdict to worker verifier ----

    private static WardenOptions Options(WardenAction action = WardenAction.Kick) => new() { Enabled = true, Action = action };

    private static ClusterNodeManifest Manifest(ClusterRole role) => GatewayLinkManifest.Create(role, role.ToString(), Guid.NewGuid(), 1, ArcaneCore.World.Cluster.Gateway.GatewayManifestVersions.ContentRevisionFor(ArcaneCore.Data.Content.WorldDbContext.Schema.CurrentVersion), ArcaneCore.Data.Characters.CharacterDbContext.Schema.CurrentVersion, ArcaneCore.Data.Content.WorldDbContext.Schema.CurrentVersion);

    private sealed class StubSession : IGatewayRemoteSession
    {
        public void Start()
        {
        }

        public void Deliver(WorldOpcode opcode, byte[] payload)
        {
        }

        public void CloseFromGateway(ClusterSessionDetachReason reason)
        {
        }
    }

    private sealed class StubAcceptor : IGatewaySessionAcceptor
    {
        public ValueTask<GatewaySessionAcceptResult> OpenAsync(GatewaySessionOpenRequest request, IGatewaySessionOutput output, CancellationToken cancellationToken)
            => ValueTask.FromResult(GatewaySessionAcceptResult.Accepted(new StubSession()));
    }

    private sealed class Rig : IAsyncDisposable
    {
        public required GatewaySessionLinkHost Host { get; init; }

        public required GatewaySessionLinkClient Client { get; init; }

        public ConcurrentQueue<(long Generation, ClusterWardenVerdict Verdict)> Applied { get; } = new();

        public static async Task<Rig> StartAsync(GatewayWardenManifestVerifier verifier, CancellationToken token, GatewayWardenPosture? linkPosture = null, bool open = true)
        {
            ConcurrentQueue<(long, ClusterWardenVerdict)>? applied = null;
            var host = new GatewaySessionLinkHost(Manifest(ClusterRole.Worker), new StubAcceptor(), () => 1, Secret, new IPEndPoint(IPAddress.Loopback, 0),
                wardenVerifier: verifier, wardenVerdictAccepted: (generation, verdict) => applied!.Enqueue((generation, verdict)));
            host.Start();
            var client = new GatewaySessionLinkClient(Manifest(ClusterRole.Gateway), host.BoundEndPoint, Secret, wardenPosture: linkPosture ?? verifier.ToPosture());
            await client.ConnectAsync(token);
            var result = new Rig { Host = host, Client = client };
            applied = result.Applied;
            if (!open)
            {
                return result;
            }

            GatewaySessionOpenReply reply = await client.OpenAsync(new GatewaySessionOpenRequest(1, 7, "ACCOUNT", 5875, "198.51.100.4:4000"), new GatewaySessionLinkTests.RecordingSink(), token);
            Assert.True(reply.Accepted);
            return result;
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Host.DisposeAsync();
        }
    }

    private static GatewayWardenManifestVerifier WorkerVerifier(WardenOptions options) => new(GatewayWardenManifestVerifier.BuildManifest(1, options));

    [Fact]
    public async Task VerdictFromTheGateway_IsAcceptedByTheWorker_AndAReplayIsNot()
    {
        using var cts = new CancellationTokenSource(Deadline);
        var gateway = new GatewayWarden(1, Options(), NullLogger.Instance);
        Assert.True(gateway.IsReady);
        await using Rig rig = await Rig.StartAsync(WorkerVerifier(Options()), cts.Token);

        Assert.True(rig.Client.SendWardenVerdict(1, 243, ClusterWardenAction.Kick, "failed scan 243", gateway.Posture));
        await GatewaySessionLinkTests.WaitUntilAsync(() => rig.Host.AcceptedWardenVerdicts == 1, cts.Token);
        (long generation, ClusterWardenVerdict verdict) = Assert.Single(rig.Applied);
        Assert.Equal(1, generation);
        Assert.Equal(7, verdict.AccountId);
        Assert.Equal(1, verdict.OwnerEpoch);
        Assert.Equal(ClusterWardenAction.Kick, verdict.Action);
        Assert.Equal((ushort)243, verdict.CheckId);

        Assert.True(rig.Client.SendWardenVerdict(1, 243, ClusterWardenAction.Kick, "failed scan 243", gateway.Posture));
        await GatewaySessionLinkTests.WaitUntilAsync(() => rig.Host.RefusedWardenVerdicts == 1, cts.Token);
        Assert.Contains("duplicate Warden verdict", rig.Host.LastWardenRefusal, StringComparison.OrdinalIgnoreCase);
        Assert.Single(rig.Applied);
    }

    [Fact]
    public async Task VerdictFromAGatewayWithAMismatchedManifest_IsRefused()
    {
        using var cts = new CancellationTokenSource(Deadline);
        var gateway = new GatewayWarden(1, Options(WardenAction.Ban), NullLogger.Instance);
        await using Rig rig = await Rig.StartAsync(WorkerVerifier(Options(WardenAction.Kick)), cts.Token);

        Assert.True(rig.Client.SendWardenVerdict(1, 243, ClusterWardenAction.Kick, "failed scan 243", gateway.Posture));
        await GatewaySessionLinkTests.WaitUntilAsync(() => rig.Host.RefusedWardenVerdicts == 1, cts.Token);
        Assert.Contains("options mismatch", rig.Host.LastWardenRefusal, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(rig.Applied);
        Assert.Equal(0, rig.Host.AcceptedWardenVerdicts);
    }

    [Fact]
    public async Task GatewayWithAMismatchedPosture_IsRefusedAtAdmission_AndCarriesNoSession()
    {
        using var cts = new CancellationTokenSource(Deadline);
        var mismatched = new GatewayWarden(1, Options(WardenAction.Ban), NullLogger.Instance);
        await using Rig rig = await Rig.StartAsync(WorkerVerifier(Options(WardenAction.Kick)), cts.Token, mismatched.Posture, open: false);

        await GatewaySessionLinkTests.WaitUntilAsync(() => rig.Host.LastWardenRefusal is not null, cts.Token);
        Assert.Contains("options mismatch", rig.Host.LastWardenRefusal, StringComparison.OrdinalIgnoreCase);
        GatewaySessionOpenReply reply = await rig.Client.OpenAsync(new GatewaySessionOpenRequest(1, 7, "ACCOUNT", 5875, "198.51.100.4:4000"), new GatewaySessionLinkTests.RecordingSink(), cts.Token);
        Assert.False(reply.Accepted);
        await GatewaySessionLinkTests.WaitUntilAsync(() => rig.Host.AdmittedLinkCount == 0, cts.Token);
        Assert.Equal(0, rig.Host.SessionCount);
    }

    [Fact]
    public async Task EnabledWorkerAndADisabledGateway_IsRefusedAtAdmission()
    {
        using var cts = new CancellationTokenSource(Deadline);
        var disabled = new GatewayWarden(1, new WardenOptions(), NullLogger.Instance);
        await using Rig rig = await Rig.StartAsync(WorkerVerifier(Options()), cts.Token, disabled.Posture, open: false);

        await GatewaySessionLinkTests.WaitUntilAsync(() => rig.Host.LastWardenRefusal is not null, cts.Token);
        Assert.Contains("enabled mismatch", rig.Host.LastWardenRefusal, StringComparison.OrdinalIgnoreCase);
        GatewaySessionOpenReply reply = await rig.Client.OpenAsync(new GatewaySessionOpenRequest(1, 7, "ACCOUNT", 5875, "198.51.100.4:4000"), new GatewaySessionLinkTests.RecordingSink(), cts.Token);
        Assert.False(reply.Accepted);
        Assert.Equal(0, rig.Host.SessionCount);
    }

    [Fact]
    public async Task GatewayThatSendsNoPosture_CannotOpenASession_AgainstAWorkerWithAVerifier()
    {
        using var cts = new CancellationTokenSource(Deadline);
        GatewayWardenManifestVerifier verifier = WorkerVerifier(Options());
        var host = new GatewaySessionLinkHost(Manifest(ClusterRole.Worker), new StubAcceptor(), () => 1, Secret, new IPEndPoint(IPAddress.Loopback, 0), wardenVerifier: verifier);
        host.Start();
        await using (host)
        {
            await using var client = new GatewaySessionLinkClient(Manifest(ClusterRole.Gateway), host.BoundEndPoint, Secret);
            await client.ConnectAsync(cts.Token);
            GatewaySessionOpenReply reply = await client.OpenAsync(new GatewaySessionOpenRequest(1, 7, "ACCOUNT", 5875, "198.51.100.4:4000"), new GatewaySessionLinkTests.RecordingSink(), cts.Token);
            Assert.False(reply.Accepted);
            Assert.Equal(0, host.SessionCount);
        }
    }

    [Fact]
    public void WardenPostureFrame_HasAPinnedTypeAndGoldenBytes_AndRoundTrips()
    {
        Assert.Equal((byte)19, (byte)GatewayWorkerFrameType.WardenPosture);
        var posture = new GatewayWardenPosture(true, "ab", "x", 1);
        byte[] frame = GatewaySessionWire.EncodeWardenPosture(posture);
        Assert.Equal(
            [(byte)19, 1, 1, 0, 0, 0, 2, 0, 0, 0, (byte)'a', (byte)'b', 1, 0, 0, 0, (byte)'x'],
            frame);
        Assert.Equal(posture, GatewaySessionWire.DecodeWardenPosture(frame.AsSpan(1)));
    }

    [Fact]
    public async Task VerdictForAnUnknownOrClosedSession_IsNotSent()
    {
        using var cts = new CancellationTokenSource(Deadline);
        var gateway = new GatewayWarden(1, Options(), NullLogger.Instance);
        await using Rig rig = await Rig.StartAsync(WorkerVerifier(Options()), cts.Token);

        Assert.False(rig.Client.SendWardenVerdict(99, 1, ClusterWardenAction.Kick, "x", gateway.Posture)); // the client has no such session
        rig.Client.CloseSession(1, ClusterSessionDetachReason.ClientDisconnect);
        Assert.False(rig.Client.SendWardenVerdict(1, 1, ClusterWardenAction.Kick, "x", gateway.Posture));
        Assert.Empty(rig.Applied);
    }

    [Fact]
    public void GatewayAndWorkerDeriveTheSameManifest_FromTheSameOptions()
    {
        GatewayWardenManifest a = GatewayWardenManifestVerifier.BuildManifest(1, Options());
        GatewayWardenManifest b = GatewayWardenManifestVerifier.BuildManifest(1, Options());
        Assert.Equal(a.ModuleDigestHex, b.ModuleDigestHex);
        Assert.Equal(a.OptionsSummary, b.OptionsSummary);
        Assert.Equal(Convert.ToHexStringLower(WardenModuleProfile.ModuleSha256), a.ModuleDigestHex);
        Assert.NotEqual(a.OptionsSummary, GatewayWardenManifestVerifier.BuildManifest(1, Options(WardenAction.Ban)).OptionsSummary);
        Assert.True(new GatewayWardenManifestVerifier(a).VerifyPeerPosture(new GatewayWarden(1, Options(), NullLogger.Instance).Posture, out string? reason), reason);
    }

    // ---- readiness ----

    [Fact]
    public void EnabledWardenWithAnUnusableModule_MakesTheGatewayNotReady()
    {
        var emptyManifest = new GatewayWardenManifest(1, true, "abc", "x", GatewayWardenManifestVerifier.CurrentCheckRevision, []);
        var gateway = new GatewayWarden(emptyManifest, [], Options(), NullLogger.Instance);
        Assert.False(gateway.IsReady);
        Assert.Contains("no OS modules", gateway.NotReadyReason, StringComparison.OrdinalIgnoreCase);

        byte[] payload = [1, 2, 3];
        var corrupt = new GatewayWardenManifest(1, true, "abc", "x", 1,
            [new WardenModuleDescriptor("win", payload, new byte[16], new string('0', 64))]);
        var corruptGateway = new GatewayWarden(corrupt, [], Options(), NullLogger.Instance);
        Assert.False(corruptGateway.IsReady);
        Assert.Contains("SHA-256", corruptGateway.NotReadyReason, StringComparison.OrdinalIgnoreCase);

        var invalid = new GatewayWarden(1, new WardenOptions { Enabled = true, ScansPerRequest = 0 }, NullLogger.Instance);
        Assert.False(invalid.IsReady);
        Assert.Contains("Warden:ScansPerRequest", invalid.NotReadyReason, StringComparison.Ordinal);

        using var server = NewServer(gateway);
        Assert.False(server.IsWardenReady);
        Assert.Same(gateway, server.Warden);
    }

    [Fact]
    public void ReadyAndDisabledGateways_ReportReady_AndOnlyEnabledOnesStartASession()
    {
        var disabled = new GatewayWarden(1, new WardenOptions(), NullLogger.Instance);
        Assert.True(disabled.IsReady);
        Assert.False(disabled.IsEnabled);
        Assert.Null(disabled.Begin(5875, AccountSecurity.Player, "A", new byte[40], _ => { }, (_, _, _) => { }));
        using (NewServer(disabled))
        {
        }

        var enabled = new GatewayWarden(1, Options(), NullLogger.Instance);
        Assert.True(enabled.IsReady);
        Assert.True(enabled.IsEnabled);
        using var readyServer = NewServer(enabled);
        Assert.True(readyServer.IsWardenReady);

        var sent = new List<byte[]>();
        using (GatewayWardenSession? session = enabled.Begin(5875, AccountSecurity.Player, "A", new byte[40], body => sent.Add(body), (_, _, _) => { }))
        {
            Assert.NotNull(session);
            Assert.Single(sent); // MODULE_USE
            Assert.Equal(WardenState.AwaitingModuleStatus, session.Warden.State);
        }

        Assert.Null(enabled.Begin(5875, AccountSecurity.Moderator, "A", new byte[40], _ => { }, (_, _, _) => { })); // exempt staff
        Assert.Null(enabled.Begin(8606, AccountSecurity.Player, "A", new byte[40], _ => { }, (_, _, _) => { }));   // another build
        Assert.Null(enabled.Begin(5875, AccountSecurity.Player, "A", new byte[8], _ => { }, (_, _, _) => { }));    // not a 40-byte key
    }

    private static GatewayServer NewServer(GatewayWarden warden)
        => new(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), Microsoft.Extensions.Options.Options.Create(new GatewayOptions()),
            NullLoggerFactory.Instance, null, warden);
}

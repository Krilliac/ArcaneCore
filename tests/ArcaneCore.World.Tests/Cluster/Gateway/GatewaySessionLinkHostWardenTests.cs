using System.Net;
using System.Security.Cryptography;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Kernel.Cluster.Auxiliary;
using ArcaneCore.World.Cluster;
using ArcaneCore.World.Cluster.Auxiliary;
using ArcaneCore.World.Cluster.Gateway;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Gateway;

/// <summary>
/// The link host keys Warden verdicts by the admitted peer's (node id, incarnation) and retires an incarnation in the verifier
/// only when its last open link is gone and a different incarnation of the same node id has been admitted, on every close path.
/// </summary>
public sealed class GatewaySessionLinkHostWardenTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private const string Secret = "link-secret-0123456789";
    private const long OwnerEpoch = 1;

    private static GatewayWardenManifestVerifier NewVerifier()
    {
        byte[] payload = [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00];
        byte[] rc4Key = new byte[GatewayWardenManifestVerifier.RequiredRc4KeyLength];
        RandomNumberGenerator.Fill(rc4Key);
        string hash = Convert.ToHexStringLower(SHA256.HashData(payload));
        var manifest = new GatewayWardenManifest(1, true, hash, "enabled=1;action=kick", 12, [new WardenModuleDescriptor("Win", payload, rc4Key, hash)]);
        var verifier = new GatewayWardenManifestVerifier(manifest);
        Assert.True(verifier.IsGatewayWardenReady);
        return verifier;
    }

    private static ClusterNodeManifest Manifest(ClusterRole role, string nodeId, Guid incarnation) =>
        GatewayLinkManifest.Create(role, nodeId, incarnation, 1, "dev", 1, 1);

    private static ClusterWardenVerdict Verdict(ulong character, ushort checkId) =>
        new(1, OwnerEpoch, AccountId: 7, CharacterGuid: character, Reason: "MEM_CHECK failed", Action: ClusterWardenAction.Kick, CheckId: checkId);

    private static GatewaySessionLinkHost StartHost(GatewayWardenManifestVerifier? verifier)
    {
        var host = new GatewaySessionLinkHost(Manifest(ClusterRole.Worker, "worker", Guid.NewGuid()), new RefusingAcceptor(), () => OwnerEpoch, Secret,
            new IPEndPoint(IPAddress.Loopback, 0), wardenVerifier: verifier);
        host.Start();
        return host;
    }

    private static async Task<GatewaySessionLinkClient> ConnectAsync(GatewaySessionLinkHost host, string nodeId, Guid incarnation, int expectedOpen, CancellationToken token)
    {
        var client = new GatewaySessionLinkClient(Manifest(ClusterRole.Gateway, nodeId, incarnation), host.BoundEndPoint, Secret);
        await client.ConnectAsync(token);
        Assert.True(client.IsConnected);
        await WaitUntilAsync(() => host.OpenLinkCount(new ClusterWardenVerdictSource(nodeId, incarnation)) == expectedOpen, token);
        return client;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken token)
    {
        while (!condition())
        {
            await Task.Delay(10, token);
        }
    }

    [Fact]
    public async Task VerdictOnAnAdmittedLink_IsAcceptedOnce_AndTheReplayIsRefused()
    {
        using var cts = new CancellationTokenSource(Deadline);
        GatewayWardenManifestVerifier verifier = NewVerifier();
        await using GatewaySessionLinkHost host = StartHost(verifier);
        Guid incarnation = Guid.NewGuid();
        await using GatewaySessionLinkClient client = await ConnectAsync(host, "gateway-a", incarnation, 1, cts.Token);
        int linkId = Assert.Single(host.AdmittedLinkIds());

        ClusterWardenVerdict verdict = Verdict(1200UL, checkId: 243);
        Assert.True(host.TryAcceptWardenVerdict(linkId, verdict, 1, OwnerEpoch, out string? reason), reason);
        Assert.False(host.TryAcceptWardenVerdict(linkId, verdict, 1, OwnerEpoch, out string? replay));
        Assert.Contains("duplicate Warden verdict", replay, StringComparison.OrdinalIgnoreCase);

        // It was recorded against the link's own identity: the same verdict from that source is a duplicate in the verifier too.
        Assert.False(verifier.TryAcceptVerdict(verdict, new ClusterWardenVerdictSource("gateway-a", incarnation), 1, OwnerEpoch, out _));
        Assert.Equal(1, verifier.RetainedVerdictKeyCount);
    }

    [Fact]
    public async Task VerdictRouting_RefusesUnknownLinks_AndHostsWithoutAVerifier()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using GatewaySessionLinkHost noVerifier = StartHost(null);
        await using GatewaySessionLinkClient c1 = await ConnectAsync(noVerifier, "gateway-a", Guid.NewGuid(), 1, cts.Token);
        Assert.False(noVerifier.TryAcceptWardenVerdict(Assert.Single(noVerifier.AdmittedLinkIds()), Verdict(1UL, 1), 1, OwnerEpoch, out string? none));
        Assert.Contains("no Warden verdict verifier", none, StringComparison.OrdinalIgnoreCase);

        GatewayWardenManifestVerifier verifier = NewVerifier();
        await using GatewaySessionLinkHost host = StartHost(verifier);
        Assert.False(host.TryAcceptWardenVerdict(12345, Verdict(1UL, 1), 1, OwnerEpoch, out string? unknown));
        Assert.Contains("not an open admitted gateway link", unknown, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, verifier.RetainedVerdictKeyCount);
    }

    [Fact]
    public async Task ClosingTheLastLinkOfASupersededIncarnation_RetiresIt()
    {
        using var cts = new CancellationTokenSource(Deadline);
        GatewayWardenManifestVerifier verifier = NewVerifier();
        await using GatewaySessionLinkHost host = StartHost(verifier);
        Guid incarnation = Guid.NewGuid();
        var source = new ClusterWardenVerdictSource("gateway-a", incarnation);
        GatewaySessionLinkClient client = await ConnectAsync(host, "gateway-a", incarnation, 1, cts.Token);
        Assert.True(host.TryAcceptWardenVerdict(Assert.Single(host.AdmittedLinkIds()), Verdict(1200UL, 243), 1, OwnerEpoch, out string? reason), reason);
        await using GatewaySessionLinkClient newer = await ConnectAsync(host, "gateway-a", Guid.NewGuid(), 1, cts.Token);

        await client.DisposeAsync();
        await WaitUntilAsync(() => host.OpenLinkCount(source) == 0, cts.Token);

        Assert.False(verifier.TryAcceptVerdict(Verdict(1200UL, 244), source, 1, OwnerEpoch, out string? retired));
        Assert.Contains("retired gateway incarnation", retired, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, verifier.RetainedVerdictKeyCount);
    }

    [Fact]
    public async Task ASupersededIncarnationWithNoOpenLink_IsRetiredWhenTheNewerOneIsAdmitted()
    {
        using var cts = new CancellationTokenSource(Deadline);
        GatewayWardenManifestVerifier verifier = NewVerifier();
        await using GatewaySessionLinkHost host = StartHost(verifier);
        Guid incarnation = Guid.NewGuid();
        var source = new ClusterWardenVerdictSource("gateway-a", incarnation);
        GatewaySessionLinkClient client = await ConnectAsync(host, "gateway-a", incarnation, 1, cts.Token);
        await client.DisposeAsync();
        await WaitUntilAsync(() => host.OpenLinkCount(source) == 0, cts.Token);
        Assert.True(verifier.TryAcceptVerdict(Verdict(1200UL, 243), source, 1, OwnerEpoch, out string? stillLive), stillLive);

        await using GatewaySessionLinkClient newer = await ConnectAsync(host, "gateway-a", Guid.NewGuid(), 1, cts.Token);

        Assert.False(verifier.TryAcceptVerdict(Verdict(1200UL, 244), source, 1, OwnerEpoch, out string? retired));
        Assert.Contains("retired gateway incarnation", retired, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReconnectingWithTheSameIncarnation_KeepsVerdictsLive_AndReplaysStayRefused()
    {
        using var cts = new CancellationTokenSource(Deadline);
        GatewayWardenManifestVerifier verifier = NewVerifier();
        await using GatewaySessionLinkHost host = StartHost(verifier);
        Guid incarnation = Guid.NewGuid();
        var source = new ClusterWardenVerdictSource("gateway-a", incarnation);
        GatewaySessionLinkClient first = await ConnectAsync(host, "gateway-a", incarnation, 1, cts.Token);
        ClusterWardenVerdict verdict = Verdict(1200UL, 243);
        Assert.True(host.TryAcceptWardenVerdict(Assert.Single(host.AdmittedLinkIds()), verdict, 1, OwnerEpoch, out string? reason), reason);

        await first.DisposeAsync();
        await WaitUntilAsync(() => host.OpenLinkCount(source) == 0, cts.Token);
        await using GatewaySessionLinkClient second = await ConnectAsync(host, "gateway-a", incarnation, 1, cts.Token);
        int linkId = Assert.Single(host.AdmittedLinkIds());

        Assert.True(host.TryAcceptWardenVerdict(linkId, Verdict(1200UL, 244), 1, OwnerEpoch, out string? fresh), fresh);
        Assert.False(host.TryAcceptWardenVerdict(linkId, verdict, 1, OwnerEpoch, out string? replay));
        Assert.Contains("duplicate Warden verdict", replay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClosingOneOfTwoLinks_DoesNotRetire_AndClosingTheSecondDoesOnceSuperseded()
    {
        using var cts = new CancellationTokenSource(Deadline);
        GatewayWardenManifestVerifier verifier = NewVerifier();
        await using GatewaySessionLinkHost host = StartHost(verifier);
        Guid incarnation = Guid.NewGuid();
        var source = new ClusterWardenVerdictSource("gateway-a", incarnation);
        GatewaySessionLinkClient first = await ConnectAsync(host, "gateway-a", incarnation, 1, cts.Token);
        GatewaySessionLinkClient second = await ConnectAsync(host, "gateway-a", incarnation, 2, cts.Token);
        await using GatewaySessionLinkClient newer = await ConnectAsync(host, "gateway-a", Guid.NewGuid(), 1, cts.Token);

        await first.DisposeAsync();
        await WaitUntilAsync(() => host.OpenLinkCount(source) == 1, cts.Token);

        Assert.True(verifier.TryAcceptVerdict(Verdict(1200UL, 243), source, 1, OwnerEpoch, out string? reason), reason);

        await second.DisposeAsync();
        await WaitUntilAsync(() => host.OpenLinkCount(source) == 0, cts.Token);
        Assert.False(verifier.TryAcceptVerdict(Verdict(1200UL, 245), source, 1, OwnerEpoch, out string? retired));
        Assert.Contains("retired gateway incarnation", retired, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ASupersededIncarnationReconnecting_NeverDemotesTheLiveOne()
    {
        using var cts = new CancellationTokenSource(Deadline);
        GatewayWardenManifestVerifier verifier = NewVerifier();
        await using GatewaySessionLinkHost host = StartHost(verifier);
        Guid inc1 = Guid.NewGuid();
        Guid inc2 = Guid.NewGuid();
        var source1 = new ClusterWardenVerdictSource("gateway-a", inc1);
        var source2 = new ClusterWardenVerdictSource("gateway-a", inc2);
        GatewaySessionLinkClient first1 = await ConnectAsync(host, "gateway-a", inc1, 1, cts.Token);
        GatewaySessionLinkClient link2 = await ConnectAsync(host, "gateway-a", inc2, 1, cts.Token);

        // inc1 (draining) drops its link and reconnects while inc2 is live.
        await first1.DisposeAsync();
        await WaitUntilAsync(() => host.OpenLinkCount(source1) == 0, cts.Token);
        await using GatewaySessionLinkClient again1 = await ConnectAsync(host, "gateway-a", inc1, 1, cts.Token);

        // inc2 loses all links, then reconnects: it must still be the live incarnation.
        await link2.DisposeAsync();
        await WaitUntilAsync(() => host.OpenLinkCount(source2) == 0, cts.Token);
        Assert.True(verifier.TryAcceptVerdict(Verdict(1200UL, 243), source2, 1, OwnerEpoch, out string? whileClosed), whileClosed);
        await using GatewaySessionLinkClient again2 = await ConnectAsync(host, "gateway-a", inc2, 1, cts.Token);

        int link2Id = host.AdmittedLinkIds().Max();
        Assert.True(host.TryAcceptWardenVerdict(link2Id, Verdict(1200UL, 244), 1, OwnerEpoch, out string? fresh), fresh);
        Assert.False(host.TryAcceptWardenVerdict(link2Id, Verdict(1200UL, 244), 1, OwnerEpoch, out string? replay));
        Assert.Contains("duplicate Warden verdict", replay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ANewIncarnationOfTheSameNodeId_NeverRetiresTheOldOneWhileItsLinksAreOpen()
    {
        using var cts = new CancellationTokenSource(Deadline);
        GatewayWardenManifestVerifier verifier = NewVerifier();
        await using GatewaySessionLinkHost host = StartHost(verifier);
        Guid oldIncarnation = Guid.NewGuid();
        Guid newIncarnation = Guid.NewGuid();
        var oldSource = new ClusterWardenVerdictSource("gateway-a", oldIncarnation);
        var newSource = new ClusterWardenVerdictSource("gateway-a", newIncarnation);
        GatewaySessionLinkClient oldClient = await ConnectAsync(host, "gateway-a", oldIncarnation, 1, cts.Token);
        GatewaySessionLinkClient newClient = await ConnectAsync(host, "gateway-a", newIncarnation, 1, cts.Token);
        int oldLink = host.AdmittedLinkIds().Min(); // the old incarnation connected first, so it holds the lower link id

        // Closing the newest incarnation's only link retires nothing: it may reconnect, and the old one is fully live.
        await newClient.DisposeAsync();
        await WaitUntilAsync(() => host.OpenLinkCount(newSource) == 0, cts.Token);
        Assert.Equal(1, host.OpenLinkCount(oldSource));
        Assert.Equal(oldLink, Assert.Single(host.AdmittedLinkIds()));

        ClusterWardenVerdict verdict = Verdict(1200UL, 243);
        Assert.True(host.TryAcceptWardenVerdict(oldLink, verdict, 1, OwnerEpoch, out string? reason), reason);
        Assert.False(host.TryAcceptWardenVerdict(oldLink, verdict, 1, OwnerEpoch, out string? replay));
        Assert.Contains("duplicate Warden verdict", replay, StringComparison.OrdinalIgnoreCase);
        Assert.True(verifier.TryAcceptVerdict(Verdict(1200UL, 244), newSource, 1, OwnerEpoch, out string? newLive), newLive);

        // Only when the superseded old incarnation's last link goes is it retired.
        await oldClient.DisposeAsync();
        await WaitUntilAsync(() => host.OpenLinkCount(oldSource) == 0, cts.Token);
        Assert.False(verifier.TryAcceptVerdict(Verdict(1200UL, 245), oldSource, 1, OwnerEpoch, out string? retired));
        Assert.Contains("retired gateway incarnation", retired, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RefusingAcceptor : IGatewaySessionAcceptor
    {
        public ValueTask<GatewaySessionAcceptResult> OpenAsync(GatewaySessionOpenRequest request, IGatewaySessionOutput output, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GatewaySessionAcceptResult.Refused(ArcaneCore.Protocol.AuthResponseCode.Failed, "not used"));
    }
}

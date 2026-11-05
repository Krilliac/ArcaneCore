using ArcaneCore.Game;
using ArcaneCore.Game.AntiCheat;
using ArcaneCore.Game.Diagnostics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.AntiCheat;
using ArcaneCore.World.AntiCheat.PacketHeuristics;
using ArcaneCore.World.Gm.Diagnostics;
using ArcaneCore.World.Net;
using ArcaneCore.World.Tests.PacketHeuristics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.AntiCheat;

/// <summary>
/// The GM stream's <c>anticheat</c> channel: every <see cref="AntiCheatFeature"/> finding (verdict None included) and every packet-heuristics
/// ledger report reaches a GM following the channel. The world's sink is replaced by a private <see cref="GmStream"/> the test drains itself,
/// so the feature's tick never delivers (and so never consumes) the lines under test.
/// </summary>
public sealed class AntiCheatGmStreamTests
{
    private static readonly ObjectGuid Watcher = new(900_001);

    private static List<string> Drain(GmStream stream)
    {
        var lines = new List<string>();
        stream.Drain(l => lines.Add(l.Text));
        return lines;
    }

    private static GmStream PrivateStream(WorldTestHost host, GmStreamScope scope = GmStreamScope.All)
    {
        var stream = new GmStream(new GmStreamOptions { MaxLinesPerSecond = 100 });
        stream.Subscribe(Watcher, GmStreamChannels.AntiCheat, scope);
        host.World.GmTrace = stream;
        return stream;
    }

    [Fact]
    public async Task AFinding_IsTracedWithTypeDetailWeightScoreAndVerdict_AsTheScoreClimbs()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        AntiCheatFeature feature = host.WorldServices.GetRequiredService<AntiCheatFeature>();
        await host.OnWorldAsync(() => feature.ApplyOptions(new AntiCheatOptions { Action = AntiCheatAction.GmAlert, DecayPerSecond = 0 }));
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsTestKit.EnterClientAsync(host, "ACSTREAM", "Acstream");
        await using (client)
        {
            Player player = session.Player!;
            GmStream stream = PrivateStream(host);

            await host.OnWorldAsync(() => feature.OnRejected(session, player, AntiCheatViolation.Interact, 15f, "game object use far out of range"));
            await host.OnWorldAsync(() => feature.OnRejected(session, player, AntiCheatViolation.Interact, 20f, "second"));

            List<string> lines = Drain(stream);
            Assert.Equal(2, lines.Count);
            string prefix = $"[anticheat] (Player:{player.Guid.Low}) account {session.AccountId}: ";
            Assert.StartsWith(prefix + "Interact 'game object use far out of range' weight 15 score 15 -> Log at map 0 (", lines[0], StringComparison.Ordinal);
            Assert.StartsWith(prefix + "Interact 'second' weight 20 score 35 -> GmAlert at map 0 (", lines[1], StringComparison.Ordinal);
            Assert.EndsWith(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"({player.X:0.0}, {player.Y:0.0}, {player.Z:0.0})"), lines[0], StringComparison.Ordinal);
            Assert.All(lines, l => Assert.DoesNotContain("Acstream", l, StringComparison.OrdinalIgnoreCase)); // the player's name is never in the line
        }
    }

    [Fact]
    public async Task AFindingWithVerdictNone_IsStillTraced_ButAnExemptAccountIsNot()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        AntiCheatFeature feature = host.WorldServices.GetRequiredService<AntiCheatFeature>();
        await host.OnWorldAsync(() => feature.ApplyOptions(new AntiCheatOptions { Action = AntiCheatAction.None }));
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsTestKit.EnterClientAsync(host, "ACNONE", "Acnone");
        (WorldTestClient staff, WorldSession staffSession) = await PacketHeuristicsTestKit.EnterClientAsync(host, "ACSTAFF", "Acstaff", AccountSecurity.GameMaster);
        await using (client)
        await using (staff)
        {
            GmStream stream = PrivateStream(host);

            await host.OnWorldAsync(() => feature.OnRejected(session, session.Player!, AntiCheatViolation.Interact, 5f, "x"));
            await host.OnWorldAsync(() => feature.OnRejected(staffSession, staffSession.Player!, AntiCheatViolation.Interact, 5f, "staff"));

            string line = Assert.Single(Drain(stream));
            Assert.Contains($"(Player:{session.Player!.Guid.Low}) account {session.AccountId}: ", line, StringComparison.Ordinal);
            Assert.DoesNotContain("Acnone", line, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Interact 'x' weight 5 score 5 -> None at map 0", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AFinding_IsPlacedAtThePlayer_SoATargetScopeOnTheSelectionMatches_AndAnotherSelectionDoesNot()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        AntiCheatFeature feature = host.WorldServices.GetRequiredService<AntiCheatFeature>();
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsTestKit.EnterClientAsync(host, "ACTARGET", "Actarget");
        await using (client)
        {
            Player player = session.Player!;
            var stream = new GmStream();
            var other = new ObjectGuid(900_002);
            stream.Subscribe(Watcher, GmStreamChannels.AntiCheat, GmStreamScope.Target);
            stream.Subscribe(other, GmStreamChannels.AntiCheat, GmStreamScope.Target);
            (uint map, uint instance, float x, float y, float z) = await host.OnWorldAsync(() => (player.Map!.MapId, player.Map.InstanceId, player.X, player.Y, player.Z));
            stream.RefreshViewers(g => g == Watcher
                ? new GmStreamViewer(player.Guid, map, instance, x + 10, y, z, 100f)
                : new GmStreamViewer(new ObjectGuid(12345), map, instance, x, y, z, 100f));
            host.World.GmTrace = stream;

            await host.OnWorldAsync(() => feature.OnRejected(session, player, AntiCheatViolation.Interact, 15f, "target"));

            var lines = new List<GmStreamLine>();
            stream.Drain(lines.Add);
            Assert.Equal(Watcher, Assert.Single(lines).Gm);
        }
    }

    [Fact]
    public async Task APacketHeuristicsReport_IsTracedWithCodeAccountTextAndSeverity_AndNoName()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        (PacketHeuristicsFeature feature, AnomalyLedger ledger) = PacketHeuristicsTestKit.Enable(host);
        GmStream stream = PrivateStream(host);
        byte[] key = await host.AddAccountAsync("PHSTREAM");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("PHSTREAM", key);
        await client.CreateCharacterAsync("Phstream");
        using AnomalyCapture capture = ledger.Capture();

        await client.SendAsync(WorldOpcode.CmsgAttackswing, new byte[8]);
        await WorldTestHost.WaitForAsync(() => feature.Count(PacketCheck.WrongState, PacketTraffic.Player) >= 1, "the wrong-state finding");

        AnomalyNotice notice = Assert.Single(capture.Notices, n => n.Key == "packet/" + PacketChecks.Code(PacketCheck.WrongState));
        Account account = (await host.Accounts.FindByUsernameAsync("PHSTREAM"))!;
        string line = Assert.Single(Drain(stream));
        // The session has no player yet (a character was created, not entered), so the account id alone identifies it.
        Assert.StartsWith($"[anticheat] packet {PacketChecks.Code(PacketCheck.WrongState)} account {account.Id}: ", line, StringComparison.Ordinal);
        Assert.DoesNotContain("(Player:", line, StringComparison.Ordinal);
        Assert.Contains("CMSG_ATTACKSWING", line, StringComparison.Ordinal);
        Assert.EndsWith($": {notice.Detail} [Warning]", line, StringComparison.Ordinal);
        Assert.DoesNotContain("PHSTREAM", line, StringComparison.OrdinalIgnoreCase); // no account name either
        Assert.DoesNotContain("Phstream", line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThePacketLine_CarriesGuidAndAccount_WhenAPlayerExists_AndTheAccountAlone_WhenNot()
    {
        Assert.Equal("packet wrong-state (Player:77) account 5: text [Warning]",
            PacketHeuristicsFeature.FormatPacketLine("wrong-state", new ObjectGuid(77), 5, "text", AnomalySeverity.Warning));
        Assert.Equal("packet wrong-state account 5: text [Warning]",
            PacketHeuristicsFeature.FormatPacketLine("wrong-state", default, 5, "text", AnomalySeverity.Warning));
    }

    [Fact]
    public async Task APacketHeuristicsReport_HasNoPlace_SoItReachesOnlyAnAllScope()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        (PacketHeuristicsFeature feature, _) = PacketHeuristicsTestKit.Enable(host);
        var stream = new GmStream();
        var near = new ObjectGuid(900_003);
        stream.Subscribe(Watcher, GmStreamChannels.AntiCheat, GmStreamScope.All);
        stream.Subscribe(near, GmStreamChannels.AntiCheat, GmStreamScope.Near);
        stream.RefreshViewers(_ => new GmStreamViewer(default, 0, 0, 0, 0, 0, 100_000f));
        host.World.GmTrace = stream;
        byte[] key = await host.AddAccountAsync("PHPLACE");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("PHPLACE", key);
        await client.CreateCharacterAsync("Phplace");

        await client.SendAsync(WorldOpcode.CmsgAttackswing, new byte[8]);
        await WorldTestHost.WaitForAsync(() => feature.Count(PacketCheck.WrongState, PacketTraffic.Player) >= 1, "the wrong-state finding");

        var lines = new List<GmStreamLine>();
        stream.Drain(lines.Add);
        Assert.Equal(Watcher, Assert.Single(lines).Gm);
    }
}

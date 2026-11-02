using System.Buffers.Binary;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Cluster.E2E.Tests;

/// <summary>
/// The one-worker baseline over real processes: the mock client logs on at the realm daemon, then talks only to the gateway daemon, which links
/// to one fenced world worker. All three run as subprocesses over PostgreSQL (see <see cref="ClusterProcessFixture"/>); the facts report
/// Skipped, with the variable to set, when <c>ARCANECORE_TEST_POSTGRES</c> is not set.
/// </summary>
public sealed class BaselineScenarioTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(4);

    [PostgresClusterFact]
    public async Task OneWorker_LoginCreateEnterMoveChatLogout_ThroughTheGateway()
    {
        using var cts = new CancellationTokenSource(Deadline);
        CancellationToken ct = cts.Token;
        ClusterProcessFixture cluster = await ClusterProcessFixture.StartAsync(null, output, ct);
        try
        {
            Assert.Single(cluster.Workers);
            await RunScenarioAsync(cluster, ct);
        }
        finally
        {
            // Throws when any launched process is still alive afterwards: the fixture's no-orphans assertion.
            await cluster.DisposeAsync();
        }

        Assert.Empty(cluster.LiveProcessIds());
    }

    private static async Task RunScenarioAsync(ClusterProcessFixture cluster, CancellationToken ct)
    {
        const string name = "Clusterhero";
        await using WorldClient client = await cluster.ConnectGatewayClientAsync(ct);
        var session = new ScenarioConnection(client);

        await session.CreateCharacterAsync(name, ct);
        MockCharacter character = Assert.Single(await session.EnumerateAsync(ct));
        Assert.Equal(name, character.Name, ignoreCase: true);

        MockLogin login = await session.LoginAsync(character.Guid, ct);
        Assert.Equal(character.Guid, login.Self.Guid);

        // Move: start, heartbeat, stop (the replay test's sequence), then a ping as a barrier: the session is still served after the movement packets.
        MockLocation at = login.Location;
        await session.SendAsync(WorldOpcode.MsgMoveStartForward, Movement(1, at, 0, 1000), ct);
        await session.SendAsync(WorldOpcode.MsgMoveHeartbeat, Movement(1, at, 1, 1500), ct);
        await session.SendAsync(WorldOpcode.MsgMoveStop, Movement(0, at, 2, 2000), ct);
        const uint sequence = 0xC1A5;
        await session.SendAsync(WorldOpcode.CmsgPing, ScenarioWire.Ping(sequence, 0), ct);
        byte[] pong = await session.ReadUntilAsync(WorldOpcode.SmsgPong, ct);
        Assert.Equal(sequence, BinaryPrimitives.ReadUInt32LittleEndian(pong));

        // Chat: a say is broadcast in range including the sender (ChatHandlers, includeSelf), so it comes back through the link and the gateway.
        const string text = "cluster e2e hello";
        await session.SendAsync(WorldOpcode.CmsgMessagechat, LiveSession.SayPacket(text), ct);
        byte[] chat = await session.ReadUntilAsync(WorldOpcode.SmsgMessagechat, ct);
        string? line = LiveSession.DecodePlayerLine(chat);
        Assert.NotNull(line);
        Assert.StartsWith("say from ", line);
        Assert.EndsWith(": " + text, line);

        await session.LogoutAsync(ct);
    }

    private static byte[] Movement(uint flags, MockLocation at, float offsetX, uint clientTime)
    {
        byte[] body = StartingZoneProbe.Movement(at.X + offsetX, at.Y, at.Z, at.Orientation, clientTime);
        BinaryPrimitives.WriteUInt32LittleEndian(body, flags);
        return body;
    }
}

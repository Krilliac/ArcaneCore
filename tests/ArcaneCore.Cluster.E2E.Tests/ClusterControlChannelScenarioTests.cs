using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Cluster.E2E.Tests;

/// <summary>
/// The cross-process control channel (design _wf/handoff-control-DESIGN.md, commit C16) on real processes and PostgreSQL: the real gateway, the realm and fenced Worker-scope worlds as
/// subprocesses, with the opt-in keys on (<see cref="ClusterOptions.Transfers"/>, <see cref="ClusterOptions.StandbyWorkerIndex"/>). The layout is the one
/// <c>tools/ops/live/start-cluster.ps1 -Topology Split -Standby -Transfers</c> starts, reduced to two workers. Skipped without <c>ARCANECORE_TEST_POSTGRES</c>.
/// <para>
/// Everything is driven as a player would: a mock client types the real commands (<c>.go xyz</c>, <c>.cluster restart</c>) and answers the client half of the far-teleport sequence. What the scenarios
/// check is what a client sees on the wire and what the journal in the characters database says; nothing here calls a participant directly.
/// </para>
/// <para>Thread affinity: none; each fact owns its cluster and client and disposes them.</para>
/// </summary>
public sealed class ClusterControlChannelScenarioTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(6);

    // Kalimdor (map 1) is owned by the second worker, the Eastern Kingdoms (map 0, where a new character starts) by the first, which also takes the logins.
    private static readonly IReadOnlyList<int[]> TwoWorkerMaps = [[0], [1]];

    // A point in Kalimdor, as the in-process far-teleport tests use (GatewayCrossWorkerTeleportTests).
    private const string KalimdorPoint = "-1196.22 29.0 176.949 1";

    private static ClusterOptions Options(bool transfers, bool standby, int? lateWorker = null) => new()
    {
        WorkerCount = 2,
        WorkerMaps = TwoWorkerMaps,
        LoginWorkerIndex = 0,
        Security = AccountSecurity.Administrator, // .go xyz needs Moderator, .cluster restart Administrator
        Transfers = transfers,
        StandbyWorkerIndex = standby ? 0 : null,
        LateWorkerIndex = lateWorker,
    };

    [PostgresClusterFact]
    public async Task ACrossWorkerTeleport_MovesThePlayerToTheOwnerOfTheMap_AndTheJournalSaysCompleted()
    {
        using var cts = new CancellationTokenSource(Deadline);
        CancellationToken ct = cts.Token;
        await using ClusterProcessFixture cluster = await ClusterProcessFixture.StartAsync(Options(transfers: true, standby: false), output, ct);
        Assert.Null(cluster.Standby);

        await using WorldClient client = await cluster.ConnectGatewayClientAsync(ct);
        var session = new ScenarioConnection(client);
        await session.CreateCharacterAsync("Telecross", ct);
        MockCharacter character = Assert.Single(await session.EnumerateAsync(ct));
        MockLogin login = await session.LoginAsync(character.Guid, ct);
        Assert.Contains((int)login.Location.Map, cluster.Workers[0].OwnedMaps); // in the world on the Eastern Kingdoms worker

        await SayAsync(session, ".go xyz " + KalimdorPoint, ct);

        // The retail far-teleport sequence, sent by the Kalimdor worker: loading screen for map 1, then the new world, and the client acknowledges the world port.
        byte[] pending = await ReadUntilOpcodeAsync(session, WorldOpcode.SmsgTransferPending, ct);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(pending));
        byte[] newWorld = await ReadUntilOpcodeAsync(session, WorldOpcode.SmsgNewWorld, ct);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(newWorld));
        await session.SendAsync(WorldOpcode.MsgMoveWorldportAck, [], ct);

        // The transfer finishes: the journal says Completed, and nothing was aborted.
        string completed = Count("cluster_transfer_decision", "state = " + (int)PlayerTransferDecisionState.Completed);
        await WaitForCountAsync(cluster, completed, expected: 1, ct);
        Assert.Equal(0, await cluster.CharactersScalarAsync(Count("cluster_transfer_decision", "state = " + (int)PlayerTransferDecisionState.Aborted), ct));
        Assert.Equal(0, await cluster.CharactersScalarAsync(Count("cluster_transfer_decision", "state = " + (int)PlayerTransferDecisionState.PreCommit), ct));
        Assert.Equal(0, await cluster.CharactersScalarAsync(Count("cluster_transfer_decision", "state = " + (int)PlayerTransferDecisionState.Committed), ct));

        // The player moved, not just the reply: the one Completed journal row is this character's transfer from the Eastern Kingdoms worker to the Kalimdor worker, and the
        // character's transfer ownership now sits in the Kalimdor worker's scope (shard = node id), so the Eastern Kingdoms worker no longer owns the player.
        string eastern = SqlText(cluster.Workers[0].NodeId);
        string kalimdor = SqlText(cluster.Workers[1].NodeId);
        string guid = character.Guid.ToString(CultureInfo.InvariantCulture);
        Assert.Equal(1, await cluster.CharactersScalarAsync(
            Count("cluster_transfer_decision", $"character_guid = {guid} AND source_worker = {eastern} AND target_worker = {kalimdor} AND target_map = 1 AND state = {(int)PlayerTransferDecisionState.Completed}"), ct));
        Assert.Equal(0, await cluster.CharactersScalarAsync(Count("cluster_transfer_decision", $"character_guid = {guid} AND state <> {(int)PlayerTransferDecisionState.Completed}"), ct));
        Assert.Equal(1, await cluster.CharactersScalarAsync(Count("cluster_transfer_owner", $"character_guid = {guid} AND scope = {SqlText("worker:" + cluster.Workers[1].NodeId)} AND active_transfer IS NULL"), ct));
        Assert.Equal(0, await cluster.CharactersScalarAsync(Count("cluster_transfer_owner", $"character_guid = {guid} AND scope = {SqlText("worker:" + cluster.Workers[0].NodeId)}"), ct));

        // The session is alive on the target: it answers a command (the Kalimdor worker's reply reaches the same client).
        await SayAsync(session, ".cluster handoff status", ct);
        string reply = await ReadSystemLineAsync(session, line => line.Contains("handoff", StringComparison.OrdinalIgnoreCase), ct);
        Assert.False(string.IsNullOrWhiteSpace(reply));
        Assert.True(cluster.IsWorkerRunning(0));
        Assert.True(cluster.IsWorkerRunning(1));
    }

    [PostgresClusterFact]
    public async Task ClusterRestart_HandsTheSlotOffToItsStandby_TheVerdictReachesTheSameClient_AndTheJournalSaysRetired()
    {
        using var cts = new CancellationTokenSource(Deadline);
        CancellationToken ct = cts.Token;
        await using ClusterProcessFixture cluster = await ClusterProcessFixture.StartAsync(Options(transfers: false, standby: true), output, ct);
        ClusterStandbyInfo standby = Assert.IsType<ClusterStandbyInfo>(cluster.Standby);
        Assert.Equal(0, standby.ForWorkerIndex);
        Assert.NotEqual(cluster.Workers[0].NodeId, standby.NodeId);
        Assert.True(cluster.IsStandbyRunning);

        await using WorldClient client = await cluster.ConnectGatewayClientAsync(ct);
        var session = new ScenarioConnection(client);
        await session.CreateCharacterAsync("Restartee", ct);
        MockCharacter character = Assert.Single(await session.EnumerateAsync(ct));
        await session.LoginAsync(character.Guid, ct);

        string worker = cluster.Workers[0].NodeId;
        await SayAsync(session, ".cluster restart " + worker, ct);

        // "Started" arrives at once, with the operation id the journal knows the handoff under.
        string started = await ReadSystemLineAsync(session, line => line.Contains("restart", StringComparison.OrdinalIgnoreCase), ct);
        Match id = Regex.Match(started, "operation ([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})");
        Assert.True(id.Success, "the restart was not started: " + started);
        Assert.Contains("started", started, StringComparison.Ordinal);
        string operation = id.Groups[1].Value;

        // The verdict comes later, to the same client, now carried by the promoted standby. No supervisor runs in this cluster, so the relaunch of the retired source is not queued; the handoff itself is complete.
        string verdict = await ReadSystemLineAsync(session, line => line.Contains("handed off (sessions kept)", StringComparison.Ordinal), ct);
        Assert.Contains(worker, verdict, StringComparison.Ordinal);

        // The journal: one handoff, retired and completed, from the active process to the standby.
        string retired = Count("cluster_world_handoff", $"stage = {(int)WorldHandoffStage.Retired} AND state = {(int)PlayerTransferDecisionState.Completed}");
        await WaitForCountAsync(cluster, retired, expected: 1, ct);
        Assert.Equal(0, await cluster.CharactersScalarAsync(Count("cluster_world_handoff", "state = " + (int)PlayerTransferDecisionState.Aborted), ct));

        // The same session is alive on the new owner: it reads the journal row over the unchanged command.
        await SayAsync(session, ".cluster handoff status " + operation, ct);
        string status = await ReadSystemLineAsync(session, line => line.Contains(operation, StringComparison.OrdinalIgnoreCase), ct);
        Assert.Contains("stage=Retired", status, StringComparison.Ordinal);
        Assert.Contains("decision=Completed", status, StringComparison.Ordinal);
        Assert.Contains(standby.NodeId, status, StringComparison.Ordinal);

        Assert.True(cluster.IsStandbyRunning);
        Assert.True(cluster.IsWorkerRunning(1));
    }

    [PostgresClusterFact]
    public async Task WithoutTheOptInKeys_ThereIsNoStandby_AndAClusterRestartIsNotStarted()
    {
        using var cts = new CancellationTokenSource(Deadline);
        CancellationToken ct = cts.Token;
        // The default two-worker cluster of the other scenarios: no control channel, so the worker and the gateway answer as they always did.
        await using ClusterProcessFixture cluster = await ClusterProcessFixture.StartAsync(Options(transfers: false, standby: false), output, ct);
        Assert.Null(cluster.Standby);

        await using WorldClient client = await cluster.ConnectGatewayClientAsync(ct);
        var session = new ScenarioConnection(client);
        await session.CreateCharacterAsync("Plainjane", ct);
        MockCharacter character = Assert.Single(await session.EnumerateAsync(ct));
        await session.LoginAsync(character.Guid, ct);

        await SayAsync(session, ".cluster restart " + cluster.Workers[0].NodeId, ct);

        string answer = await ReadSystemLineAsync(session, line => line.Contains("restart", StringComparison.OrdinalIgnoreCase), ct);
        Assert.DoesNotContain("started", answer, StringComparison.Ordinal);
        Assert.Equal(0, await cluster.CharactersScalarAsync(Count("cluster_world_handoff", "stage >= 0"), ct));
        Assert.True(cluster.IsWorkerRunning(0));
        Assert.True(cluster.IsWorkerRunning(1));
    }

    /// <summary>
    /// The order a slow host produces by chance, made certain: the target worker starts listening while the gateway's link supervisor for it sits in its doubling backoff, so the worker is up
    /// but has no link and owns nothing for routing. The teleport waits for that link (its submit makes the supervisor dial at once) instead of being refused with map-unassigned.
    /// </summary>
    [PostgresClusterFact]
    public async Task ACrossWorkerTeleport_ToAWorkerThatIsUpButBetweenLinkDials_WaitsForItsLink_AndTheJournalSaysCompleted()
    {
        using var cts = new CancellationTokenSource(Deadline);
        CancellationToken ct = cts.Token;
        await using ClusterProcessFixture cluster = await ClusterProcessFixture.StartAsync(Options(transfers: true, standby: false, lateWorker: 1), output, ct);

        await using WorldClient client = await cluster.ConnectGatewayClientAsync(ct);
        var session = new ScenarioConnection(client);
        await session.CreateCharacterAsync("Telelate", ct);
        MockCharacter character = Assert.Single(await session.EnumerateAsync(ct));
        MockLogin login = await session.LoginAsync(character.Guid, ct);
        Assert.Contains((int)login.Location.Map, cluster.Workers[0].OwnedMaps);

        await cluster.StartLateWorkerAsync(ct);
        await SayAsync(session, ".go xyz " + KalimdorPoint, ct);

        // A refused teleport sends nothing that ends this read, so it has its own bound: well past a prompt dial and the transfer itself, far short of the scenario's deadline.
        using (var arrival = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            arrival.CancelAfter(TimeSpan.FromSeconds(90));
            byte[] pending = await ReadUntilOpcodeAsync(session, WorldOpcode.SmsgTransferPending, arrival.Token);
            Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(pending));
            byte[] newWorld = await ReadUntilOpcodeAsync(session, WorldOpcode.SmsgNewWorld, arrival.Token);
            Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(newWorld));
        }

        await session.SendAsync(WorldOpcode.MsgMoveWorldportAck, [], ct);

        string guid = character.Guid.ToString(CultureInfo.InvariantCulture);
        await WaitForCountAsync(cluster, Count("cluster_transfer_decision", $"character_guid = {guid} AND state = {(int)PlayerTransferDecisionState.Completed}"), expected: 1, ct);
        Assert.Equal(1, await cluster.CharactersScalarAsync(Count("cluster_transfer_owner", $"character_guid = {guid} AND scope = {SqlText("worker:" + cluster.Workers[1].NodeId)} AND active_transfer IS NULL"), ct));
    }

    /// <summary>A SQL string literal for a node id the fixture generated (never player input); a quote is doubled all the same.</summary>
    private static string SqlText(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string Count(string table, string where) => string.Create(CultureInfo.InvariantCulture, $"SELECT count(*) FROM {table} WHERE {where}");

    /// <summary>Polls a journal count until it equals <paramref name="expected"/> (the coordinator writes the final row after the client already saw the result).</summary>
    private static async Task WaitForCountAsync(ClusterProcessFixture cluster, string sql, long expected, CancellationToken ct)
    {
        long last = -1;
        while (true)
        {
            last = await cluster.CharactersScalarAsync(sql, ct);
            if (last == expected)
            {
                return;
            }

            ct.ThrowIfCancellationRequested();
            await Task.Delay(250, ct);
        }
    }

    /// <summary>A chat line as CMSG_MESSAGECHAT (say): a line that starts with a dot is a command.</summary>
    private static Task SayAsync(ScenarioConnection session, string text, CancellationToken ct) => session.SendAsync(WorldOpcode.CmsgMessagechat, LiveSession.SayPacket(text), ct);

    /// <summary>
    /// Reads frames until one with <paramref name="opcode"/> arrives and returns its payload. The wait for each frame's first byte has no deadline of its own (a transfer or a handoff takes seconds
    /// of silence); only <paramref name="ct"/> ends it.
    /// </summary>
    private static async Task<byte[]> ReadUntilOpcodeAsync(ScenarioConnection session, WorldOpcode opcode, CancellationToken ct)
    {
        while (true)
        {
            await session.WaitForTrafficAsync(ct);
            WorldFrame frame = await session.ReadAsync(ct);
            if (frame.Opcode == (ushort)opcode)
            {
                return frame.Payload;
            }
        }
    }

    /// <summary>Reads frames until a system chat line that satisfies <paramref name="match"/> arrives (other frames and other lines, such as the login message of the day, are skipped) and returns its text.</summary>
    private static async Task<string> ReadSystemLineAsync(ScenarioConnection session, Func<string, bool> match, CancellationToken ct)
    {
        while (true)
        {
            byte[] payload = await ReadUntilOpcodeAsync(session, WorldOpcode.SmsgMessagechat, ct);
            if (LiveSession.DecodeSystemLine(payload) is { } line && match(line))
            {
                return line;
            }
        }
    }
}

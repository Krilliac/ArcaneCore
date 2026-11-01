using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Cluster.E2E.Tests;

/// <summary>
/// S6b: a player crosses from the Eastern Kingdoms worker to the Kalimdor worker, and into a dungeon only the other worker hosts, over real
/// processes (gateway, realm daemon, two fenced workers on PostgreSQL; see <see cref="ClusterProcessFixture"/>). The same TCP connection
/// carries the whole session; the characters database is read directly to prove the durable side (decision journal, per-character ownership
/// epoch, shard ownership rows, the character row). The facts report Skipped, with the variable to set, when <c>ARCANECORE_TEST_POSTGRES</c> is not set.
/// <para>
/// The processes are owned by the fixture (killed and checked for orphans by its dispose). The database reads use unpooled connections so the
/// fixture can drop the database. Thread affinity: none; each fact is sequential.
/// </para>
/// </summary>
public sealed class CrossWorkerTransferTests
{
    // Orgrimmar (Kalimdor, map 1) and the Deadmines entrance room (map 36), as .go xyz arguments: x y z map.
    private const string OrgrimmarGo = ".go xyz 1629.36 -4373.39 31.26 1";
    private const uint KalimdorMap = 1;
    private const string DeadminesGo = ".go xyz -16.4 -383.07 61.78 36";
    private const uint DeadminesMap = 36;

    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(5);

    // Eastern Kingdoms (0, where a human starts) on the first worker; Kalimdor (1) and the Deadmines (36) on the second, which alone hosts dungeon instances.
    // The far-teleport router always submits target instance 0 and the gateway resolves that through the listed maps (an instanceable map outside every
    // list is refused as unassigned), so the instance worker lists map 36: a dungeon entry from the first worker has to cross.
    // The account is a game master: .go xyz is how a client starts a far teleport.
    // The Deadmines map_template row is seeded: without it the workers know only the continents and .go refuses map 36 before any transfer.
    private static ClusterOptions Crossing() => new()
    {
        WorkerCount = 2,
        WorkerMaps = [[0], [1, 36]],
        LoginWorkerIndex = 0,
        InstanceWorkerIndex = 1,
        Security = AccountSecurity.GameMaster,
        Transfers = true,
        SeedDeadminesTemplate = true,
    };

    [PostgresClusterFact]
    public async Task EasternKingdomsToKalimdor_KeepsTheConnection_AdvancesTheEpochOnce_AndAReplacingLoginTakesOver()
    {
        using var cts = new CancellationTokenSource(Deadline);
        CancellationToken ct = cts.Token;
        await using ClusterProcessFixture cluster = await ClusterProcessFixture.StartAsync(Crossing(), ct);

        await using WorldClient client = await cluster.ConnectGatewayClientAsync(ct);
        var session = new ScenarioConnection(client);
        await session.CreateCharacterAsync("Crosser", ct);
        MockCharacter character = Assert.Single(await session.EnumerateAsync(ct));
        long guid = (long)character.Guid;
        MockLogin login = await session.LoginAsync(character.Guid, ct);
        Assert.Contains((int)login.Location.Map, cluster.Workers[0].OwnedMaps);

        EndPoint connection = client.LocalEndPoint ?? throw new InvalidOperationException("The client has no local endpoint.");
        OwnerRow ownerBefore = Assert.IsType<OwnerRow>(await ReadOwnerAsync(cluster, guid, ct)); // the login claim created it
        IReadOnlyList<ShardRow> shardsBefore = await ReadShardsAsync(cluster, ct);

        Arrival arrival = await CrossAsync(session, cluster, guid, OrgrimmarGo, KalimdorMap, ct);
        Assert.True(arrival.TransferPendingSeen, "SMSG_TRANSFER_PENDING must precede SMSG_NEW_WORLD.");
        Assert.InRange(arrival.DecisionStateAtNewWorld, (int)PlayerTransferDecisionState.Committed, (int)PlayerTransferDecisionState.Completed);

        // The same connection served the whole crossing, and it still answers.
        Assert.Equal(connection, client.LocalEndPoint);
        await PingAsync(session, 0xC0FFEE, ct);

        // One journal row, committed once. With per-worker fencing the commit gives the character the target worker's own live scope token, so the owner row
        // now carries that worker's active character_ownership epoch (not source epoch + 1), its scope and incarnation moved, and the shard rows did not.
        DecisionRow decision = await WaitForCompletedAsync(cluster, guid, ct);
        Assert.Equal(0, decision.SourceMap);
        Assert.Equal((long)KalimdorMap, decision.TargetMap);
        Assert.NotEqual(decision.SourceWorker, decision.TargetWorker);
        Assert.True(decision.SessionGeneration > 0, "the journal must name the gateway session generation.");
        IReadOnlyList<ShardRow> shardsAfter = await ReadShardsAsync(cluster, ct);
        OwnerRow owner = Assert.IsType<OwnerRow>(await ReadOwnerAsync(cluster, guid, ct));
        AssertMovedToTarget(ownerBefore, owner, decision, shardsAfter);

        Assert.Equal(shardsBefore, shardsAfter);

        // A second login of the same account while the first session is online replaces it: the first connection is closed, the
        // second is served by the owner of the character's map, and nothing advances the epoch again.
        await using WorldClient second = await cluster.ConnectGatewayClientAsync(ct);
        var replacement = new ScenarioConnection(second);
        MockCharacter again = Assert.Single(await replacement.EnumerateAsync(ct));
        Assert.Equal(character.Guid, again.Guid);
        MockLogin relogin = await replacement.LoginAsync(again.Guid, ct);
        Assert.Equal(character.Guid, relogin.Self.Guid);
        Assert.Contains((int)relogin.Location.Map, cluster.Workers[1].OwnedMaps);
        Assert.True(await WaitForCloseAsync(session, ct), "The first session must be closed by the replacing login.");
        await PingAsync(replacement, 0xBEEF, ct);

        // Checked while the replacing session is still online: the replacing login claims with the token the owner row already holds, so scope, epoch and
        // incarnation stay. (A logout schedules the claim release, which deletes the owner row once the final save is durable, so it is not read after it.)
        DecisionRow only = Assert.Single(await ReadDecisionsAsync(cluster, guid, ct));
        Assert.Equal(decision.TransferId, only.TransferId);
        Assert.Equal(decision.SessionGeneration, only.SessionGeneration);
        Assert.Equal((int)PlayerTransferDecisionState.Completed, only.State);
        OwnerRow ownerAfter = Assert.IsType<OwnerRow>(await ReadOwnerAsync(cluster, guid, ct));
        Assert.Equal(owner, ownerAfter);

        await replacement.LogoutAsync(ct);

        // No stale write from the old owner: the character row holds the new map after both sessions are gone. SMSG_LOGOUT_COMPLETE only queues the final
        // save, so the row is read once the claim is released, which happens only after the character's last write is durable.
        Assert.True(await WaitForClaimReleasedAsync(cluster, guid, ct), "The logout must release the character's claim once its final save is durable.");
        Assert.Equal((long)KalimdorMap, await ReadCharacterMapAsync(cluster, guid, ct));
    }

    [PostgresClusterFact]
    public async Task DungeonEntry_FromTheContinentWorker_CrossesToTheInstanceWorker_OnTheSameConnection()
    {
        using var cts = new CancellationTokenSource(Deadline);
        CancellationToken ct = cts.Token;
        await using ClusterProcessFixture cluster = await ClusterProcessFixture.StartAsync(Crossing(), ct);
        Assert.DoesNotContain((int)DeadminesMap, cluster.Workers[0].OwnedMaps);
        Assert.Contains((int)DeadminesMap, cluster.Workers[1].OwnedMaps);

        await using WorldClient client = await cluster.ConnectGatewayClientAsync(ct);
        var session = new ScenarioConnection(client);
        await session.CreateCharacterAsync("Delver", ct);
        MockCharacter character = Assert.Single(await session.EnumerateAsync(ct));
        long guid = (long)character.Guid;
        await session.LoginAsync(character.Guid, ct);

        EndPoint connection = client.LocalEndPoint ?? throw new InvalidOperationException("The client has no local endpoint.");
        OwnerRow ownerBefore = Assert.IsType<OwnerRow>(await ReadOwnerAsync(cluster, guid, ct));

        Arrival arrival = await CrossAsync(session, cluster, guid, DeadminesGo, DeadminesMap, ct);
        Assert.True(arrival.TransferPendingSeen, "SMSG_TRANSFER_PENDING must precede SMSG_NEW_WORLD.");
        Assert.InRange(arrival.DecisionStateAtNewWorld, (int)PlayerTransferDecisionState.Committed, (int)PlayerTransferDecisionState.Completed);
        Assert.Equal(connection, client.LocalEndPoint);
        await PingAsync(session, 0xD3AD, ct);

        DecisionRow decision = await WaitForCompletedAsync(cluster, guid, ct);
        Assert.Equal((long)DeadminesMap, decision.TargetMap);
        Assert.NotEqual(decision.SourceWorker, decision.TargetWorker);
        OwnerRow owner = Assert.IsType<OwnerRow>(await ReadOwnerAsync(cluster, guid, ct));
        AssertMovedToTarget(ownerBefore, owner, decision, await ReadShardsAsync(cluster, ct));

        await session.LogoutAsync(ct);
        Assert.Single(await ReadDecisionsAsync(cluster, guid, ct));
        Assert.True(await WaitForClaimReleasedAsync(cluster, guid, ct), "The logout must release the character's claim once its final save is durable.");
        Assert.Equal((long)DeadminesMap, await ReadCharacterMapAsync(cluster, guid, ct));
    }

    /// <summary>The owner row moved to the target worker's scope, carries that scope's live (active) character_ownership epoch and a new incarnation, and holds no open transfer.</summary>
    private static void AssertMovedToTarget(OwnerRow before, OwnerRow after, DecisionRow decision, IReadOnlyList<ShardRow> shards)
    {
        Assert.NotNull(decision.TargetScope);
        Assert.Equal(decision.TargetScope, after.Scope);
        Assert.NotEqual(before.Scope, after.Scope);
        Assert.NotEqual(before.Incarnation, after.Incarnation);
        ShardRow target = Assert.Single(shards, shard => shard.Scope == after.Scope);
        Assert.True(target.Active, "the target worker's shard row must be active.");
        Assert.Equal(target.Epoch, after.Epoch);
        Assert.Equal(target.Epoch, decision.TargetEpoch);
        Assert.Equal(target.Incarnation, after.Incarnation);
        Assert.Null(after.ActiveTransfer);
    }

    // ---- client side ----

    private sealed record Arrival(bool TransferPendingSeen, int DecisionStateAtNewWorld);

    /// <summary>
    /// Types <paramref name="command"/> as a say, then plays the client's half of a far teleport: reads to SMSG_NEW_WORLD (noting an earlier SMSG_TRANSFER_PENDING),
    /// reads the state of the journal at that moment, answers with MSG_MOVE_WORLDPORT_ACK and waits for the target's world states (the map add).
    /// </summary>
    private static async Task<Arrival> CrossAsync(ScenarioConnection session, ClusterProcessFixture cluster, long guid, string command, uint targetMap, CancellationToken ct)
    {
        await session.SendAsync(WorldOpcode.CmsgMessagechat, LiveSession.SayPacket(command), ct);

        bool pending = false;
        byte[]? newWorld = null;
        for (int frames = 0; frames < 512 && newWorld is null; frames++)
        {
            await session.WaitForTrafficAsync(ct); // the freeze and the transfer may be quiet for longer than one read's deadline
            WorldFrame frame = await session.ReadAsync(ct);
            if (frame.Opcode == (ushort)WorldOpcode.SmsgTransferPending)
            {
                pending = true;
            }
            else if (frame.Opcode == (ushort)WorldOpcode.SmsgNewWorld)
            {
                newWorld = frame.Payload;
            }
        }

        byte[] payload = newWorld ?? throw new InvalidOperationException("SMSG_NEW_WORLD did not arrive within 512 packets of the teleport command.");
        Assert.Equal(targetMap, BinaryPrimitives.ReadUInt32LittleEndian(payload));

        // The loading-screen packet only exists once the transfer committed: the journal already says so.
        DecisionRow decision = Assert.Single(await ReadDecisionsAsync(cluster, guid, ct));

        await session.SendAsync(WorldOpcode.MsgMoveWorldportAck, [], ct);
        await ReadPatientUntilAsync(session, WorldOpcode.SmsgInitWorldStates, ct);
        return new Arrival(pending, decision.State);
    }

    private static async Task ReadPatientUntilAsync(ScenarioConnection session, WorldOpcode opcode, CancellationToken ct)
    {
        for (int frames = 0; frames < 512; frames++)
        {
            await session.WaitForTrafficAsync(ct);
            WorldFrame frame = await session.ReadAsync(ct);
            if (frame.Opcode == (ushort)opcode)
            {
                return;
            }
        }

        Assert.Fail($"{opcode} did not arrive within 512 packets.");
    }

    private static async Task PingAsync(ScenarioConnection session, uint sequence, CancellationToken ct)
    {
        await session.SendAsync(WorldOpcode.CmsgPing, ScenarioWire.Ping(sequence, 0), ct);
        byte[] pong = await session.ReadUntilAsync(WorldOpcode.SmsgPong, ct);
        Assert.Equal(sequence, BinaryPrimitives.ReadUInt32LittleEndian(pong));
    }

    /// <summary>True once the server has closed this session's connection (a read fails with an I/O or socket error).</summary>
    private static async Task<bool> WaitForCloseAsync(ScenarioConnection session, CancellationToken ct)
    {
        // A shorter budget than the fact's deadline, so a session that is never closed fails the caller's assert, not the whole fact with a cancellation.
        using var closeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        closeCts.CancelAfter(TimeSpan.FromSeconds(45));
        for (int frames = 0; frames < 1024; frames++)
        {
            try
            {
                await session.WaitForTrafficAsync(closeCts.Token);
                await session.ReadAsync(closeCts.Token);
            }
            catch (OperationCanceledException) when (closeCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex) when (ex is not MockProtocolException and (EndOfStreamException or SocketException or IOException { InnerException: SocketException }))
            {
                return true;
            }
        }

        return false;
    }

    // ---- characters database (PostgreSQL; table and column names are the data modules' mappings) ----

    private sealed record DecisionRow(
        string TransferId, long SessionGeneration, string SourceWorker, string TargetWorker, long SourceMap, long TargetMap, int State, long SourceEpoch, long TargetEpoch, string? TargetScope);

    private sealed record OwnerRow(string Scope, long Epoch, string Incarnation, string? ActiveTransfer);

    // Incarnation is NULL on the realm-gate 'world' row (inactive under worker fencing).
    private sealed record ShardRow(string Scope, long Epoch, bool Active, string? Incarnation);

    private static Task<IReadOnlyList<DecisionRow>> ReadDecisionsAsync(ClusterProcessFixture cluster, long guid, CancellationToken ct)
        => cluster.CharactersRowsAsync(
            "SELECT transfer_id, session_generation, source_worker, target_worker, source_map, target_map, state, source_epoch, target_epoch, target_scope " +
            "FROM cluster_transfer_decision WHERE character_guid = " + guid.ToString(CultureInfo.InvariantCulture) + " ORDER BY updated_at_ms, transfer_id",
            reader => new DecisionRow(
                reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5),
                reader.GetInt32(6), reader.GetInt64(7), reader.IsDBNull(8) ? -1 : reader.GetInt64(8), reader.IsDBNull(9) ? null : reader.GetString(9)),
            ct);

    /// <summary>Waits until the character has exactly one journal row and it is Completed (stage 6 done), then returns it.</summary>
    private static async Task<DecisionRow> WaitForCompletedAsync(ClusterProcessFixture cluster, long guid, CancellationToken ct)
    {
        while (true)
        {
            IReadOnlyList<DecisionRow> rows = await ReadDecisionsAsync(cluster, guid, ct);
            Assert.True(rows.Count <= 1, $"Expected one transfer journal row for the character, found {rows.Count}.");
            if (rows.Count == 1)
            {
                Assert.NotEqual((int)PlayerTransferDecisionState.Aborted, rows[0].State);
                if (rows[0].State == (int)PlayerTransferDecisionState.Completed)
                {
                    return rows[0];
                }
            }

            await Task.Delay(250, ct);
        }
    }

    /// <summary>
    /// True once the character's owner row is gone: a logout releases the claim only after every queue holding the character's writes is durable
    /// (CharacterClaimService.ScheduleRelease), so the final save is in the row by then. False if it is still there after 45 seconds.
    /// </summary>
    private static async Task<bool> WaitForClaimReleasedAsync(ClusterProcessFixture cluster, long guid, CancellationToken ct)
    {
        using var releaseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        releaseCts.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            while (await ReadOwnerAsync(cluster, guid, releaseCts.Token) is not null)
            {
                await Task.Delay(100, releaseCts.Token);
            }

            return true;
        }
        catch (OperationCanceledException) when (releaseCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private static async Task<OwnerRow?> ReadOwnerAsync(ClusterProcessFixture cluster, long guid, CancellationToken ct)
    {
        IReadOnlyList<OwnerRow> rows = await cluster.CharactersRowsAsync(
            "SELECT scope, epoch, incarnation, active_transfer FROM cluster_transfer_owner WHERE character_guid = " + guid.ToString(CultureInfo.InvariantCulture),
            reader => new OwnerRow(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)),
            ct);
        return rows.Count == 0 ? null : rows[0];
    }

    private static Task<IReadOnlyList<ShardRow>> ReadShardsAsync(ClusterProcessFixture cluster, CancellationToken ct)
        => cluster.CharactersRowsAsync(
            "SELECT scope, epoch, active, owner_incarnation FROM character_ownership ORDER BY scope",
            reader => new ShardRow(reader.GetString(0), reader.GetInt64(1), reader.GetBoolean(2), reader.IsDBNull(3) ? null : reader.GetString(3)),
            ct);

    private static Task<long> ReadCharacterMapAsync(ClusterProcessFixture cluster, long guid, CancellationToken ct)
        => cluster.CharactersScalarAsync("SELECT \"MapId\" FROM characters WHERE \"Id\" = " + guid.ToString(CultureInfo.InvariantCulture), ct);
}

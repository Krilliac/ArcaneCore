using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using Npgsql;
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

    // Eastern Kingdoms (0, where a human starts) on the first worker; Kalimdor (1) on the second, which alone hosts dungeon instances,
    // so a dungeon entry from the first worker has to cross. The account is a game master: .go xyz is how a client starts a far teleport.
    private static ClusterOptions Crossing() => new()
    {
        WorkerCount = 2,
        WorkerMaps = [[0], [1]],
        LoginWorkerIndex = 0,
        InstanceWorkerIndex = 1,
        AccountSecurity = AccountSecurity.GameMaster,
    };

    [PostgresClusterFact]
    public async Task EasternKingdomsToKalimdor_KeepsTheConnection_AdvancesTheEpochOnce_AndAReplacingLoginTakesOver()
    {
        using var cts = new CancellationTokenSource(Deadline);
        CancellationToken ct = cts.Token;
        await using ClusterProcessFixture cluster = await ClusterProcessFixture.StartAsync(Crossing(), ct);
        string database = cluster.CharactersConnectionString;

        await using WorldClient client = await cluster.ConnectGatewayClientAsync(ct);
        var session = new ScenarioConnection(client);
        await session.CreateCharacterAsync("Crosser", ct);
        MockCharacter character = Assert.Single(await session.EnumerateAsync(ct));
        long guid = (long)character.Guid;
        MockLogin login = await session.LoginAsync(character.Guid, ct);
        Assert.Contains((int)login.Location.Map, cluster.Workers[0].OwnedMaps);

        EndPoint connection = client.LocalEndPoint ?? throw new InvalidOperationException("The client has no local endpoint.");
        long? epochBefore = (await ReadOwnerAsync(database, guid, ct))?.Epoch;
        IReadOnlyList<ShardRow> shardsBefore = await ReadShardsAsync(database, ct);

        Arrival arrival = await CrossAsync(session, database, guid, OrgrimmarGo, KalimdorMap, ct);
        Assert.True(arrival.TransferPendingSeen, "SMSG_TRANSFER_PENDING must precede SMSG_NEW_WORLD.");
        Assert.InRange(arrival.DecisionStateAtNewWorld, (int)PlayerTransferDecisionState.Committed, (int)PlayerTransferDecisionState.Completed);

        // The same connection served the whole crossing, and it still answers.
        Assert.Equal(connection, client.LocalEndPoint);
        await PingAsync(session, 0xC0FFEE, ct);

        // One journal row, committed once: the source's epoch plus one, which the per-character ownership row now carries; the shard rows did not move.
        DecisionRow decision = await WaitForCompletedAsync(database, guid, ct);
        Assert.Equal(0, decision.SourceMap);
        Assert.Equal((long)KalimdorMap, decision.TargetMap);
        Assert.NotEqual(decision.SourceWorker, decision.TargetWorker);
        Assert.Equal(decision.SourceEpoch + 1, decision.TargetEpoch);
        OwnerRow owner = Assert.IsType<OwnerRow>(await ReadOwnerAsync(database, guid, ct));
        Assert.Equal(decision.TargetEpoch, owner.Epoch);
        Assert.Null(owner.ActiveTransfer);
        // Two paths, reported in the failure text: a row existed before (epoch compared against it), or none did (only the journal's source+1 above holds).
        Assert.Equal(epochBefore is { } before ? before + 1 : decision.TargetEpoch, owner.Epoch,
            epochBefore is null ? "no cluster_transfer_owner row existed before the transfer; only the journal epoch was checked" : "the owner epoch must advance exactly once from the pre-transfer row");

        Assert.Equal(shardsBefore, await ReadShardsAsync(database, ct));

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

        await replacement.LogoutAsync(ct);
        DecisionRow only = Assert.Single(await ReadDecisionsAsync(database, guid, ct));
        Assert.Equal(decision.TransferId, only.TransferId);
        Assert.Equal(decision.SessionGeneration, only.SessionGeneration);
        Assert.Equal((int)PlayerTransferDecisionState.Completed, only.State);
        OwnerRow ownerAfter = Assert.IsType<OwnerRow>(await ReadOwnerAsync(database, guid, ct));
        Assert.Equal(decision.TargetEpoch, ownerAfter.Epoch);

        // No stale write from the old owner: the character row holds the new map after both sessions are gone.
        Assert.Equal((long)KalimdorMap, await ReadCharacterMapAsync(database, guid, ct));
    }

    [PostgresClusterFact]
    public async Task DungeonEntry_FromTheContinentWorker_CrossesToTheInstanceWorker_OnTheSameConnection()
    {
        using var cts = new CancellationTokenSource(Deadline);
        CancellationToken ct = cts.Token;
        await using ClusterProcessFixture cluster = await ClusterProcessFixture.StartAsync(Crossing(), ct);
        string database = cluster.CharactersConnectionString;
        Assert.DoesNotContain((int)DeadminesMap, cluster.Workers[0].OwnedMaps.Concat(cluster.Workers[1].OwnedMaps));

        await using WorldClient client = await cluster.ConnectGatewayClientAsync(ct);
        var session = new ScenarioConnection(client);
        await session.CreateCharacterAsync("Delver", ct);
        MockCharacter character = Assert.Single(await session.EnumerateAsync(ct));
        long guid = (long)character.Guid;
        await session.LoginAsync(character.Guid, ct);

        EndPoint connection = client.LocalEndPoint ?? throw new InvalidOperationException("The client has no local endpoint.");
        long? epochBefore = (await ReadOwnerAsync(database, guid, ct))?.Epoch;

        Arrival arrival = await CrossAsync(session, database, guid, DeadminesGo, DeadminesMap, ct);
        Assert.True(arrival.TransferPendingSeen, "SMSG_TRANSFER_PENDING must precede SMSG_NEW_WORLD.");
        Assert.InRange(arrival.DecisionStateAtNewWorld, (int)PlayerTransferDecisionState.Committed, (int)PlayerTransferDecisionState.Completed);
        Assert.Equal(connection, client.LocalEndPoint);
        await PingAsync(session, 0xD3AD, ct);

        DecisionRow decision = await WaitForCompletedAsync(database, guid, ct);
        Assert.Equal((long)DeadminesMap, decision.TargetMap);
        Assert.NotEqual(decision.SourceWorker, decision.TargetWorker);
        Assert.Equal(decision.SourceEpoch + 1, decision.TargetEpoch);
        OwnerRow owner = Assert.IsType<OwnerRow>(await ReadOwnerAsync(database, guid, ct));
        Assert.Equal(decision.TargetEpoch, owner.Epoch);
        // Two paths, reported in the failure text: a row existed before (epoch compared against it), or none did (only the journal's source+1 above holds).
        Assert.Equal(epochBefore is { } before ? before + 1 : decision.TargetEpoch, owner.Epoch,
            epochBefore is null ? "no cluster_transfer_owner row existed before the transfer; only the journal epoch was checked" : "the owner epoch must advance exactly once from the pre-transfer row");

        await session.LogoutAsync(ct);
        Assert.Single(await ReadDecisionsAsync(database, guid, ct));
        Assert.Equal((long)DeadminesMap, await ReadCharacterMapAsync(database, guid, ct));
    }

    // ---- client side ----

    private sealed record Arrival(bool TransferPendingSeen, int DecisionStateAtNewWorld);

    /// <summary>
    /// Types <paramref name="command"/> as a say, then plays the client's half of a far teleport: reads to SMSG_NEW_WORLD (noting an earlier SMSG_TRANSFER_PENDING),
    /// reads the state of the journal at that moment, answers with MSG_MOVE_WORLDPORT_ACK and waits for the target's world states (the map add).
    /// </summary>
    private static async Task<Arrival> CrossAsync(ScenarioConnection session, string database, long guid, string command, uint targetMap, CancellationToken ct)
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
        DecisionRow decision = Assert.Single(await ReadDecisionsAsync(database, guid, ct));

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
        for (int frames = 0; frames < 1024; frames++)
        {
            try
            {
                await session.WaitForTrafficAsync(ct);
                await session.ReadAsync(ct);
            }
            catch (Exception ex) when (ex is EndOfStreamException or SocketException or ObjectDisposedException
                or IOException { InnerException: SocketException or ObjectDisposedException } and not MockProtocolException)
            {
                return true;
            }
        }

        return false;
    }

    // ---- characters database (PostgreSQL; table and column names are the data modules' mappings) ----

    private sealed record DecisionRow(
        string TransferId, long SessionGeneration, string SourceWorker, string TargetWorker, long SourceMap, long TargetMap, int State, long SourceEpoch, long TargetEpoch);

    private sealed record OwnerRow(long Epoch, string? ActiveTransfer);

    private sealed record ShardRow(string Scope, long Epoch, bool Active);

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString, CancellationToken ct)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task<IReadOnlyList<DecisionRow>> ReadDecisionsAsync(string database, long guid, CancellationToken ct)
    {
        await using NpgsqlConnection connection = await OpenAsync(database, ct);
        await using var command = new NpgsqlCommand(
            "SELECT transfer_id, session_generation, source_worker, target_worker, source_map, target_map, state, source_epoch, target_epoch " +
            "FROM cluster_transfer_decision WHERE character_guid = @guid ORDER BY updated_at_ms, transfer_id", connection);
        command.Parameters.AddWithValue("guid", guid);
        var rows = new List<DecisionRow>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new DecisionRow(
                reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5),
                reader.GetInt32(6), reader.GetInt64(7), reader.IsDBNull(8) ? -1 : reader.GetInt64(8)));
        }

        return rows;
    }

    /// <summary>Waits until the character has exactly one journal row and it is Completed (stage 6 done), then returns it.</summary>
    private static async Task<DecisionRow> WaitForCompletedAsync(string database, long guid, CancellationToken ct)
    {
        while (true)
        {
            IReadOnlyList<DecisionRow> rows = await ReadDecisionsAsync(database, guid, ct);
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

    private static async Task<OwnerRow?> ReadOwnerAsync(string database, long guid, CancellationToken ct)
    {
        await using NpgsqlConnection connection = await OpenAsync(database, ct);
        await using var command = new NpgsqlCommand("SELECT epoch, active_transfer FROM cluster_transfer_owner WHERE character_guid = @guid", connection);
        command.Parameters.AddWithValue("guid", guid);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new OwnerRow(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1)) : null;
    }

    private static async Task<IReadOnlyList<ShardRow>> ReadShardsAsync(string database, CancellationToken ct)
    {
        await using NpgsqlConnection connection = await OpenAsync(database, ct);
        await using var command = new NpgsqlCommand("SELECT scope, epoch, active FROM character_ownership ORDER BY scope", connection);
        var rows = new List<ShardRow>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new ShardRow(reader.GetString(0), reader.GetInt64(1), reader.GetBoolean(2)));
        }

        return rows;
    }

    private static async Task<long> ReadCharacterMapAsync(string database, long guid, CancellationToken ct)
    {
        await using NpgsqlConnection connection = await OpenAsync(database, ct);
        await using var command = new NpgsqlCommand("SELECT \"MapId\" FROM characters WHERE \"Id\" = @id", connection);
        command.Parameters.AddWithValue("id", (int)guid);
        object? map = await command.ExecuteScalarAsync(ct);
        if (map is null or DBNull)
        {
            throw new InvalidOperationException("The character row has no map.");
        }

        return Convert.ToInt64(map, CultureInfo.InvariantCulture);
    }
}

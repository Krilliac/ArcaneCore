using System.Globalization;
using System.Net.Sockets;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Cluster.E2E.Tests;

/// <summary>The process a <see cref="TransferCrashTests"/> case kills.</summary>
public enum CrashVictim
{
    /// <summary>The worker the player leaves (Eastern Kingdoms).</summary>
    Source,

    /// <summary>The worker the player enters (Kalimdor).</summary>
    Target,

    /// <summary>The gateway, which hosts the transfer coordinator and the client connection.</summary>
    Gateway,
}

/// <summary>
/// S6c: a process is killed at each of the six stages of the cross-worker transfer (reserve, suspend and capture, install passive, stage world-port, commit decision,
/// activate and retire), for the source worker, the target worker and the gateway: 18 cases over real processes on PostgreSQL (see <see cref="ClusterProcessFixture"/>).
/// <para>
/// The coordinator runs inside the gateway. The gateway is launched with the test-only stage hold of <c>TransferStageProbe</c> (set through
/// <see cref="ClusterOptions.FirstGatewayEnvironment"/>): when the transfer reaches the stage under test it writes a marker file and waits, the test kills the victim at
/// exactly that moment, restarts it, and releases the hold. The journal sweep (worker and gateway start, and the gateway's recovery loop) then has to settle the transfer.
/// Stage 6 is held between retiring the source and activating the target.
/// </para>
/// <para>
/// Asserted once the journal row is settled: one journal row, Completed or Aborted, no open transfer on the owner row; the owner row (when the character is still
/// claimed) names the target's scope after a Completed transfer and the source's unchanged scope after an Aborted one, so there is exactly one owner; copper and the
/// number of item rows are unchanged; the original connection either still answers or is cleanly closed; and a fresh login reconnects the character on a worker that owns its map.
/// A kill at the commit-decision or activate-and-retire stage must end Completed (roll-forward only); earlier stages may end either way, and the fresh login then lands
/// on Kalimdor after a Completed transfer and on Eastern Kingdoms after an Aborted one. Single ownership is read from the owner row and the shard rows while the fresh session is online.
/// </para>
/// Processes are owned by the fixture. Thread affinity: none; each case is sequential.
/// </summary>
public sealed class TransferCrashTests
{
    private const string OrgrimmarGo = ".go xyz 1629.36 -4373.39 31.26 1";
    private const uint EasternKingdomsMap = 0;
    private const uint KalimdorMap = 1;
    private const int SourceWorker = 0;
    private const int TargetWorker = 1;
    private const long StartCopper = 123456;
    private const uint StartItemEntry = 900101;

    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(8);
    private static readonly TimeSpan MarkerWait = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SettleWait = TimeSpan.FromMinutes(2);

    private static readonly PlayerTransferStage[] Stages =
    [
        PlayerTransferStage.Reserve,
        PlayerTransferStage.SuspendAndCapture,
        PlayerTransferStage.InstallPassive,
        PlayerTransferStage.StageWorldPort,
        PlayerTransferStage.CommitDecision,
        PlayerTransferStage.ActivateAndRetire,
    ];

    /// <summary>Every stage against every victim: 6 x 3 = 18 cases.</summary>
    public static TheoryData<PlayerTransferStage, CrashVictim> Cases()
    {
        var data = new TheoryData<PlayerTransferStage, CrashVictim>();
        foreach (PlayerTransferStage stage in Stages)
        {
            foreach (CrashVictim victim in Enum.GetValues<CrashVictim>())
            {
                data.Add(stage, victim);
            }
        }

        return data;
    }

    [PostgresClusterTheory]
    [MemberData(nameof(Cases))]
    public async Task KillAtStage_LeavesOneOwner_AndNoLostCopperOrItems(PlayerTransferStage stage, CrashVictim victim)
    {
        using var cts = new CancellationTokenSource(Deadline);
        CancellationToken ct = cts.Token;
        string holdDirectory = Path.Combine(Path.GetTempPath(), "arcane-transfer-crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(holdDirectory);
        try
        {
            var options = new ClusterOptions
            {
                WorkerCount = 2,
                WorkerMaps = [[0], [1]],
                LoginWorkerIndex = SourceWorker,
                Security = AccountSecurity.GameMaster,
                Transfers = true,
                StartPlayerMoney = StartCopper,
                StartingItemEntry = StartItemEntry,
                FirstGatewayEnvironment = new Dictionary<string, string>
                {
                    ["ARCANECORE_TEST_TRANSFER_HOLD_STAGE"] = stage.ToString(),
                    ["ARCANECORE_TEST_TRANSFER_HOLD_DIR"] = holdDirectory,
                },
            };
            await using ClusterProcessFixture cluster = await ClusterProcessFixture.StartAsync(options, ct);

            await using WorldClient client = await cluster.ConnectGatewayClientAsync(ct);
            var session = new ScenarioConnection(client);
            await session.CreateCharacterAsync("Crashmid", ct);
            MockCharacter character = Assert.Single(await session.EnumerateAsync(ct));
            long guid = (long)character.Guid;
            MockLogin login = await session.LoginAsync(character.Guid, ct);
            Assert.Contains((int)login.Location.Map, cluster.Workers[SourceWorker].OwnedMaps);

            OwnerRow ownerBefore = Assert.IsType<OwnerRow>(await ReadOwnerAsync(cluster, guid, ct));
            long copperBefore = await ReadCopperAsync(cluster, guid, ct);
            long itemsBefore = await ReadItemCountAsync(cluster, guid, ct);
            Assert.True(copperBefore > 0, "The character must own copper before the transfer, or a loss could not show.");
            Assert.True(itemsBefore > 0, "The character must own a persisted item before the transfer, or a loss could not show.");

            // Start the far teleport; the coordinator parks at the stage under test.
            await session.SendAsync(WorldOpcode.CmsgMessagechat, LiveSession.SayPacket(OrgrimmarGo), ct);
            await WaitForMarkerAsync(holdDirectory, stage, ct);
            Assert.Single(await ReadDecisionsAsync(cluster, guid, ct));

            // Kill the victim at exactly this stage, bring it back, and let the coordinator (if it survived) go on.
            switch (victim)
            {
                case CrashVictim.Source:
                    await cluster.KillWorkerAsync(SourceWorker);
                    await cluster.RestartWorkerAsync(SourceWorker, ct);
                    break;
                case CrashVictim.Target:
                    await cluster.KillWorkerAsync(TargetWorker);
                    await cluster.RestartWorkerAsync(TargetWorker, ct);
                    break;
                default:
                    await cluster.KillGatewayAsync();
                    await cluster.RestartGatewayAsync(ct);
                    break;
            }

            await File.WriteAllTextAsync(Path.Combine(holdDirectory, "release"), "release", ct);

            DecisionRow decision = await WaitForSettledAsync(cluster, guid, ct);

            // The claim, if the character is still claimed, is the target's after a completed transfer and the source's, unchanged, after an abort (single ownership while a
            // session is online is asserted after the fresh login below).
            bool completed = decision.State == (int)PlayerTransferDecisionState.Completed;
            if (stage is PlayerTransferStage.CommitDecision or PlayerTransferStage.ActivateAndRetire)
            {
                // Past the commit decision the coordinator's contract is roll-forward only (PlayerTransferCoordinator: a committed transfer is never aborted).
                Assert.True(completed, $"A transfer killed at {stage} ({victim}) must roll forward to Completed, but the journal state is {decision.State}.");
            }
            else if (victim == CrashVictim.Gateway && stage is not PlayerTransferStage.ActivateAndRetire)
            {
                // The restarted gateway has no surviving coordinator; recovery aborts a record that never reached the commit decision (RecoverTransferAsync).
                Assert.False(completed, $"A transfer whose gateway was killed at {stage} has no coordinator left to commit it and must abort.");
            }

            if (await ReadOwnerAsync(cluster, guid, ct) is { } owner)
            {
                Assert.Null(owner.ActiveTransfer);
                if (completed)
                {
                    Assert.NotNull(decision.TargetScope);
                    Assert.Equal(decision.TargetScope, owner.Scope);
                }
                else
                {
                    Assert.Equal(ownerBefore.Scope, owner.Scope);
                    Assert.Equal(ownerBefore.Epoch, owner.Epoch);
                }
            }

            // Nothing lost or duplicated.
            Assert.Equal(copperBefore, await ReadCopperAsync(cluster, guid, ct));
            Assert.Equal(itemsBefore, await ReadItemCountAsync(cluster, guid, ct));
            Assert.Single(await ReadDecisionsAsync(cluster, guid, ct));

            // The original connection either still answers or is cleanly closed; it never hangs.
            await AssertAnswersOrClosedAsync(session, ct);

            // A fresh login reconnects the character on a worker that owns its map, and nothing lost its state meanwhile.
            string? otherScope = completed ? ownerBefore.Scope : decision.TargetScope;
            string expectedScope = completed ? decision.TargetScope! : ownerBefore.Scope;
            await ReconnectAsync(cluster, character.Guid, completed ? KalimdorMap : EasternKingdomsMap, expectedScope, otherScope, ct);
            Assert.Equal(copperBefore, await ReadCopperAsync(cluster, guid, ct));
            Assert.Equal(itemsBefore, await ReadItemCountAsync(cluster, guid, ct));
            Assert.Equal(completed ? (long)KalimdorMap : EasternKingdomsMap, await ReadCharacterMapAsync(cluster, guid, ct));
        }
        finally
        {
            try
            {
                Directory.Delete(holdDirectory, recursive: true);
            }
            catch (IOException)
            {
                // A daemon still holds the directory open; the temp folder is cleaned by the OS later.
            }
        }
    }

    private static async Task WaitForMarkerAsync(string directory, PlayerTransferStage stage, CancellationToken ct)
    {
        string marker = Path.Combine(directory, "reached-" + stage);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(MarkerWait);
        try
        {
            while (!File.Exists(marker))
            {
                await Task.Delay(100, wait.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Assert.Fail($"The transfer never reached stage {stage} within {MarkerWait.TotalSeconds:0} seconds.");
        }
    }

    /// <summary>Waits until the character has exactly one journal row, Completed or Aborted, and the owner row (if any) holds no open transfer.</summary>
    private static async Task<DecisionRow> WaitForSettledAsync(ClusterProcessFixture cluster, long guid, CancellationToken ct)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(SettleWait);
        IReadOnlyList<DecisionRow> rows = [];
        try
        {
            while (true)
            {
                rows = await ReadDecisionsAsync(cluster, guid, wait.Token);
                Assert.True(rows.Count <= 1, $"Expected one transfer journal row for the character, found {rows.Count}.");
                if (rows.Count == 1
                    && (rows[0].State == (int)PlayerTransferDecisionState.Completed || rows[0].State == (int)PlayerTransferDecisionState.Aborted)
                    && (await ReadOwnerAsync(cluster, guid, wait.Token))?.ActiveTransfer is null)
                {
                    return rows[0];
                }

                await Task.Delay(250, wait.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            string state = rows.Count == 1 ? rows[0].State.ToString(CultureInfo.InvariantCulture) : rows.Count.ToString(CultureInfo.InvariantCulture) + " rows";
            throw new Xunit.Sdk.XunitException($"The journal sweep did not settle the transfer within {SettleWait.TotalSeconds:0} seconds (journal: {state}).");
        }
    }

    /// <summary>The original session either answers a ping or has been closed by the server; a session that does neither within the budget fails.</summary>
    private static async Task AssertAnswersOrClosedAsync(ScenarioConnection session, CancellationToken ct)
    {
        // A patient read loop, as WaitForCloseAsync does: after a completed far teleport the session streams NEW_WORLD and update traffic, so a fixed-size
        // ReadUntilAsync would overflow its packet limit. A protocol error (MockProtocolException, which derives from IOException) or running out of frames fails the test.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            await session.SendAsync(WorldOpcode.CmsgPing, ScenarioWire.Ping(0xC4A5, 0), budget.Token);
            for (int frames = 0; frames < 1024; frames++)
            {
                await session.WaitForTrafficAsync(budget.Token);
                WorldFrame frame = await session.ReadAsync(budget.Token);
                if (frame.Opcode == (ushort)WorldOpcode.SmsgPong)
                {
                    return; // still answering
                }
            }

            Assert.Fail("The original connection streamed 1024 packets without answering the ping or closing.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Assert.Fail("The original connection neither answered nor closed within 45 seconds of the sweep.");
        }
        catch (Exception ex) when (ex is not MockProtocolException and (EndOfStreamException or SocketException or IOException { InnerException: SocketException }))
        {
            // A clean disconnect.
        }
    }

    /// <summary>Logs the character in on a new connection (retrying while a replaced session or a claim release is still settling), then logs out.</summary>
    private static async Task ReconnectAsync(ClusterProcessFixture cluster, ulong characterGuid, uint expectedMap, string expectedScope, string? otherScope, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(90));
        Exception? last = null;
        try
        {
            while (true)
            {
                try
                {
                    await using WorldClient fresh = await cluster.ConnectGatewayClientAsync(budget.Token);
                    var replacement = new ScenarioConnection(fresh);
                    MockCharacter again = Assert.Single(await replacement.EnumerateAsync(budget.Token));
                    Assert.Equal(characterGuid, again.Guid);
                    MockLogin relogin = await replacement.LoginAsync(again.Guid, budget.Token);
                    Assert.Equal(characterGuid, relogin.Self.Guid);
                    Assert.True(
                        cluster.Workers.Any(worker => worker.OwnedMaps.Contains((int)relogin.Location.Map)),
                        $"The character logged in on map {relogin.Location.Map}, which no worker owns.");
                    Assert.Equal(expectedMap, relogin.Location.Map);

                    // Exactly one owner while the session is online (read before the logout, which schedules the claim release): the owner row (keyed by character) names the
                    // scope of the worker that owns the map the character is on, holds no open transfer, and carries that scope's live epoch; the other worker's scope does not hold it.
                    OwnerRow owner = Assert.IsType<OwnerRow>(await ReadOwnerAsync(cluster, (long)characterGuid, budget.Token));
                    Assert.Null(owner.ActiveTransfer);
                    Assert.Equal(expectedScope, owner.Scope);
                    if (otherScope is not null)
                    {
                        Assert.NotEqual(otherScope, owner.Scope);
                    }

                    ShardRow shard = Assert.Single(await ReadShardsAsync(cluster, budget.Token), row => row.Scope == owner.Scope);
                    Assert.True(shard.Active, "The shard row of the owning worker must be active.");
                    Assert.Equal(shard.Epoch, owner.Epoch);
                    await replacement.LogoutAsync(budget.Token);
                    return;
                }
                catch (Exception ex) when (ex is MockProtocolException or EndOfStreamException or SocketException or IOException or TimeoutException)
                {
                    last = ex;
                    await Task.Delay(1000, budget.Token);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new Xunit.Sdk.XunitException($"The character could not log in again within 90 seconds of the sweep. Last failure: {last?.Message}");
        }
    }

    // ---- characters database (PostgreSQL; table and column names are the data modules' mappings) ----

    private sealed record DecisionRow(string TransferId, int State, string? TargetScope);

    private sealed record ShardRow(string Scope, long Epoch, bool Active);

    private sealed record OwnerRow(string Scope, long Epoch, string? ActiveTransfer);

    private static Task<IReadOnlyList<DecisionRow>> ReadDecisionsAsync(ClusterProcessFixture cluster, long guid, CancellationToken ct)
        => cluster.CharactersRowsAsync(
            "SELECT transfer_id, state, target_scope FROM cluster_transfer_decision WHERE character_guid = " + guid.ToString(CultureInfo.InvariantCulture) + " ORDER BY updated_at_ms, transfer_id",
            reader => new DecisionRow(reader.GetString(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2)),
            ct);

    private static Task<IReadOnlyList<ShardRow>> ReadShardsAsync(ClusterProcessFixture cluster, CancellationToken ct)
        => cluster.CharactersRowsAsync(
            "SELECT scope, epoch, active FROM character_ownership ORDER BY scope",
            reader => new ShardRow(reader.GetString(0), reader.GetInt64(1), reader.GetBoolean(2)),
            ct);

    private static async Task<OwnerRow?> ReadOwnerAsync(ClusterProcessFixture cluster, long guid, CancellationToken ct)
    {
        IReadOnlyList<OwnerRow> rows = await cluster.CharactersRowsAsync(
            "SELECT scope, epoch, active_transfer FROM cluster_transfer_owner WHERE character_guid = " + guid.ToString(CultureInfo.InvariantCulture),
            reader => new OwnerRow(reader.GetString(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetString(2)),
            ct);
        return rows.Count == 0 ? null : rows[0];
    }

    private static Task<long> ReadCopperAsync(ClusterProcessFixture cluster, long guid, CancellationToken ct)
        => cluster.CharactersScalarAsync("SELECT \"Money\" FROM characters WHERE \"Id\" = " + guid.ToString(CultureInfo.InvariantCulture), ct);

    private static Task<long> ReadItemCountAsync(ClusterProcessFixture cluster, long guid, CancellationToken ct)
        => cluster.CharactersScalarAsync("SELECT count(*) FROM item_instance WHERE owner_guid = " + guid.ToString(CultureInfo.InvariantCulture), ct);

    private static Task<long> ReadCharacterMapAsync(ClusterProcessFixture cluster, long guid, CancellationToken ct)
        => cluster.CharactersScalarAsync("SELECT \"MapId\" FROM characters WHERE \"Id\" = " + guid.ToString(CultureInfo.InvariantCulture), ct);
}

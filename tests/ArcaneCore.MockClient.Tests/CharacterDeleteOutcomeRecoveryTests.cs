using System.Data.Common;
using ArcaneCore.Data.Characters;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

/// <summary>
/// A durable deletion whose acknowledgement is lost (the commit succeeds, then the commit call
/// throws) must not leave live state behind, and a character create that throws must answer
/// CHAR_CREATE_ERROR instead of dropping the session. Real SQLite/EF, the real world and loopback
/// clients; the faults are EF interceptors, so a real commit precedes the injected exception.
/// </summary>
public sealed class CharacterDeleteOutcomeRecoveryTests
{
    private const string Account = "DELETERECOVERY";
    private const string OtherAccount = "DELETEOTHER";
    private const string Password = "PASSWORD";
    private static readonly byte[] Success = [(byte)CharResult.CharDeleteSuccess];
    private static readonly byte[] Failed = [(byte)CharResult.CharDeleteFailed];

    [Fact]
    public async Task Delete_CommitThenAckLost_AnswersSuccessAndRunsTheFinalizers()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        CancellationToken token = deadline.Token;
        var faults = new Faults();
        var observed = new CountingHook();
        await using SyntheticArcaneServer server = await StartAsync(faults, observed, token);
        (WorldClient client, ScenarioConnection connection, ulong doomed) = await SetUpAsync(server, token);
        await using WorldClient _ = client;
        CharacterDirectory directory = server.Services.GetRequiredService<CharacterDirectory>();
        Assert.NotNull(directory.Find((int)doomed));

        faults.ArmCommitFault();
        Assert.Equal(Success, await DeleteAsync(connection, doomed, token));

        Assert.Equal(1, observed.Deleted);
        Assert.Null(directory.Find((int)doomed));
        Assert.Null(await FindRowAsync(server, doomed, token));
    }

    [Fact]
    public async Task Delete_CommitThenAckLostAndTheReconcileReadFails_AResendFinalizes()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        CancellationToken token = deadline.Token;
        var faults = new Faults();
        var observed = new CountingHook();
        await using SyntheticArcaneServer server = await StartAsync(faults, observed, token);
        (WorldClient client, ScenarioConnection connection, ulong doomed) = await SetUpAsync(server, token);
        await using WorldClient _ = client;
        CharacterDirectory directory = server.Services.GetRequiredService<CharacterDirectory>();

        faults.ArmCommitFault();
        faults.FailReconcileReads = 1;
        Assert.Equal(Failed, await DeleteAsync(connection, doomed, token)); // outcome unknown: nothing observed removed
        Assert.Equal(0, observed.Deleted);
        Assert.Null(await FindRowAsync(server, doomed, token)); // the rows really are gone

        Assert.Equal(Success, await DeleteAsync(connection, doomed, token));

        Assert.Equal(1, observed.Deleted);
        Assert.Null(directory.Find((int)doomed));
    }

    [Fact]
    public async Task CharEnum_AfterALostAck_FinalizesThePendingDeletion()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        CancellationToken token = deadline.Token;
        var faults = new Faults();
        var observed = new CountingHook();
        await using SyntheticArcaneServer server = await StartAsync(faults, observed, token);
        (WorldClient client, ScenarioConnection connection, ulong doomed) = await SetUpAsync(server, token);
        await using WorldClient _ = client;
        CharacterDirectory directory = server.Services.GetRequiredService<CharacterDirectory>();

        faults.ArmCommitFault();
        faults.FailReconcileReads = 1;
        Assert.Equal(Failed, await DeleteAsync(connection, doomed, token));
        Assert.NotNull(directory.Find((int)doomed));

        IReadOnlyList<MockCharacter> listed = await connection.EnumerateAsync(token);

        Assert.DoesNotContain(listed, c => c.Guid == doomed);
        Assert.Equal(1, observed.Deleted);
        Assert.Null(directory.Find((int)doomed));
    }

    [Fact]
    public async Task ResendFromAnotherAccount_ForAPendingDeletion_FailsAndFinalizesNothing()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        CancellationToken token = deadline.Token;
        var faults = new Faults();
        var observed = new CountingHook();
        await using SyntheticArcaneServer server = await StartAsync(faults, observed, token);
        (WorldClient client, ScenarioConnection connection, ulong doomed) = await SetUpAsync(server, token);
        await using WorldClient _ = client;
        CharacterDirectory directory = server.Services.GetRequiredService<CharacterDirectory>();
        faults.ArmCommitFault();
        faults.FailReconcileReads = 1;
        Assert.Equal(Failed, await DeleteAsync(connection, doomed, token));

        await server.AddAccountAsync(OtherAccount, Password, token);
        await using WorldClient intruder = await AuthenticateAsync(server, OtherAccount, token);
        Assert.Equal(Failed, await DeleteAsync(new ScenarioConnection(intruder), doomed, token));

        Assert.Equal(0, observed.Deleted);
        Assert.NotNull(directory.Find((int)doomed));
    }

    [Fact]
    public async Task Finalizers_RerunAfterCompleteFails_AtMostOncePerSession()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        CancellationToken token = deadline.Token;
        var faults = new Faults();
        var observed = new CountingHook();
        await using SyntheticArcaneServer server = await StartAsync(faults, observed, token);
        (WorldClient client, ScenarioConnection connection, ulong doomed) = await SetUpAsync(server, token);
        await using WorldClient _ = client;
        faults.FailCompletes = 1;

        Assert.Equal(Success, await DeleteAsync(connection, doomed, token)); // rows gone; the ledger row stays
        Assert.Equal(1, observed.Deleted);

        await connection.EnumerateAsync(token); // the sweep finalizes the still-pending operation once
        Assert.Equal(2, observed.Deleted);

        await connection.EnumerateAsync(token); // completed: nothing left to finalize
        Assert.Equal(2, observed.Deleted);
    }

    [Fact]
    public async Task CharCreate_WhenTheStoreThrows_AnswersCharCreateErrorAndKeepsTheSession()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        CancellationToken token = deadline.Token;
        var faults = new Faults();
        await using SyntheticArcaneServer server = await StartAsync(faults, new CountingHook(), token);
        await server.AddAccountAsync(Account, Password, token);
        await using WorldClient client = await AuthenticateAsync(server, Account, token);
        var connection = new ScenarioConnection(client);
        faults.FailCreateInserts = 1;

        await connection.SendAsync(WorldOpcode.CmsgCharCreate, ScenarioWire.CharacterCreate("Refused"), token);
        byte[] answer = await connection.ReadUntilAsync(WorldOpcode.SmsgCharCreate, token);

        Assert.Equal([(byte)CharResult.CharCreateError], answer);
        Assert.Empty(await connection.EnumerateAsync(token)); // still connected, nothing was stored
        await connection.CreateCharacterAsync("Accepted", token);
        Assert.Single(await connection.EnumerateAsync(token));
    }

    private static Task<SyntheticArcaneServer> StartAsync(Faults faults, CountingHook hook, CancellationToken token)
        => SyntheticArcaneServer.StartAsync(services =>
        {
            services.ConfigureDbContext<CharacterDbContext>(builder => builder.AddInterceptors(
                new FaultCommands(faults), new FaultTransactions(faults)));
            services.AddSingleton<ICharacterDeleteHook>(hook);
        }, token);

    private static async Task<(WorldClient, ScenarioConnection, ulong)> SetUpAsync(SyntheticArcaneServer server, CancellationToken token)
    {
        await server.AddAccountAsync(Account, Password, token);
        WorldClient client = await AuthenticateAsync(server, Account, token);
        var connection = new ScenarioConnection(client);
        await connection.CreateCharacterAsync("Doomed", token);
        await connection.CreateCharacterAsync("Keeper", token);
        IReadOnlyList<MockCharacter> characters = await connection.EnumerateAsync(token);
        return (client, connection, characters.Single(c => c.Name == "Doomed").Guid);
    }

    private static async Task<byte[]> DeleteAsync(ScenarioConnection connection, ulong guid, CancellationToken token)
    {
        await connection.SendAsync(WorldOpcode.CmsgCharDelete, ScenarioWire.Guid(guid), token);
        return await connection.ReadUntilAsync(WorldOpcode.SmsgCharDelete, token);
    }

    private static async Task<CharacterRecord?> FindRowAsync(SyntheticArcaneServer server, ulong guid, CancellationToken token)
    {
        await using AsyncServiceScope scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICharacterStore>().GetByIdAsync((int)guid, token);
    }

    private static async Task<WorldClient> AuthenticateAsync(SyntheticArcaneServer server, string account, CancellationToken token)
    {
        LogonResult logon = await LogonClient.AuthenticateAsync(server.RealmEndpoint, account, Password, token);
        WorldClient client = await WorldClient.ConnectAsync(Assert.Single(logon.Realms).GetLoopbackEndpoint(), token);
        Assert.Equal((byte)0x0C, await client.AuthenticateAsync(account, logon.SessionKey, token));
        return client;
    }

    /// <summary>Counts finalizer runs (OnCharacterDeletedAsync).</summary>
    private sealed class CountingHook : ICharacterDeleteHook
    {
        private int _deleted;

        public int Deleted => Volatile.Read(ref _deleted);

        public Task OnCharacterDeletedAsync(WorldSession session, CharacterRecord character)
        {
            Interlocked.Increment(ref _deleted);
            return Task.CompletedTask;
        }
    }

    private sealed class Faults
    {
        private int _commitFault;
        private int _commitPending;
        private int _commitFired;
        private int _failReconcileReads;
        private int _failCompletes;
        private int _failCreateInserts;

        /// <summary>The next transaction that deletes a characters row commits, then throws.</summary>
        public void ArmCommitFault() => Volatile.Write(ref _commitFault, 1);

        public int FailReconcileReads { set => Volatile.Write(ref _failReconcileReads, value); }

        public int FailCompletes { set => Volatile.Write(ref _failCompletes, value); }

        public int FailCreateInserts { set => Volatile.Write(ref _failCreateInserts, value); }

        public void Observe(string sql)
        {
            if (sql.Contains("DELETE FROM \"characters\"", StringComparison.Ordinal)
                && Interlocked.CompareExchange(ref _commitFault, 0, 1) == 1)
            {
                Volatile.Write(ref _commitPending, 1);
            }

            // Only reads issued after the lost acknowledgement: the deletion transaction's own
            // checks (module cleanups, create fence) must run normally.
            if (Volatile.Read(ref _commitFired) != 0
                && sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && sql.Contains("character_deletion", StringComparison.Ordinal)
                && TryTake(ref _failReconcileReads))
            {
                throw new IOException("injected: reconciliation read failed");
            }

            if (sql.Contains("DELETE FROM \"character_deletion\"", StringComparison.Ordinal) && TryTake(ref _failCompletes))
            {
                throw new IOException("injected: completion failed");
            }

            if (sql.Contains("INSERT INTO \"characters\"", StringComparison.Ordinal) && TryTake(ref _failCreateInserts))
            {
                throw new IOException("injected: character insert failed");
            }
        }

        public void AfterCommit()
        {
            if (Interlocked.CompareExchange(ref _commitPending, 0, 1) == 1)
            {
                Volatile.Write(ref _commitFired, 1);
                throw new IOException("injected: commit acknowledgement lost");
            }
        }

        private static bool TryTake(ref int counter)
        {
            while (true)
            {
                int current = Volatile.Read(ref counter);
                if (current <= 0)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref counter, current - 1, current) == current)
                {
                    return true;
                }
            }
        }
    }

    private sealed class FaultCommands(Faults faults) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            faults.Observe(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            faults.Observe(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class FaultTransactions(Faults faults) : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            faults.AfterCommit();
            return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
        }
    }
}

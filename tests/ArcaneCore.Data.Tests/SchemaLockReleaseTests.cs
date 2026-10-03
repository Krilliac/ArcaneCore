using System.Diagnostics.CodeAnalysis;
using System.Data;
using System.Data.Common;
using ArcaneCore.Data.Schema;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The release of a MariaDB schema lock runs while an exception from the bootstrap is already
/// propagating. A release that fails the way MySqlConnector fails on a connection still busy with
/// the failed command (InvalidOperationException) must not replace that exception. Proved against a
/// fake connection, so it needs no server.
/// </summary>
public sealed class SchemaLockReleaseTests
{
    [Fact]
    public async Task FailedRelease_DoesNotMaskTheBootstrapFailure_AndEndsTheSession()
    {
        var connection = new FakeLockConnection();
        var logger = new CapturingLogger();
        await using DbContext db = CreateMariaDbContext(connection);

        var original = new SchemaMismatchException("original bootstrap failure");
        SchemaMismatchException thrown = await Assert.ThrowsAsync<SchemaMismatchException>(async () =>
        {
            await using SchemaLock held = await SchemaLock.AcquireAsync(db, "auth", TimeSpan.FromSeconds(5), CancellationToken.None, logger);
            throw original;
        });

        Assert.Same(original, thrown);
        Assert.True(connection.ReleaseAttempts == 1, "the lock release was not attempted exactly once");
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task SuccessfulRelease_LeavesTheConnectionOpen()
    {
        var connection = new FakeLockConnection { FailRelease = false };
        await using DbContext db = CreateMariaDbContext(connection);

        await using (await SchemaLock.AcquireAsync(db, "auth", TimeSpan.FromSeconds(5), CancellationToken.None))
        {
        }

        Assert.Equal(1, connection.ReleaseAttempts);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    private static DbContext CreateMariaDbContext(FakeLockConnection connection)
    {
        var options = new DbContextOptionsBuilder<DbContext>()
            .UseMySql(connection, new MariaDbServerVersion(new Version(10, 11, 0)))
            .Options;
        return new DbContext(options);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, exception));
    }

    /// <summary>GET_LOCK succeeds; RELEASE_LOCK throws what MySqlConnector throws on a connection that is still in use.</summary>
    private sealed class FakeLockConnection : DbConnection
    {
        private ConnectionState _state = ConnectionState.Open;

        public bool FailRelease { get; init; } = true;

        public int ReleaseAttempts { get; set; }

        [AllowNull]
        public override string ConnectionString { get; set; } = "Server=fake;Database=fake";

        public override string Database => "fake";

        public override string DataSource => "fake";

        public override string ServerVersion => "10.11.0-MariaDB";

        public override ConnectionState State => _state;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        public override void Close() => _state = ConnectionState.Closed;

        public override void Open() => _state = ConnectionState.Open;

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => new FakeCommand(this);
    }

    private sealed class FakeCommand(FakeLockConnection owner) : DbCommand
    {
        private readonly SqliteCommand _parameters = new();

        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;

        public override int CommandTimeout { get; set; }

        public override CommandType CommandType { get; set; }

        public override bool DesignTimeVisible { get; set; }

        public override UpdateRowSource UpdatedRowSource { get; set; }

        protected override DbConnection? DbConnection { get; set; } = owner;

        protected override DbParameterCollection DbParameterCollection => _parameters.Parameters;

        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel()
        {
        }

        public override int ExecuteNonQuery() => throw new NotSupportedException(CommandText);

        public override object? ExecuteScalar()
        {
            if (CommandText.Contains("RELEASE_LOCK", StringComparison.Ordinal))
            {
                owner.ReleaseAttempts++;
                if (owner.FailRelease)
                {
                    throw new InvalidOperationException("This MySqlConnection is already in use.");
                }

                return 1L;
            }

            if (CommandText.Contains("GET_LOCK", StringComparison.Ordinal))
            {
                return 1L;
            }

            throw new NotSupportedException(CommandText);
        }

        public override void Prepare()
        {
        }

        protected override DbParameter CreateDbParameter() => _parameters.CreateParameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException(CommandText);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _parameters.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

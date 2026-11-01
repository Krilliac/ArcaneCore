using System.Data;
using System.Data.Common;
using ArcaneCore.Data.ClientData;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// Each call site that used to open a raw connection (<c>DbcCrossReferences.RunAsync</c>, <c>CharacterOwnershipGuard</c>'s fence lock,
/// <c>SchemaCatalog</c>'s connection lease) is driven with a connection in <see cref="ConnectionState.Broken"/>. A raw
/// <c>OpenAsync</c> on it would throw; routed through <see cref="DbConnectionOpen"/> it is closed, then opened.
/// </summary>
public sealed class DbConnectionOpenSiteTests
{
    private sealed class BrokenAwareConnection : DbConnection
    {
        private ConnectionState _state = ConnectionState.Broken;

        public List<string> Calls { get; } = [];

        public override ConnectionState State => _state;

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = "Server=offline.invalid;Database=x";

        public override string Database => "fake";

        public override string DataSource => "fake";

        public override string ServerVersion => "0";

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        public override void Close()
        {
            Calls.Add("Close");
            _state = ConnectionState.Closed;
        }

        public override void Open()
        {
            Calls.Add("Open");
            if (_state == ConnectionState.Broken)
            {
                throw new InvalidOperationException("The connection is broken and must be closed before it can be opened.");
            }

            _state = ConnectionState.Open;
        }

        public override Task OpenAsync(CancellationToken cancellationToken)
        {
            Open();
            return Task.CompletedTask;
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => new ScalarCommand(this);
    }

    /// <summary>A command with no parameters that answers every scalar query with 1.</summary>
    private sealed class ScalarCommand(BrokenAwareConnection connection) : DbCommand
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get; set; } = string.Empty;

        public override int CommandTimeout { get; set; }

        public override CommandType CommandType { get; set; }

        public override bool DesignTimeVisible { get; set; }

        public override UpdateRowSource UpdatedRowSource { get; set; }

        protected override DbConnection? DbConnection { get => connection; set => throw new NotSupportedException(); }

        protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();

        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel()
        {
        }

        public override int ExecuteNonQuery() => throw new NotSupportedException();

        public override object? ExecuteScalar() => 1L;

        public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken) => Task.FromResult<object?>(1L);

        public override void Prepare()
        {
        }

        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
    }

    private sealed class FakeContext(BrokenAwareConnection connection) : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder.UseMySql(connection, new MariaDbServerVersion(new Version(10, 11, 0)));
    }

    [Fact]
    public async Task DbcCrossReferences_RunAsync_RecoversABrokenConnection()
    {
        var connection = new BrokenAwareConnection();

        await DbcCrossReferences.RunAsync(connection, Path.GetTempPath(), references: []);

        Assert.Equal(["Close", "Open"], connection.Calls);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task CharacterOwnershipGuard_FenceLock_RecoversABrokenConnection()
    {
        var connection = new BrokenAwareConnection();
        await using var db = new FakeContext(connection);

        bool locked = await CharacterOwnershipGuard.LockSharedAsync(db, connection, null, null, CancellationToken.None);

        Assert.True(locked);
        Assert.Equal(["Close", "Open"], connection.Calls);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task SchemaCatalog_ConnectionLease_RecoversABrokenConnection()
    {
        var connection = new BrokenAwareConnection();
        await using var db = new FakeContext(connection);

        long value = await SchemaCatalog.ScalarAsync(db, "SELECT 1", CancellationToken.None);

        Assert.Equal(1L, value);
        // The lease opened it, so it closes it again when disposed.
        Assert.Equal(["Close", "Open", "Close"], connection.Calls);
    }
}

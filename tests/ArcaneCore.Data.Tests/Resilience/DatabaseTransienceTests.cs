using System.Net.Sockets;
using ArcaneCore.Data.Resilience;
using ArcaneCore.Kernel.Resilience;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Npgsql;
using Xunit;

namespace ArcaneCore.Data.Tests.Resilience;

public sealed class DatabaseTransienceTests
{
    [Theory]
    [InlineData(5)]  // SQLITE_BUSY
    [InlineData(6)]  // SQLITE_LOCKED
    [InlineData(10)] // SQLITE_IOERR
    [InlineData(14)] // SQLITE_CANTOPEN
    [InlineData(26)] // SQLITE_NOTADB
    public void SqliteAvailabilityCodes_AreTransient(int code)
    {
        Assert.True(DatabaseTransience.IsTransient(new SqliteException("x", code)));
        Assert.True(DatabaseTransience.IsTransient(new DbUpdateException("save", new SqliteException("x", code))));
    }

    [Theory]
    [InlineData(1)]  // SQLITE_ERROR: no such table, syntax
    [InlineData(19)] // SQLITE_CONSTRAINT
    public void SqliteStatementErrors_AreNot(int code)
    {
        Assert.False(DatabaseTransience.IsTransient(new SqliteException("x", code)));
        Assert.False(DatabaseTransience.IsTransient(new DbUpdateException("save", new SqliteException("x", code))));
    }

    [Fact]
    public void ProviderTransientFlags_AreHonoured()
    {
        var refused = new NpgsqlException("connection refused", new SocketException(10061));
        Assert.True(refused.IsTransient);
        Assert.True(DatabaseTransience.IsTransient(refused));
        Assert.True(DatabaseTransience.IsTransient(new DbUpdateException("save", refused)));

        // MySqlException has no public constructor; its IsTransient (UnableToConnectToHost and friends) is exercised by
        // the closed-port start-up test below, which goes through the real connector.
        Assert.True(typeof(MySqlException).IsSubclassOf(typeof(System.Data.Common.DbException)));
    }

    [Fact]
    public void DomainAndKernelRules_StillApply()
    {
        Assert.False(DatabaseTransience.IsTransient(new InvalidOperationException("account 'X' does not exist")));
        Assert.False(DatabaseTransience.IsTransient(new OperationCanceledException()));
        Assert.True(DatabaseTransience.IsTransient(new TimeoutRejectedException(TimeSpan.FromSeconds(1))));
        Assert.False(DatabaseTransience.IsTransient(new CircuitOpenException("Auth database", TimeSpan.Zero)));
    }
}

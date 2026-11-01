using System.Data.Common;
using ArcaneCore.Data.Schema.Upgrade.Cli;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The exit code <c>arcane-db</c> maps each database failure to, through the internal <c>DbUpgradeCli.ClassifyFailure</c> seam:
/// lock waits are 7, a connection the server refused for good (credentials, database name) is 2 (configuration, never restarted),
/// a transient connection failure is 6, anything else the server said about a step is 1.
/// </summary>
public sealed class DbUpgradeCliExitTests
{
    private const string Password = "exit-map-marker";
    private const string ConnectionString = "Server=db.example;Port=5432;Database=w;User Id=arcane;Password=" + Password;

    private sealed class StateDbException(string message, string? sqlState, bool transient = false) : DbException(message)
    {
        public override string? SqlState { get; } = sqlState;

        public override bool IsTransient => transient;
    }

    private static (int ExitCode, string Message) Classify(Exception exception)
        => DbUpgradeCli.ClassifyFailure(
            new DatabaseOptions { Provider = DatabaseProvider.PostgreSql, ConnectionString = ConnectionString }, "world", exception);

    // MySqlException has no public constructor (MySqlConnector 2.4.0), so the internal (MySqlErrorCode, string) one is reached by reflection.
    private static MySqlConnector.MySqlException MySqlError(MySqlConnector.MySqlErrorCode errorCode, string message)
        => (MySqlConnector.MySqlException)(System.Activator.CreateInstance(
            typeof(MySqlConnector.MySqlException),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
            binder: null,
            args: [errorCode, message],
            culture: null)!);

    [Theory]
    [InlineData("40001")]
    [InlineData("40P01")]
    [InlineData("55P03")]
    public void ALockWaitTheDatabaseGaveUpOn_IsLockTimeout_EvenWhenTheDriverCallsItTransient(string sqlState)
    {
        (int code, string message) = Classify(new StateDbException("deadlock detected, Password=" + Password, sqlState, transient: true));

        Assert.Equal(DbUpgradeExitCodes.LockTimeout, code);
        Assert.DoesNotContain(Password, message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALockWait_WrappedInAnotherException_IsStillLockTimeout()
    {
        (int code, _) = Classify(new InvalidOperationException("step failed", new StateDbException("lock not available", "55P03")));

        Assert.Equal(DbUpgradeExitCodes.LockTimeout, code);
    }

    [Fact]
    public void ASqliteBusyDatabase_IsLockTimeout()
    {
        (int code, _) = Classify(new Microsoft.Data.Sqlite.SqliteException("database is locked", 5));

        Assert.Equal(DbUpgradeExitCodes.LockTimeout, code);
    }

    [Theory]
    [InlineData("28000")]
    [InlineData("28P01")]
    [InlineData("28001")]
    [InlineData("3D000")]
    public void ACredentialOrDatabaseNameRefusal_IsUsage_NotUnreachable(string sqlState)
    {
        (int code, string message) = Classify(new StateDbException("password authentication failed, Password=" + Password, sqlState));

        Assert.Equal(DbUpgradeExitCodes.Usage, code);
        Assert.DoesNotContain(Password, message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMySqlLockWaitTimeout_IsLockTimeout_EvenThoughTheDriverCallsItTransient()
    {
        (int code, string message) = Classify(MySqlError(
            MySqlConnector.MySqlErrorCode.LockWaitTimeout, "Lock wait timeout exceeded; try restarting transaction"));

        Assert.Equal(DbUpgradeExitCodes.LockTimeout, code);
        Assert.DoesNotContain("could not be reached", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMySqlUnknownDatabase_IsUsage()
    {
        (int code, _) = Classify(MySqlError(
            MySqlConnector.MySqlErrorCode.UnknownDatabase, "Unknown database 'w'"));

        Assert.Equal(DbUpgradeExitCodes.Usage, code);
    }

    [Fact]
    public void ATransientConnectionFailure_IsStillUnreachable()
    {
        (int code, _) = Classify(new StateDbException("connection refused", "08006", transient: true));

        Assert.Equal(DbUpgradeExitCodes.Unreachable, code);
    }

    [Fact]
    public void AnUnclassifiedServerError_IsFailure()
    {
        (int code, _) = Classify(new StateDbException("syntax error", "42601"));

        Assert.Equal(DbUpgradeExitCodes.Failure, code);
    }
}

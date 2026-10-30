using System.Data.Common;
using System.Net.Sockets;
using System.Reflection;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Schema.Upgrade.Cli;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The unreachable-server fixtures use a refused loopback port (see <see cref="ClosedLoopbackPort"/>), so they no longer reach
/// the driver's connect-timeout path. These tests feed synthetic timeout-shaped exceptions straight into the classifiers
/// (private, so by reflection) to keep that path covered: still "unreachable"/database failure, never echoing the password.
/// </summary>
public sealed class UnreachableClassificationTests
{
    private const string Password = "hunter2-timeout-marker";
    private const string ConnectionString = "Server=db.example;Port=3306;Database=w;User Id=arcane;Password=" + Password + ";Connection Timeout=2";

    private sealed class TimeoutDbException(string message) : DbException(message)
    {
        public override bool IsTransient => true;
    }

    private sealed class ServerRejectedDbException(string message) : DbException(message);

    public static TheoryData<string> TimeoutShapes => new()
    {
        "timeout",
        "dbexception",
        "wrapped-timeout",
        "socket",
    };

    private static Exception Build(string shape)
    {
        string text = "Connect Timeout expired for " + ConnectionString;
        return shape switch
        {
            "timeout" => new TimeoutException(text),
            "dbexception" => new TimeoutDbException(text),
            "wrapped-timeout" => new InvalidOperationException("outer failure", new TimeoutException(text)),
            _ => new SocketException((int)SocketError.TimedOut),
        };
    }

    [Theory]
    [MemberData(nameof(TimeoutShapes))]
    public void DbUpgradeCli_ClassifiesTimeoutShapedFailuresAsUnreachable_WithoutThePassword(string shape)
    {
        Type run = typeof(DbUpgradeCli).GetNestedType("Run", BindingFlags.NonPublic)!;
        var options = new DatabaseOptions { Provider = DatabaseProvider.MariaDb, ConnectionString = ConnectionString };
        object instance = Activator.CreateInstance(run, options, TextWriter.Null, TextWriter.Null)!;
        MethodInfo classify = run.GetMethod("Classify", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var failure = (Exception)classify.Invoke(instance, ["auth", Build(shape)])!;
        int exitCode = (int)failure.GetType().GetProperty("ExitCode")!.GetValue(failure)!;

        Assert.Equal(DbUpgradeExitCodes.Unreachable, exitCode);
        Assert.StartsWith("auth:", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ConnectionString, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DbUpgradeCli_ClassifiesAServerRejectedStep_AsFailure_NotUnreachable()
    {
        Type run = typeof(DbUpgradeCli).GetNestedType("Run", BindingFlags.NonPublic)!;
        var options = new DatabaseOptions { Provider = DatabaseProvider.MariaDb, ConnectionString = ConnectionString };
        object instance = Activator.CreateInstance(run, options, TextWriter.Null, TextWriter.Null)!;
        MethodInfo classify = run.GetMethod("Classify", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var failure = (Exception)classify.Invoke(instance, ["world", new ServerRejectedDbException("Specified key was too long; max key length is 3072 bytes")])!;
        int exitCode = (int)failure.GetType().GetProperty("ExitCode")!.GetValue(failure)!;

        Assert.Equal(DbUpgradeExitCodes.Failure, exitCode);
        Assert.DoesNotContain("could not be reached", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(TimeoutShapes))]
    public void ContentImporter_DatabaseError_RedactsThePassword_ForTimeoutShapedFailures(string shape)
    {
        Type importer = typeof(ContentImporterCli);
        Type target = importer.GetNestedType("Target", BindingFlags.NonPublic)!;
        object instance = Activator.CreateInstance(target, DatabaseProvider.MariaDb, ConnectionString, null)!;
        MethodInfo databaseError = importer.GetMethod("DatabaseError", BindingFlags.NonPublic | BindingFlags.Static)!;

        var failure = (Exception)databaseError.Invoke(null, [Build(shape), instance])!;
        int exitCode = (int)failure.GetType().GetProperty("ExitCode")!.GetValue(failure)!;

        Assert.Equal(ExitCodes.Database, exitCode);
        Assert.DoesNotContain(Password, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ConnectionString, failure.Message, StringComparison.Ordinal);
    }
}

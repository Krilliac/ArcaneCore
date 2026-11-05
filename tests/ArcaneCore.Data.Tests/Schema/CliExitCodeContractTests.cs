using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Schema.Upgrade.Cli;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The numeric exit codes of <c>arcane-db</c> and <c>arcane-content-importer</c>. Operators and scripts read them from each tool's usage
/// text and docs/ops/database-upgrade.md (DbUpgradeExitCodes.cs lists them). Two in-repo scripts branch on specific values: pg-cutover.ps1
/// counts a table that differs on <c>arcane-db verify</c> by exit 9 (any other non-zero code stops the cutover), and verify-classic-db.ps1
/// requires importer verify to exit 6 (anything else throws). The other scripts only test zero versus non-zero. This pins the numbers so
/// they do not change when the CLI code moves between assemblies.
/// </summary>
public sealed class CliExitCodeContractTests
{
    [Fact]
    public void DbUpgradeExitCodes_KeepTheirNumbers()
    {
        Assert.Equal(0, DbUpgradeExitCodes.Ok);
        Assert.Equal(1, DbUpgradeExitCodes.Failure);
        Assert.Equal(2, DbUpgradeExitCodes.Usage);
        Assert.Equal(3, DbUpgradeExitCodes.UpgradePending);
        Assert.Equal(4, DbUpgradeExitCodes.Refused);
        Assert.Equal(5, DbUpgradeExitCodes.Drift);
        Assert.Equal(6, DbUpgradeExitCodes.Unreachable);
        Assert.Equal(7, DbUpgradeExitCodes.LockTimeout);
        Assert.Equal(8, DbUpgradeExitCodes.BackupNotConfirmed);
        Assert.Equal(9, DbUpgradeExitCodes.CopyVerifyFailed);
    }

    [Fact]
    public void ContentImporterExitCodes_KeepTheirNumbers()
    {
        Assert.Equal(0, ExitCodes.Ok);
        Assert.Equal(1, ExitCodes.Io);
        Assert.Equal(2, ExitCodes.Usage);
        Assert.Equal(3, ExitCodes.Schema);
        Assert.Equal(4, ExitCodes.Database);
        Assert.Equal(5, ExitCodes.RepositoryPath);
        Assert.Equal(6, ExitCodes.Verify);
    }
}

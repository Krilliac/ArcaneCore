using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Schema.Upgrade.Cli;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The numeric exit codes of <c>arcane-db</c> and <c>arcane-content-importer</c>. run-gm-commands.ps1, dev-runner.ps1 and the deploy
/// scripts branch on these values, so they must not change when the CLI code moves between assemblies.
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

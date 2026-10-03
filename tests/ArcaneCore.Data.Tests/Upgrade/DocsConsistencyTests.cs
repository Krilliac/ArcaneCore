using System.Reflection;
using ArcaneCore.Data.Schema.Upgrade;
using ArcaneCore.Data.Schema.Upgrade.Cli;
using Xunit;

namespace ArcaneCore.Data.Tests.Upgrade;

/// <summary>
/// The operator runbook must name every exit code, command, option and configuration key the tool has: adding one
/// without documenting it fails here. The runbook is found by walking up from the test binary; not finding it is a
/// failure, not a skip (a check that cannot read its input must not pass).
/// </summary>
public sealed class DocsConsistencyTests
{
    private static string ReadRunbook()
    {
        for (string? dir = AppContext.BaseDirectory; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            string candidate = Path.Combine(dir, "docs", "ops", "database-upgrade.md");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new Xunit.Sdk.XunitException("docs/ops/database-upgrade.md was not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Runbook_NamesEveryExitCode_InItsTable()
    {
        string runbook = ReadRunbook();
        int[] codes = [.. typeof(DbUpgradeExitCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral).Select(f => (int)f.GetRawConstantValue()!)];
        Assert.True(codes.Length >= 9);
        foreach (int code in codes)
        {
            Assert.Contains($"| {code} |", runbook, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Runbook_NamesEveryCommandAndOption()
    {
        string runbook = ReadRunbook();
        Assert.NotEmpty(DbUpgradeCli.Commands);
        foreach (string command in DbUpgradeCli.Commands)
        {
            Assert.Contains($"`{command}`", runbook, StringComparison.Ordinal);
        }

        foreach (string option in DbUpgradeCli.Options)
        {
            Assert.Contains(option, runbook, StringComparison.Ordinal);
        }

        // The usage text the tool prints names the same set.
        foreach (string option in DbUpgradeCli.Options)
        {
            Assert.Contains(option, DbUpgradeCli.Usage, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Runbook_NamesEveryUpgradeConfigurationKey()
    {
        string runbook = ReadRunbook();
        string[] keys = [.. typeof(DatabaseUpgradeOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name)];
        Assert.Equal(["LockTimeoutSeconds", "Policy"], keys.Order(StringComparer.Ordinal));
        foreach (string key in keys)
        {
            Assert.Contains($"Database:Upgrade:{key}", runbook, StringComparison.Ordinal);
        }

        foreach (string policy in Enum.GetNames<SchemaPolicy>())
        {
            Assert.Contains($"`{policy}`", runbook, StringComparison.Ordinal);
        }
    }
}

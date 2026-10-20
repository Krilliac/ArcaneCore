using Xunit;

namespace ArcaneCore.Kernel.Tests.Ops;

/// <summary>
/// A fact that is skipped (reported as Skipped, not Failed) unless <c>ARCANECORE_REPEAT_TEST_SELFTEST=1</c>. Only
/// tools/ops/ci/repeat-test.ps1's self-test sets it.
/// </summary>
public sealed class RepeatTestSelfTestFactAttribute : FactAttribute
{
    public RepeatTestSelfTestFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(RepeatTestSelfTest.EnableVariable) != "1")
        {
            Skip = $"opt-in: set {RepeatTestSelfTest.EnableVariable}=1 (tools/ops/ci/repeat-test.ps1 self-test).";
        }
    }
}

/// <summary>
/// Proves tools/ops/ci/repeat-test.ps1 detects failure. <see cref="Flaky"/> fails on purpose about half the time: the outcome is a
/// pure function of <c>ARCANECORE_REPEAT_TEST_SELFTEST_SEED</c> and the script's per-run <c>ARCANECORE_REPEAT_RUN_INDEX</c>, so a
/// given (seed, run) pair always gives the same result. <see cref="Stable"/> always passes. Both are skipped in normal runs.
/// </summary>
public sealed class RepeatTestSelfTest
{
    internal const string EnableVariable = "ARCANECORE_REPEAT_TEST_SELFTEST";

    private const string SeedVariable = "ARCANECORE_REPEAT_TEST_SELFTEST_SEED";
    private const string RunIndexVariable = "ARCANECORE_REPEAT_RUN_INDEX";

    private static int ParseOrZero(string variable) =>
        int.TryParse(Environment.GetEnvironmentVariable(variable), out int value) ? value : 0;

    [RepeatTestSelfTestFact]
    public void Flaky()
    {
        int seed = ParseOrZero(SeedVariable);
        int run = ParseOrZero(RunIndexVariable);
        // A fixed integer mix, not HashCode.Combine (randomly seeded per process) or Random (patterned for consecutive seeds).
        ulong mixed = unchecked((ulong)((seed * 1_000_003L) + run) * 0x9E3779B97F4A7C15UL);
        bool fail = ((mixed >> 40) & 1UL) == 0UL;
        Assert.False(fail, $"deliberate self-test failure (seed={seed}, run={run}, probability 1/2).");
    }

    [RepeatTestSelfTestFact]
    public void Stable()
    {
        Assert.True(true);
    }
}

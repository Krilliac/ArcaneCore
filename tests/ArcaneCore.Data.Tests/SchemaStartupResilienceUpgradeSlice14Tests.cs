using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// Share 14 of 16 of the interrupted-upgrade-step theory (every 16th row of each provider's block of <see cref="SchemaStartupResilienceTestBase.UpgradeSteps"/>, so every share carries the same mix of providers): a fault at each DDL statement of an upgrade step, then a restart that must converge.
/// Split from the former <c>SchemaStartupResilienceTests</c> so the rows run in parallel and each class stays under the gate's class budget; the body is <see cref="SchemaStartupResilienceTestBase"/>'s.
/// </summary>
public sealed class SchemaStartupResilienceUpgradeSlice14Tests : SchemaStartupResilienceTestBase
{
    public static IEnumerable<object[]> Rows() => UpgradeStepsSlice(13, UpgradeSliceCount);

    [Theory]
    [MemberData(nameof(Rows))]
    public Task InterruptedUpgradeStep_ResumesOnRestart(DatabaseProvider provider, string component, int stepVersion) => RunInterruptedUpgradeStepAsync(provider, component, stepVersion);
}

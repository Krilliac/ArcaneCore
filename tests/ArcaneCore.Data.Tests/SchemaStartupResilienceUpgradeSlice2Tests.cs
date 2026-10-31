using System.Data.Common;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// Share 2 of 4 of the interrupted-upgrade-step theory (every 4th row of <see cref="SchemaStartupResilienceTestBase.UpgradeSteps"/>): a fault at each DDL statement of an upgrade step, then a restart that must converge.
/// Split from the former <c>SchemaStartupResilienceTests</c> so the rows run in parallel; the body is <see cref="SchemaStartupResilienceTestBase"/>'s.
/// </summary>
public sealed class SchemaStartupResilienceUpgradeSlice2Tests : SchemaStartupResilienceTestBase
{
    public static IEnumerable<object[]> Rows() => UpgradeStepsSlice(1, 4);

    [Theory]
    [MemberData(nameof(Rows))]
    public Task InterruptedUpgradeStep_ResumesOnRestart(DatabaseProvider provider, string component, int stepVersion) => RunInterruptedUpgradeStepAsync(provider, component, stepVersion);
}

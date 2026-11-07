using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// Share 5 of 6 of the interrupted-fresh-create theory (every 6th row of <see cref="SchemaStartupResilienceTestBase.FreshCreateCases"/>); share 1 is <see cref="SchemaStartupResilienceTests"/>.
/// Split so the rows run in parallel and each class stays under the gate's class budget; the body is <see cref="SchemaStartupResilienceTestBase"/>'s.
/// </summary>
public sealed class SchemaStartupResilienceFreshCreateShare5Tests : SchemaStartupResilienceTestBase
{
    public static IEnumerable<object[]> Rows() => FreshCreateSlice(4, FreshCreateClassCount);

    [Theory]
    [MemberData(nameof(Rows))]
    public Task InterruptedFreshCreate_ResumesOnRestart(DatabaseProvider provider, string component, int shard, int shards) => RunInterruptedFreshCreateAsync(provider, component, shard, shards);
}

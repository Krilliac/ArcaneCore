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
/// Share 2 of 2 of the interrupted-fresh-create theory (every 2nd row of <see cref="SchemaStartupResilienceTestBase.ProviderComponents"/>); share 1 is <see cref="SchemaStartupResilienceTests"/>.
/// Split so the rows run in parallel and the class stays under the gate's class budget; the body is <see cref="SchemaStartupResilienceTestBase"/>'s.
/// </summary>
public sealed class SchemaStartupResilienceFreshCreateShare2Tests : SchemaStartupResilienceTestBase
{
    public static IEnumerable<object[]> Rows() => ProviderComponentsSlice(1, 2);

    [Theory]
    [MemberData(nameof(Rows))]
    public Task InterruptedFreshCreate_ResumesOnRestart(DatabaseProvider provider, string component) => RunInterruptedFreshCreateAsync(provider, component);
}

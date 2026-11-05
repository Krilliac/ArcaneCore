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
/// Startup-resilience cases for a fresh create (a fault after each DDL statement of a first start, then a restart that must converge; share 1 of 6; the others are <c>SchemaStartupResilienceFreshCreateShare2Tests</c> to <c>Share6Tests</c>). The databases that already exist
/// (empty, historic create) and the repeated, concurrent and rolled-back starts are in <see cref="SchemaStartupResilienceExistingDatabaseTests"/> and <see cref="SchemaStartupResilienceRestartTests"/>;
/// the interrupted-upgrade-step theory runs in the 16 <c>SchemaStartupResilienceUpgradeSlice*Tests</c> classes. Split from the former single class so each stays under the gate's class budget;
/// the setup and helpers are in <see cref="SchemaStartupResilienceTestBase"/>.
/// </summary>
public sealed class SchemaStartupResilienceTests : SchemaStartupResilienceTestBase
{
    public static IEnumerable<object[]> FreshCreateRows() => FreshCreateSlice(0, FreshCreateClassCount);

    [Theory]
    [MemberData(nameof(FreshCreateRows))]
    public Task InterruptedFreshCreate_ResumesOnRestart(DatabaseProvider provider, string component, int shard, int shards) => RunInterruptedFreshCreateAsync(provider, component, shard, shards);

    [Fact]
    public void Shorten_TakesTheLengthAfterLineEndingsShrink()
    {
        // "\r\n" becomes one space, so the replaced text is shorter than the original: slicing it with the
        // original length throws ArgumentOutOfRangeException and hides the failure being reported.
        Assert.Equal("a b", Shorten("a\r\nb", 90));
        Assert.Equal("a b c", Shorten("a\r\nb\r\nc", 90));
        Assert.Equal("a b", Shorten("a\r\nb\r\ncdefgh", 3));
    }
}

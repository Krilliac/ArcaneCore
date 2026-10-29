using Xunit;

namespace ArcaneCore.Game.Tests.HotCode;

/// <summary>
/// Tests that commit or compare against the process-wide <see cref="ArcaneCore.Game.Maps.DefaultMapUpdaters"/> set.
/// A commit swaps that set for every map created in the test process, so these run alone, never beside other tests.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DefaultMapUpdatersCollection
{
    public const string Name = "DefaultMapUpdaters process-wide set";
}

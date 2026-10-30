using Xunit;

namespace ArcaneCore.World.Tests;

/// <summary>
/// Tests that write or assert the process-global <c>ExitCodes.Current</c> run one at a time and alone: another test class setting it between a
/// write and its assertion would make them flaky.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ExitCodesCollection
{
    public const string Name = "ExitCodes";
}

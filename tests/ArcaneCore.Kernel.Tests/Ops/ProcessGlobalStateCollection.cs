using Xunit;

namespace ArcaneCore.Kernel.Tests.Ops;

/// <summary>
/// Tests that change process-global state (environment variables, <c>ExitCodes.Current</c>) run alone: a collection with parallelization
/// disabled does not overlap any other test class.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessGlobalStateCollection
{
    public const string Name = "ProcessGlobalState";
}

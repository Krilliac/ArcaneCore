using Xunit;

namespace ArcaneCore.Kernel.Tests.Ops;

/// <summary>
/// A started <c>MetricsStore</c> attaches a process-wide <c>MeterListener</c> that enables every <c>ArcaneCore*</c> meter, so the
/// classes that start one must not overlap: <c>MetricsRoleTests</c> asserts no listener outlives its owner.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MetricsStoreCollection
{
    public const string Name = "MetricsStore";
}

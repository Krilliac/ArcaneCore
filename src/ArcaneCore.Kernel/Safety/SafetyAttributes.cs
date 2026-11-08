namespace ArcaneCore.Kernel.Safety;

// Markers read by the ArcaneCore.Analyzers safety rules (docs/ops/memory-safety.md). They carry no behaviour at run time;
// the analyzer matches them by full name, so a project needs no reference to the analyzer assembly to use them.

/// <summary>
/// The type (and every type derived from it) or member is owned by the world thread: reading or changing it from a thread-pool
/// callback, a <c>Task.Run</c>/<c>ContinueWith</c>/timer lambda, or code after <c>await ...ConfigureAwait(false)</c> is a data race
/// (the analyzer reports ARC0001/ARC0002). Hop back with a <see cref="RunsOnWorldThreadAttribute"/> method such as
/// <c>WorldRuntime.Post</c> before touching it. Members that are immutable or otherwise thread-safe opt out with <see cref="AnyThreadAttribute"/>.
/// </summary>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Method
    | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Event | AttributeTargets.Constructor,
    AllowMultiple = false, Inherited = false)]
public sealed class WorldThreadOnlyAttribute : Attribute
{
}

/// <summary>A member of a <see cref="WorldThreadOnlyAttribute"/> type that is safe from any thread (immutable state, or internally synchronised).</summary>
[AttributeUsage(
    AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Event | AttributeTargets.Constructor,
    AllowMultiple = false, Inherited = false)]
public sealed class AnyThreadAttribute : Attribute
{
}

/// <summary>
/// On a method: every delegate argument it takes runs on the world thread. On a parameter: that delegate argument does. A lambda passed
/// there may touch <see cref="WorldThreadOnlyAttribute"/> members even when it is written inside a <c>Task.Run</c> lambda.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class RunsOnWorldThreadAttribute : Attribute
{
}

/// <summary>
/// Opt-in to an API the safety rules ban (<c>Unsafe</c>, <c>MemoryMarshal</c>, <c>NativeMemory</c>, <c>Marshal</c>, P/Invoke, <c>AllowUnsafeBlocks</c>)
/// on the member, type or assembly it marks. <see cref="Reason"/> is the audit record: why it is needed and what keeps it safe.
/// </summary>
[AttributeUsage(
    AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method | AttributeTargets.Property
    | AttributeTargets.Constructor | AttributeTargets.Field,
    AllowMultiple = false, Inherited = false)]
public sealed class AuditedUnsafeAttribute(string reason) : Attribute
{
    /// <summary>Why the unsafe API is needed and what keeps it safe.</summary>
    public string Reason { get; } = reason;
}

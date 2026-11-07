using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace ArcaneCore.Analyzers;

/// <summary>The diagnostics of the safety analyzer. Every rule is a warning, which the repo's warnings-as-errors turns into a build break.</summary>
internal static class SafetyRules
{
    private const string Category = "ArcaneCore.Safety";

    public static readonly DiagnosticDescriptor OffThreadWorldAccess = Make(
        "ARC0001",
        "World-thread-only member used off the world thread",
        "A member owned by the world thread is used inside a callback that runs on another thread.");

    public static readonly DiagnosticDescriptor WorldAccessAfterAwait = Make(
        "ARC0002",
        "World-thread-only member used after an await",
        "A member owned by the world thread is used after an await; the world thread has no SynchronizationContext, so the continuation runs on a pool thread.");

    public static readonly DiagnosticDescriptor UnsafeApi = Make(
        "ARC0003",
        "Unsafe API outside [AuditedUnsafe]",
        "Unsafe, MemoryMarshal, NativeMemory, Marshal, P/Invoke and AllowUnsafeBlocks need an [AuditedUnsafe(\"reason\")] on the member, type or assembly.");

    public static readonly DiagnosticDescriptor PoolRentWithoutReturn = Make(
        "ARC0004",
        "Pooled buffer is rented but not returned in the same member",
        "ArrayPool.Rent without a Return in the same member (or MemoryPool.Rent outside a using) leaves the buffer's owner implicit.");

    public static readonly DiagnosticDescriptor UseAfterReturn = Make(
        "ARC0005",
        "Pooled buffer used after it was returned",
        "A buffer is used by a later statement after ArrayPool.Return gave it back; another renter may already be writing to it.");

    public static readonly DiagnosticDescriptor BlockingWait = Make(
        "ARC0006",
        "Blocking wait on a task",
        "Task.Result, Task.Wait, Task.WaitAll/WaitAny and GetAwaiter().GetResult() block the calling thread; on the world thread that stalls the tick.");

    public static readonly DiagnosticDescriptor StaleBaseline = Make(
        "ARC9001",
        "Safety baseline entry no longer matches",
        "A safety-baseline.txt entry allows more occurrences than the code has; lower or remove it so the baseline only shrinks.");

    public static readonly DiagnosticDescriptor MalformedBaseline = Make(
        "ARC9002",
        "Safety baseline line is malformed",
        "A safety-baseline.txt line is not 'RULE|path|member|detail|count'.");

    public static readonly ImmutableArray<DiagnosticDescriptor> All = ImmutableArray.Create(
        OffThreadWorldAccess, WorldAccessAfterAwait, UnsafeApi, PoolRentWithoutReturn, UseAfterReturn, BlockingWait, StaleBaseline, MalformedBaseline);

    private static DiagnosticDescriptor Make(string id, string title, string description)
        => new(id, title, "{0}", Category, DiagnosticSeverity.Warning, isEnabledByDefault: true, description: description);
}

using System.Diagnostics.CodeAnalysis;
using ArcaneCore.Kernel.Characters;

namespace ArcaneCore.World.Persistence;

/// <summary>
/// Finds a fence refusal (<see cref="CharacterOwnershipRejectedException"/>) in an exception a store raised. EF Core may wrap what an interceptor or a commit-time check throws (a
/// <c>DbUpdateException</c>, an <c>InvalidOperationException</c>), and a write queue that matched the exact type would then treat a refusal as an ordinary failure: retained and retried
/// instead of dropped (sealed or retired source) or terminal (lost ownership). Every queue that decides on a refusal asks here, so they all agree on what a refusal is.
/// </summary>
internal static class OwnershipRefusal
{
    /// <summary>Upper bound on the inner-exception chain walked; a cyclic chain cannot be built by the framework, the bound is only a guard.</summary>
    private const int MaxDepth = 16;

    /// <summary><paramref name="failure"/> itself, or the first inner exception, that is a fence refusal; null when there is none. Pure; any thread.</summary>
    public static CharacterOwnershipRejectedException? Find(Exception? failure)
    {
        int depth = 0;
        for (Exception? e = failure; e is not null && depth < MaxDepth; e = e.InnerException, depth++)
        {
            if (e is CharacterOwnershipRejectedException refusal)
            {
                return refusal;
            }

            if (e is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions)
                {
                    if (Find(inner) is { } found)
                    {
                        return found;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>True when <paramref name="failure"/> is, or wraps, a fence refusal; <paramref name="refusal"/> is that refusal.</summary>
    public static bool TryFind(Exception? failure, [NotNullWhen(true)] out CharacterOwnershipRejectedException? refusal)
    {
        refusal = Find(failure);
        return refusal is not null;
    }
}

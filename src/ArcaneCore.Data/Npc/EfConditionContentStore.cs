using ArcaneCore.Data.Content;
using ArcaneCore.Kernel.Npc;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Npc;

/// <summary>Reads the <c>conditions</c> table (startup only).</summary>
public sealed class EfConditionContentStore(WorldDbContext db) : IConditionContentStore
{
    public async Task<IReadOnlyList<ConditionRecord>> LoadAsync(CancellationToken cancellationToken = default)
        => await db.Set<ConditionRow>().AsNoTracking().OrderBy(r => r.ConditionEntry)
            .Select(r => new ConditionRecord(r.ConditionEntry, r.Type, r.Value1, r.Value2, r.Value3, r.Value4, r.Flags))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
}

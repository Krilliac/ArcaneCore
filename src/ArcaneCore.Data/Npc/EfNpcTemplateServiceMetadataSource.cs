using ArcaneCore.Data.Content;
using ArcaneCore.Kernel.Npc;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Npc;

public sealed class EfNpcTemplateServiceMetadataSource(WorldDbContext db) : INpcTemplateServiceMetadataSource
{
    public async Task<IReadOnlyList<NpcTemplateServiceMetadata>> LoadAsync(CancellationToken cancellationToken = default)
        => await db.Set<NpcTemplateServiceMetadata>().AsNoTracking().OrderBy(r => r.Entry)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
}

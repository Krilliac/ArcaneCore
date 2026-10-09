using ArcaneCore.Data.Content;
using ArcaneCore.Kernel.WorldData.SpawnGroups;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.World.SpawnGroups;

/// <summary>Reads the spawn group tables of the world database into a <see cref="SpawnGroupCatalog"/> (the creature and game object stores call it).</summary>
public static class SpawnGroupStore
{
    /// <summary>The groups of one type, with their spawns (in table order of id, guid), entries, formation and linked groups.</summary>
    public static async Task<SpawnGroupCatalog> LoadAsync(WorldDbContext db, SpawnGroupType type, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        uint typeId = (uint)type;
        List<SpawnGroupRow> groups = await db.Set<SpawnGroupRow>().AsNoTracking().Where(g => g.Type == typeId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (groups.Count == 0)
        {
            return SpawnGroupCatalog.Empty;
        }

        List<SpawnGroupSpawnRow> spawns = await db.Set<SpawnGroupSpawnRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<SpawnGroupEntryRow> entries = await db.Set<SpawnGroupEntryRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<SpawnGroupFormationRow> formations = await db.Set<SpawnGroupFormationRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<SpawnGroupLinkedGroupRow> links = await db.Set<SpawnGroupLinkedGroupRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        return Build(groups, spawns, entries, formations, links);
    }

    /// <summary>The catalog of <paramref name="groups"/> (rows of other groups in the member, entry, formation and link lists are ignored).</summary>
    public static SpawnGroupCatalog Build(
        IEnumerable<SpawnGroupRow> groups, IEnumerable<SpawnGroupSpawnRow> spawns, IEnumerable<SpawnGroupEntryRow> entries,
        IEnumerable<SpawnGroupFormationRow> formations, IEnumerable<SpawnGroupLinkedGroupRow> links)
    {
        ILookup<uint, SpawnGroupSpawnRow> membersOf = spawns.ToLookup(s => s.Id);
        ILookup<uint, SpawnGroupEntryRow> entriesOf = entries.ToLookup(e => e.Id);
        Dictionary<uint, SpawnGroupFormationRow> formationOf = formations.GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First());
        ILookup<uint, SpawnGroupLinkedGroupRow> linksOf = links.ToLookup(l => l.Id);
        return new SpawnGroupCatalog(groups.Select(g => new SpawnGroupDefinition
        {
            Id = g.Id,
            Name = g.Name,
            Type = (SpawnGroupType)g.Type,
            MaxCount = g.MaxCount,
            WorldStateCondition = g.WorldState,
            WorldStateExpression = g.WorldStateExpression,
            Flags = (SpawnGroupFlags)g.Flags,
            StringId = g.StringId,
            Members = [.. membersOf[g.Id].OrderBy(s => s.Guid).Select(s => new SpawnGroupMember(s.Guid, s.SlotId, s.Chance))],
            RandomEntries = [.. entriesOf[g.Id].OrderBy(e => e.Entry).Select(e => new SpawnGroupRandomEntry(e.Entry, e.MinCount, e.MaxCount, e.Chance))],
            Formation = formationOf.TryGetValue(g.Id, out SpawnGroupFormationRow? f)
                ? new SpawnGroupFormation(f.FormationType, f.FormationSpread, f.FormationOptions, f.PathId, f.MovementType, f.Comment ?? string.Empty)
                : null,
            LinkedGroups = [.. linksOf[g.Id].Select(l => l.LinkedId).Order()],
        }));
    }
}

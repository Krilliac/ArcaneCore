using ArcaneCore.Data.Content;
using ArcaneCore.Kernel.Npc;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Npc;

/// <summary>Reads the NPC service tables of world schema v2 (startup only).</summary>
public sealed class EfNpcContentStore(WorldDbContext db) : INpcContentStore
{
    public async Task<NpcContent> LoadAsync(CancellationToken cancellationToken = default)
    {
        List<NpcGossip> gossips = await db.Set<NpcGossip>().AsNoTracking().OrderBy(r => r.NpcGuid)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<GossipMenu> menus = await db.Set<GossipMenu>().AsNoTracking()
            .OrderBy(r => r.Entry).ThenBy(r => r.TextId).ThenBy(r => r.ConditionId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<GossipMenuOption> options = await db.Set<GossipMenuOption>().AsNoTracking()
            .OrderBy(r => r.MenuId).ThenBy(r => r.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<NpcTextRow> textRows = await db.Set<NpcTextRow>().AsNoTracking().OrderBy(r => r.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<VendorItem> vendor = await db.Set<VendorItem>().AsNoTracking()
            .OrderBy(r => r.Entry).ThenBy(r => r.Slot).ThenBy(r => r.Item)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<TrainerSpell> trainer = await db.Set<TrainerSpell>().AsNoTracking()
            .OrderBy(r => r.Entry).ThenBy(r => r.Spell)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<TaxiNode> nodes = await db.Set<TaxiNode>().AsNoTracking().OrderBy(r => r.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<TaxiPath> paths = await db.Set<TaxiPath>().AsNoTracking().OrderBy(r => r.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<RaceTaxiStart> starts = await db.Set<RaceTaxiStart>().AsNoTracking().OrderBy(r => r.Race)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        List<PointOfInterest> pois = await db.Set<PointOfInterest>().AsNoTracking().OrderBy(r => r.Entry)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return new NpcContent(gossips, menus, options, textRows.Select(ToNpcText).ToArray(), vendor, trainer, nodes, paths, starts, pois);
    }

    /// <summary>The inline cmangos-classic row as eight variants.</summary>
    public static NpcText ToNpcText(NpcTextRow r) => new()
    {
        Id = r.Id,
        Options =
            [
                new NpcTextOption(r.Prob0, r.Text0_0, r.Text0_1, r.Lang0, r.Em0_0, r.Em0_1, r.Em0_2, r.Em0_3, r.Em0_4, r.Em0_5),
                new NpcTextOption(r.Prob1, r.Text1_0, r.Text1_1, r.Lang1, r.Em1_0, r.Em1_1, r.Em1_2, r.Em1_3, r.Em1_4, r.Em1_5),
                new NpcTextOption(r.Prob2, r.Text2_0, r.Text2_1, r.Lang2, r.Em2_0, r.Em2_1, r.Em2_2, r.Em2_3, r.Em2_4, r.Em2_5),
                new NpcTextOption(r.Prob3, r.Text3_0, r.Text3_1, r.Lang3, r.Em3_0, r.Em3_1, r.Em3_2, r.Em3_3, r.Em3_4, r.Em3_5),
                new NpcTextOption(r.Prob4, r.Text4_0, r.Text4_1, r.Lang4, r.Em4_0, r.Em4_1, r.Em4_2, r.Em4_3, r.Em4_4, r.Em4_5),
                new NpcTextOption(r.Prob5, r.Text5_0, r.Text5_1, r.Lang5, r.Em5_0, r.Em5_1, r.Em5_2, r.Em5_3, r.Em5_4, r.Em5_5),
                new NpcTextOption(r.Prob6, r.Text6_0, r.Text6_1, r.Lang6, r.Em6_0, r.Em6_1, r.Em6_2, r.Em6_3, r.Em6_4, r.Em6_5),
                new NpcTextOption(r.Prob7, r.Text7_0, r.Text7_1, r.Lang7, r.Em7_0, r.Em7_1, r.Em7_2, r.Em7_3, r.Em7_4, r.Em7_5),
            ],
    };
}

using ArcaneCore.Data.Content;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Data.World.Creatures;
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
        List<NpcTextBroadcastRow> broadcastRows = await db.Set<NpcTextBroadcastRow>().AsNoTracking().OrderBy(r => r.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<uint, BroadcastTextRow> broadcastTexts = broadcastRows.Count == 0
            ? []
            : await db.Set<BroadcastTextRow>().AsNoTracking().ToDictionaryAsync(r => r.Id, cancellationToken).ConfigureAwait(false);
        List<VendorItem> vendor = await db.Set<VendorItem>().AsNoTracking()
            .OrderBy(r => r.Entry).ThenBy(r => r.Slot).ThenBy(r => r.Item)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<VendorTemplateItemRow> vendorTemplates = await db.Set<VendorTemplateItemRow>().AsNoTracking()
            .OrderBy(r => r.Entry).ThenBy(r => r.Slot).ThenBy(r => r.Item)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<uint, uint> vendorTemplateIds = await db.Set<CreatureTemplateRow>().AsNoTracking()
            .Where(r => r.VendorTemplateId != 0)
            .ToDictionaryAsync(r => r.Entry, r => r.VendorTemplateId, cancellationToken).ConfigureAwait(false);
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

        return new NpcContent(gossips, menus, options, ResolveTexts(textRows, broadcastRows, broadcastTexts), vendor, trainer, nodes, paths, starts, pois)
        {
            VendorTemplateItems = vendorTemplates.Select(r => r.ToVendorItem()).ToArray(),
            VendorTemplateIds = vendorTemplateIds,
        };
    }

    /// <summary>
    /// Every inline <c>npc_text</c> row, plus a text for each <c>npc_text_broadcast_text</c> id that has no inline row (an inline row
    /// wins, so existing content is unchanged). Ordered by id, like the inline rows were.
    /// </summary>
    public static NpcText[] ResolveTexts(
        IReadOnlyList<NpcTextRow> inline,
        IReadOnlyList<NpcTextBroadcastRow> links,
        IReadOnlyDictionary<uint, BroadcastTextRow> broadcastTexts)
    {
        var texts = new List<NpcText>(inline.Count + links.Count);
        var inlineIds = new HashSet<uint>();
        foreach (NpcTextRow row in inline)
        {
            inlineIds.Add(row.Id);
            texts.Add(ToNpcText(row));
        }

        foreach (NpcTextBroadcastRow link in links)
        {
            if (!inlineIds.Contains(link.Id))
            {
                texts.Add(ToNpcText(link, broadcastTexts));
            }
        }

        texts.Sort((a, b) => a.Id.CompareTo(b.Id));
        return [.. texts];
    }

    /// <summary>
    /// The eight variants of a broadcast-text row. A variant whose <c>BroadcastTextId</c> is 0 or names no <c>broadcast_text</c> row has
    /// probability 0 and empty text, so it is never picked. The broadcast row's EmoteID1..3 / EmoteDelay1..3 become the three
    /// emote/delay pairs of the variant and its LanguageID the variant language.
    /// </summary>
    public static NpcText ToNpcText(NpcTextBroadcastRow r, IReadOnlyDictionary<uint, BroadcastTextRow> broadcastTexts)
    {
        (float Prob, uint Broadcast)[] slots =
        [
            (r.Prob0, r.BroadcastTextId0), (r.Prob1, r.BroadcastTextId1), (r.Prob2, r.BroadcastTextId2), (r.Prob3, r.BroadcastTextId3),
            (r.Prob4, r.BroadcastTextId4), (r.Prob5, r.BroadcastTextId5), (r.Prob6, r.BroadcastTextId6), (r.Prob7, r.BroadcastTextId7),
        ];
        var options = new NpcTextOption[8];
        for (int i = 0; i < slots.Length; i++)
        {
            (float prob, uint id) = slots[i];
            options[i] = id != 0 && broadcastTexts.TryGetValue(id, out BroadcastTextRow? b)
                ? new NpcTextOption(prob, b.Text, b.FemaleText, b.Language, b.EmoteDelay1, b.EmoteId1, b.EmoteDelay2, b.EmoteId2, b.EmoteDelay3, b.EmoteId3)
                : new NpcTextOption(0f, string.Empty, string.Empty, 0, 0, 0, 0, 0, 0, 0);
        }

        return new NpcText { Id = r.Id, Options = options };
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

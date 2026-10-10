using System.Collections.Frozen;
using ArcaneCore.Kernel.Npc;

namespace ArcaneCore.Game.Npc;

/// <summary>
/// The immutable NPC service content (built once at startup, read on the world thread):
/// gossip menus, texts and options, vendor and trainer lists, flight nodes and paths, points of
/// interest — vmangos ObjectMgr's gossip/vendor/trainer stores and the TaxiNodes/TaxiPath DBC data.
/// </summary>
public sealed class NpcStore
{
    /// <summary>vmangos TaxiMaskSize (8 words, node ids 1..256).</summary>
    public const int TaxiMaskSize = 8;

    private readonly FrozenDictionary<uint, uint> _npcGossip;
    private readonly FrozenDictionary<uint, GossipMenu[]> _menus;
    private readonly FrozenDictionary<uint, GossipMenuOption[]> _options;
    private readonly FrozenDictionary<uint, NpcText> _texts;
    private readonly FrozenDictionary<uint, VendorItem[]> _vendor;
    private readonly FrozenDictionary<uint, TrainerSpell[]> _trainer;
    private readonly FrozenDictionary<uint, TaxiNode> _nodes;
    private readonly FrozenDictionary<(uint From, uint To), TaxiPath> _paths;
    private readonly FrozenDictionary<uint, TaxiPath> _pathsById;
    private readonly FrozenDictionary<byte, uint> _raceTaxi;
    private readonly FrozenDictionary<uint, PointOfInterest> _pois;
    private readonly uint[] _taxiNodesMask = new uint[TaxiMaskSize];

    public NpcStore(NpcContent content, IReadOnlySet<uint>? scriptedTaxiPathIds = null)
    {
        Content = content;
        ScriptedTaxiPathIds = (scriptedTaxiPathIds ?? new HashSet<uint>()).ToFrozenSet();
        _npcGossip = content.NpcGossips.GroupBy(g => g.NpcGuid).ToFrozenDictionary(g => g.Key, g => g.First().TextId);

        // vmangos GetGossipTextId walks a menu's texts by rising condition_id.
        _menus = content.GossipMenus.GroupBy(m => m.Entry)
            .ToFrozenDictionary(g => g.Key, g => g.OrderBy(m => m.ConditionId).ThenBy(m => m.TextId).ToArray());

        // vmangos LoadGossipMenuItems: ORDER BY menu_id, id.
        _options = content.GossipMenuOptions.GroupBy(o => o.MenuId)
            .ToFrozenDictionary(g => g.Key, g => g.OrderBy(o => o.Id).ToArray());
        _texts = content.NpcTexts.GroupBy(t => t.Id).ToFrozenDictionary(g => g.Key, g => g.First());

        // vmangos LoadVendors: ORDER BY entry, slot.
        _vendor = content.VendorItems.GroupBy(v => v.Entry)
            .ToFrozenDictionary(g => g.Key, g => g.OrderBy(v => v.Slot).ThenBy(v => v.Item).ToArray());
        _trainer = content.TrainerSpells.GroupBy(t => t.Entry)
            .ToFrozenDictionary(g => g.Key, g => g.OrderBy(t => t.Spell).ToArray());
        _nodes = content.TaxiNodes.Where(n => n.Id is > 0 and <= TaxiMaskSize * 32)
            .GroupBy(n => n.Id).ToFrozenDictionary(g => g.Key, g => g.First());
        _paths = content.TaxiPaths.GroupBy(p => (p.FromNode, p.ToNode)).ToFrozenDictionary(g => g.Key, g => g.First());
        _pathsById = content.TaxiPaths.GroupBy(p => p.Id).ToFrozenDictionary(g => g.Key, g => g.First());
        _raceTaxi = content.RaceTaxiStarts.GroupBy(r => r.Race).ToFrozenDictionary(g => g.Key, g => g.First().Mask);
        _pois = content.PointsOfInterest.GroupBy(p => p.Entry).ToFrozenDictionary(g => g.Key, g => g.First());

        // vmangos DBCStores.cpp, "Initialize global taxinodes mask": every existing node joins the network unless all of
        // its outgoing paths are SEND_TAXI spell (scripted) paths. A node with no outgoing path at all is kept.
        ILookup<uint, TaxiPath> outgoing = _paths.Values.ToLookup(p => p.FromNode);
        foreach (uint id in _nodes.Keys)
        {
            if (outgoing.Contains(id) && outgoing[id].All(p => ScriptedTaxiPathIds.Contains(p.Id)))
            {
                continue;
            }

            _taxiNodesMask[(id - 1) / 32] |= 1u << (int)((id - 1) % 32);
        }
    }

    public static NpcStore Empty { get; } = new(NpcContent.Empty);

    /// <summary>
    /// The rows this store was built from. The live reload (<c>.reload npc_vendor</c>, ...) replaces one table's rows
    /// of it and builds the next store, so the tables it does not reload keep exactly what is live.
    /// </summary>
    public NpcContent Content { get; }

    /// <summary>The taxi network (vmangos sTaxiNodesMask): every node except those whose outgoing paths are all SEND_TAXI paths.</summary>
    public IReadOnlyList<uint> TaxiNodesMask => _taxiNodesMask;

    /// <summary>SEND_TAXI (effect 123) path ids this store's network excludes; kept across NPC table reloads.</summary>
    public IReadOnlySet<uint> ScriptedTaxiPathIds { get; }

    /// <summary>Whether the node is in <see cref="TaxiNodesMask"/> (vmangos GetNearestTaxiNode's "skip not taxi network nodes").</summary>
    public bool IsNetworkNode(uint id) => id is > 0 and <= TaxiMaskSize * 32
        && (_taxiNodesMask[(id - 1) / 32] & (1u << (int)((id - 1) % 32))) != 0;

    /// <summary>npc_gossip text for a spawn id, 0 when none (vmangos ObjectMgr::GetNpcGossip).</summary>
    public uint NpcGossipText(uint spawnId) => _npcGossip.GetValueOrDefault(spawnId);

    public IReadOnlyList<GossipMenu> MenuTexts(uint menuId) => _menus.GetValueOrDefault(menuId) ?? [];

    public IReadOnlyList<GossipMenuOption> MenuOptions(uint menuId) => _options.GetValueOrDefault(menuId) ?? [];

    public NpcText? Text(uint textId) => _texts.GetValueOrDefault(textId);

    public IReadOnlyList<VendorItem> VendorItems(uint entry) => _vendor.GetValueOrDefault(entry) ?? [];

    public IReadOnlyList<TrainerSpell> TrainerSpells(uint entry) => _trainer.GetValueOrDefault(entry) ?? [];

    public TaxiNode? Node(uint id) => _nodes.GetValueOrDefault(id);

    public IEnumerable<TaxiNode> Nodes => _nodes.Values.OrderBy(n => n.Id);

    /// <summary>vmangos ObjectMgr::GetTaxiPath (null when no direct path).</summary>
    public TaxiPath? Path(uint from, uint to) => _paths.GetValueOrDefault((from, to));

    /// <summary>sTaxiPathStore.LookupEntry: the path with this TaxiPath.dbc id, null when none.</summary>
    public TaxiPath? PathById(uint id) => _pathsById.GetValueOrDefault(id);

    /// <summary>ChrRaces startingTaxiMask (0 when not configured).</summary>
    public uint RaceStartingTaxiMask(byte race) => _raceTaxi.GetValueOrDefault(race);

    public PointOfInterest? Poi(uint entry) => _pois.GetValueOrDefault(entry);
}

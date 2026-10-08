namespace ArcaneCore.Kernel.Npc;

/// <summary><c>npc_gossip</c> (cmangos/vmangos): a spawned creature's fixed greeting text.</summary>
public sealed class NpcGossip
{
    /// <summary>npc_gossip.npc_guid (creature spawn id).</summary>
    public uint NpcGuid { get; init; }

    /// <summary>npc_gossip.textid (npc_text.ID).</summary>
    public uint TextId { get; init; }
}

/// <summary><c>gossip_menu</c> (cmangos/vmangos): candidate greeting texts of a menu.</summary>
public sealed class GossipMenu
{
    /// <summary>gossip_menu.entry (menu id).</summary>
    public uint Entry { get; init; }

    /// <summary>gossip_menu.text_id (npc_text.ID).</summary>
    public uint TextId { get; init; }

    /// <summary>gossip_menu.condition_id (0 = always).</summary>
    public uint ConditionId { get; init; }

    /// <summary>gossip_menu.script_id: the <c>dbscripts_on_gossip</c> id run when this text is chosen (mangos-classic Player::GetGossipTextId); 0 = none. World schema 42.</summary>
    public uint ScriptId { get; init; }
}

/// <summary><c>gossip_menu_option</c> (cmangos/vmangos): one line of a gossip menu.</summary>
public sealed class GossipMenuOption
{
    /// <summary>gossip_menu_option.menu_id.</summary>
    public uint MenuId { get; init; }

    /// <summary>gossip_menu_option.id (order within the menu).</summary>
    public uint Id { get; init; }

    /// <summary>gossip_menu_option.option_icon (GossipOptionIcon).</summary>
    public byte OptionIcon { get; init; }

    /// <summary>gossip_menu_option.option_text.</summary>
    public string OptionText { get; init; } = string.Empty;

    /// <summary>gossip_menu_option.option_id (GossipOption).</summary>
    public byte OptionId { get; init; }

    /// <summary>gossip_menu_option.npc_option_npcflag (required UNIT_NPC_FLAG_*).</summary>
    public uint NpcOptionNpcFlag { get; init; }

    /// <summary>gossip_menu_option.action_menu_id (0 none, -1 close, else a menu).</summary>
    public int ActionMenuId { get; init; }

    /// <summary>gossip_menu_option.action_poi_id.</summary>
    public uint ActionPoiId { get; init; }

    /// <summary>gossip_menu_option.box_coded (the client asks for a code).</summary>
    public byte BoxCoded { get; init; }

    /// <summary>gossip_menu_option.box_text.</summary>
    public string BoxText { get; init; } = string.Empty;

    /// <summary>gossip_menu_option.condition_id (0 = always).</summary>
    public uint ConditionId { get; init; }

    /// <summary>gossip_menu_option.action_script_id: the <c>dbscripts_on_gossip</c> id run when the option is selected (mangos-classic Player::OnGossipSelect); 0 = none. World schema 42.</summary>
    public uint ActionScriptId { get; init; }
}

/// <summary>
/// <c>npc_text</c> in the cmangos-classic inline layout: eight weighted greeting variants
/// (text{i}_0 male / text{i}_1 female, lang{i}, prob{i}, em{i}_0..5 as delay/emote pairs).
/// </summary>
/// <remarks>
/// Reference discrepancy: vmangos indexes broadcast_text (BroadcastTextID{i}); cmangos-classic
/// and the classic-db dump keep the strings inline. Inline is what the content source carries.
/// </remarks>
public sealed class NpcText
{
    /// <summary>npc_text.ID.</summary>
    public uint Id { get; init; }

    /// <summary>The eight variants (always length 8).</summary>
    public IReadOnlyList<NpcTextOption> Options { get; init; } = [];
}

/// <summary>One weighted variant of an <see cref="NpcText"/>.</summary>
public sealed record NpcTextOption(
    float Probability,
    string Text0,
    string Text1,
    uint Language,
    uint EmoteDelay0,
    uint Emote0,
    uint EmoteDelay1,
    uint Emote1,
    uint EmoteDelay2,
    uint Emote2);

/// <summary><c>npc_vendor</c> (vmangos): an item a creature entry sells.</summary>
public sealed class VendorItem
{
    /// <summary>npc_vendor.entry (creature entry).</summary>
    public uint Entry { get; init; }

    /// <summary>npc_vendor.item (item_template entry).</summary>
    public uint Item { get; init; }

    /// <summary>npc_vendor.maxcount (0 = unlimited).</summary>
    public uint MaxCount { get; init; }

    /// <summary>npc_vendor.incrtime (restock delay, seconds).</summary>
    public uint IncrTime { get; init; }

    /// <summary>npc_vendor.slot (display order).</summary>
    public uint Slot { get; init; }

    /// <summary>npc_vendor.condition_id: the item is listed only when the condition holds (vmangos IsVendorItemVisible).</summary>
    public uint ConditionId { get; init; }
}

/// <summary><c>npc_trainer</c> (vmangos): a spell a creature entry teaches.</summary>
public sealed class TrainerSpell
{
    /// <summary>npc_trainer.entry (creature entry).</summary>
    public uint Entry { get; init; }

    /// <summary>npc_trainer.spell (the teaching spell id, as listed to the client).</summary>
    public uint Spell { get; init; }

    /// <summary>npc_trainer.spellcost (copper).</summary>
    public uint SpellCost { get; init; }

    /// <summary>npc_trainer.reqskill.</summary>
    public uint ReqSkill { get; init; }

    /// <summary>npc_trainer.reqskillvalue.</summary>
    public uint ReqSkillValue { get; init; }

    /// <summary>npc_trainer.reqlevel (0 = the taught spell's level).</summary>
    public uint ReqLevel { get; init; }
}

/// <summary>A flight master node (TaxiNodes.dbc: ID, ContinentID, x, y, z, Name, MountCreatureID[2]).</summary>
public sealed class TaxiNode
{
    public uint Id { get; init; }

    public uint MapId { get; init; }

    public float X { get; init; }

    public float Y { get; init; }

    public float Z { get; init; }

    public string Name { get; init; } = string.Empty;

    /// <summary>MountCreatureID[0] (horde mount creature; 0 = node not usable by horde).</summary>
    public uint MountHorde { get; init; }

    /// <summary>MountCreatureID[1] (alliance mount creature; 0 = node not usable by alliance).</summary>
    public uint MountAlliance { get; init; }
}

/// <summary>A flight path between two nodes (TaxiPath.dbc: ID, FromTaxiNode, ToTaxiNode, Cost).</summary>
public sealed class TaxiPath
{
    public uint Id { get; init; }

    public uint FromNode { get; init; }

    public uint ToNode { get; init; }

    public uint Price { get; init; }
}

/// <summary>A race's starting flight paths (ChrRaces.dbc startingTaxiMask, vmangos PlayerTaxi::InitTaxiNodes).</summary>
public sealed class RaceTaxiStart
{
    public byte Race { get; init; }

    /// <summary>The first mask word (vmangos sets only m_taximask[0]).</summary>
    public uint Mask { get; init; }
}

/// <summary><c>points_of_interest</c> (vmangos): a map marker a gossip option can show (SMSG_GOSSIP_POI).</summary>
public sealed class PointOfInterest
{
    /// <summary>points_of_interest.entry.</summary>
    public uint Entry { get; init; }

    public float X { get; init; }

    public float Y { get; init; }

    public uint Icon { get; init; }

    public uint Flags { get; init; }

    public uint Data { get; init; }

    /// <summary>points_of_interest.icon_name.</summary>
    public string IconName { get; init; } = string.Empty;
}

/// <summary>Everything the NPC services read from the world database at startup.</summary>
public sealed record NpcContent(
    IReadOnlyList<NpcGossip> NpcGossips,
    IReadOnlyList<GossipMenu> GossipMenus,
    IReadOnlyList<GossipMenuOption> GossipMenuOptions,
    IReadOnlyList<NpcText> NpcTexts,
    IReadOnlyList<VendorItem> VendorItems,
    IReadOnlyList<TrainerSpell> TrainerSpells,
    IReadOnlyList<TaxiNode> TaxiNodes,
    IReadOnlyList<TaxiPath> TaxiPaths,
    IReadOnlyList<RaceTaxiStart> RaceTaxiStarts,
    IReadOnlyList<PointOfInterest> PointsOfInterest)
{
    public static NpcContent Empty { get; } = new([], [], [], [], [], [], [], [], [], []);
}

/// <summary>Reads the NPC service tables once at startup (off the world thread).</summary>
public interface INpcContentStore
{
    Task<NpcContent> LoadAsync(CancellationToken cancellationToken = default);
}

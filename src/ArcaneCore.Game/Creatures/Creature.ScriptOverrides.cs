namespace ArcaneCore.Game.Creatures;

public sealed partial class Creature
{
    /// <summary>
    /// The gossip menu a DB script set (cmangos SCRIPT_COMMAND_SET_GOSSIP_MENU 52, Creature::SetDefaultGossipMenuId); null: the template's
    /// <c>gossip_menu_id</c>. Like cmangos' <c>m_gossipMenuId</c> it is not reset by a respawn; the scripts that change it set it back themselves.
    /// </summary>
    public uint? ScriptGossipMenuId { get; set; }

    /// <summary>The menu the creature opens (cmangos Creature::GetDefaultGossipMenuId): a script's choice, else the template's.</summary>
    public uint DefaultGossipMenuId => ScriptGossipMenuId ?? Template.GossipMenuId;

    /// <summary>
    /// The three virtual item slots (display and the two info fields each) as they were before a DB script first changed them
    /// (SCRIPT_COMMAND_SET_EQUIPMENT_SLOTS 42); its "reset default" puts these back. Null until a script changes a slot.
    /// </summary>
    internal uint[]? ScriptEquipmentDefault { get; set; }
}

using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Npc;

/// <summary>
/// A script that owns the gossip of some creatures (vmangos ScriptDev's pGossipHello and pGossipSelect). World thread.
/// </summary>
public interface INpcGossipScript
{
    /// <summary>The menu of <paramref name="npc"/> for <paramref name="player"/>, or null when the script does not own this creature's gossip.</summary>
    ScriptedGossipMenu? Hello(Player player, NpcInfo npc);

    /// <summary>A line the script added was chosen: the npc text to show over the same lines, or 0 for nothing.</summary>
    uint Select(Player player, NpcInfo npc, uint sender, uint action);

    /// <summary>
    /// A line the script added was chosen, with what the script asks of the menu (vmangos CLOSE_GOSSIP_MENU, SEND_VENDORLIST, SEND_GOSSIP_MENU
    /// in a pGossipSelect). By default only the npc text of <see cref="Select"/>.
    /// </summary>
    ScriptedGossipReply SelectReply(Player player, NpcInfo npc, uint sender, uint action) => new(Select(player, npc, sender, action));
}

/// <summary>
/// A scripted menu: the creature's quest list first when <paramref name="ShowQuests"/>, the lines, the npc text (0: the creature's own).
/// A <see cref="Silent"/> menu sends nothing at all (a pGossipHello that returned true without a menu).
/// </summary>
public sealed record ScriptedGossipMenu(bool ShowQuests, uint NpcTextId, IReadOnlyList<ScriptedGossipItem> Items)
{
    public bool Silent { get; init; }

    /// <summary>The script handled the hello and shows nothing.</summary>
    public static ScriptedGossipMenu Nothing { get; } = new(false, 0, []) { Silent = true };
}

/// <summary>
/// What a script's choice asks of the menu, in the order vmangos scripts do it: close the menu, open the creature's vendor or trainer list, then show
/// <paramref name="NpcTextId"/> over the same lines (0: none).
/// </summary>
public readonly record struct ScriptedGossipReply(uint NpcTextId, bool Close = false, bool Vendor = false, bool Trainer = false);

/// <summary>One scripted line (vmangos ADD_GOSSIP_ITEM: icon, text, sender, action).</summary>
public readonly record struct ScriptedGossipItem(byte Icon, string Text, uint Sender, uint Action);

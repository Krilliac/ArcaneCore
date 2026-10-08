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
}

/// <summary>A scripted menu: the creature's quest list first when <paramref name="ShowQuests"/>, the lines, the npc text (0: the creature's own).</summary>
public sealed record ScriptedGossipMenu(bool ShowQuests, uint NpcTextId, IReadOnlyList<ScriptedGossipItem> Items);

/// <summary>One scripted line (vmangos ADD_GOSSIP_ITEM: icon, text, sender, action).</summary>
public readonly record struct ScriptedGossipItem(byte Icon, string Text, uint Sender, uint Action);

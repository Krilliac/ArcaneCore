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
/// Several gossip scripts in one slot (each owns its own creatures, as ScriptDev scripts are bound per creature): the first one with a menu
/// answers a hello, and a scripted line chosen later goes back to that script. A player has one gossip menu open at a time, so the owner is
/// remembered per player. World thread.
/// </summary>
public sealed class NpcGossipScriptChain : INpcGossipScript
{
    private readonly INpcGossipScript[] _scripts;
    private readonly Dictionary<ObjectGuid, INpcGossipScript> _owners = [];

    private NpcGossipScriptChain(INpcGossipScript[] scripts) => _scripts = scripts;

    /// <summary>A chain of <paramref name="first"/> then <paramref name="next"/> (a chain given as <paramref name="first"/> is extended).</summary>
    public static NpcGossipScriptChain Of(INpcGossipScript first, INpcGossipScript next)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(next);
        return new NpcGossipScriptChain(first is NpcGossipScriptChain chain ? [.. chain._scripts, next] : [first, next]);
    }

    public ScriptedGossipMenu? Hello(Player player, NpcInfo npc)
    {
        foreach (INpcGossipScript script in _scripts)
        {
            if (script.Hello(player, npc) is { } menu)
            {
                _owners[player.Guid] = script;
                return menu;
            }
        }

        _owners.Remove(player.Guid);
        return null;
    }

    public uint Select(Player player, NpcInfo npc, uint sender, uint action) => SelectReply(player, npc, sender, action).NpcTextId;

    public ScriptedGossipReply SelectReply(Player player, NpcInfo npc, uint sender, uint action)
        => _owners.TryGetValue(player.Guid, out INpcGossipScript? owner) ? owner.SelectReply(player, npc, sender, action) : default;
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

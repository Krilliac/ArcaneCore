using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.Classic;
using ArcaneCore.Game.Npc;

namespace ArcaneCore.Game.Instances.Scripts.BlackrockDepths;

/// <summary>
/// npc_dughal_stormwing (entry 9022, the first "Jail Break!" prisoner of Blackrock Depths): gossip only, from the behaviour spec
/// 230-npc_dughal_stormwing.md. A player whose quest 4322 is INCOMPLETE is offered one line over npc text 2846; choosing it releases
/// Dughal: the Jail Break reads SPECIAL (which resumes Marshal Windsor's pause at his point 7), Dughal thanks the player (text -1230010), loses
/// his gossip flag, runs and starts his waypoint movement, and the menu closes. Registered by ScriptName with
/// <see cref="CreatureAiFactory.RegisterNpcScript"/>. World thread, stateless.
/// </summary>
public sealed class DughalStormwingGossip : INpcGossipScript
{
    public const string ScriptName = "npc_dughal_stormwing";

    /// <summary>Dughal's menu text (spec section 3): always sent; the fixture's npc_text has no row for it, so the client shows its default.</summary>
    public const uint NpcTextMenu = 2846;

    /// <summary>GOSSIP_ACTION_INFO_DEF + 1.</summary>
    public const uint ActionRelease = 1001;

    /// <summary>The gossip_texts id of the option line (GOSSIP_ITEM_DUGHAL), carried by the importer into creature_ai_texts.</summary>
    public const int OptionTextId = -3230000;

    /// <summary>Said to the player on the release (SAY_FREE_AND_RUN).</summary>
    public const int SayFree = -1230010;

    private const byte IconChat = 0;
    private const uint SenderMain = 1;

    public ScriptedGossipMenu? Hello(Player player, NpcInfo npc)
    {
        // Menu 2846 is always sent; the option only to a player whose Jail Break is incomplete (and only when the option text was imported).
        ScriptedGossipItem[] items = BlackrockDepthsInstance.JailBreakIsIncomplete(player)
            && BlackrockDepthsInstance.GossipOptionText(player, OptionTextId) is { } text
            ? [new ScriptedGossipItem(IconChat, text, SenderMain, ActionRelease)]
            : [];
        return new ScriptedGossipMenu(ShowQuests: false, NpcTextMenu, items);
    }

    public uint Select(Player player, NpcInfo npc, uint sender, uint action) => SelectReply(player, npc, sender, action).NpcTextId;

    public ScriptedGossipReply SelectReply(Player player, NpcInfo npc, uint sender, uint action)
    {
        if (action != ActionRelease || player.Map?.FindUpdater<InstanceData>() is not BlackrockDepthsInstance depths
            || player.Map.FindObject(npc.Guid) is not Creature dughal)
        {
            return default;
        }

        depths.ReleasePrisoner();
        dughal.System?.SayText(dughal, SayFree, player);
        BlackrockDepthsInstance.SendPrisonerOut(dughal);
        return new ScriptedGossipReply(0, Close: true);
    }
}

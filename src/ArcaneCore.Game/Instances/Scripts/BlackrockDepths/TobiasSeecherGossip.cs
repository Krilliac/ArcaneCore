using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.Classic;
using ArcaneCore.Game.Npc;

namespace ArcaneCore.Game.Instances.Scripts.BlackrockDepths;

/// <summary>
/// npc_tobias_seecher (entry 9679, the last "Jail Break!" prisoner of Blackrock Depths): gossip only, from the behaviour spec
/// 230-npc_tobias_seecher.md. A player whose quest 4322 is INCOMPLETE is offered one line over npc text 2847; choosing it releases Tobias: the
/// Jail Break reads SPECIAL (which resumes Marshal Windsor's pause at his point 49), Tobias says one of two lines at random, loses his gossip
/// flag, runs and starts his waypoint movement, and the menu closes. Registered by ScriptName with <see cref="CreatureAiFactory.RegisterNpcScript"/>.
/// World thread, stateless.
/// </summary>
public sealed class TobiasSeecherGossip : INpcGossipScript
{
    public const string ScriptName = "npc_tobias_seecher";

    /// <summary>Tobias's menu text (spec section 3): always sent; the fixture's npc_text has no row for it.</summary>
    public const uint NpcTextMenu = 2847;

    /// <summary>GOSSIP_ACTION_INFO_DEF + 1.</summary>
    public const uint ActionRelease = 1001;

    /// <summary>The gossip_texts id of the option line (GOSSIP_ITEM_TOBIAS), carried by the importer into creature_ai_texts.</summary>
    public const int OptionTextId = -3230001;

    /// <summary>
    /// The two release lines (SAY_TOBIAS_FREE_1 and 2), chosen 50/50 with no target. Spec 230-npc_tobias_seecher.md section 8, item 2: the
    /// primary (mangos-classic) behaviour picks one of two; vmangos uses a single line.
    /// </summary>
    public const int SayFree1 = -1230032, SayFree2 = -1230033;

    private const byte IconChat = 0;
    private const uint SenderMain = 1;

    public ScriptedGossipMenu? Hello(Player player, NpcInfo npc)
    {
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
            || player.Map.FindObject(npc.Guid) is not Creature tobias)
        {
            return default;
        }

        depths.ReleasePrisoner();
        tobias.System?.SayText(tobias, (tobias.System.RandomInt(0, 1)) == 0 ? SayFree1 : SayFree2);
        BlackrockDepthsInstance.SendPrisonerOut(tobias);
        return new ScriptedGossipReply(0, Close: true);
    }
}

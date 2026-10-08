using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.Classic;
using ArcaneCore.Game.Npc;

namespace ArcaneCore.Game.Instances.Scripts.BlackrockDepths;

/// <summary>
/// mangos-classic blackrock_depths/blackrock_depths.cpp GossipHello_boss_doomrel and GossipSelect_boss_doomrel: Doom'rel offers the
/// challenge while the Tomb of the Seven is NOT_STARTED or FAIL, always over npc text 2601 (his database menu 1947 has no option), and
/// the challenge says SAY_DOOMREL_START_EVENT and sets TYPE_TOMB_OF_SEVEN IN_PROGRESS, which calls the first dwarf. World thread.
/// </summary>
public sealed class DoomrelGossip : INpcGossipScript
{
    public const uint NpcDoomrel = 9039;

    /// <summary>GOSSIP_TEXT_ID_CHALLENGE.</summary>
    public const uint NpcTextChallenge = 2601;

    /// <summary>GOSSIP_ACTION_INFO_DEF + 1.</summary>
    public const uint ActionChallenge = 1001;

    /// <summary>
    /// GOSSIP_ITEM_CHALLENGE (-3230002): ScriptDev2 gossip_texts, which the importer does not load, so its classic-db z2815 English text is
    /// kept here.
    /// </summary>
    public const string ChallengeText = "Your bondage is at an end, Doom'rel. I challenge you!";

    private const byte IconChat = 0;
    private const uint SenderMain = 1;

    public ScriptedGossipMenu? Hello(Player player, NpcInfo npc)
    {
        if (npc.Entry != NpcDoomrel || Instance(player) is not { } depths)
        {
            return null;
        }

        ScriptedGossipItem[] items = depths.CanChallengeTheSeven
            ? [new ScriptedGossipItem(IconChat, ChallengeText, SenderMain, ActionChallenge)]
            : [];
        return new ScriptedGossipMenu(ShowQuests: false, NpcTextChallenge, items);
    }

    public uint Select(Player player, NpcInfo npc, uint sender, uint action) => SelectReply(player, npc, sender, action).NpcTextId;

    public ScriptedGossipReply SelectReply(Player player, NpcInfo npc, uint sender, uint action)
    {
        if (npc.Entry != NpcDoomrel || action != ActionChallenge || Instance(player) is not { } depths)
        {
            return default;
        }

        depths.ChallengeTheSeven(player.Map?.FindObject(npc.Guid) as Creature);
        return new ScriptedGossipReply(0, Close: true);
    }

    private static BlackrockDepthsInstance? Instance(Player player) => player.Map?.FindUpdater<InstanceData>() as BlackrockDepthsInstance;
}

using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.Gnomeregan;
using ArcaneCore.Game.Instances.Scripts.RazorfenDowns;
using ArcaneCore.Game.Instances.Scripts.RazorfenKraul;
using ArcaneCore.Game.Instances.Scripts.ScarletMonastery;
using ArcaneCore.Game.Instances.Scripts.Uldaman;
using ArcaneCore.Game.Instances.Scripts.ZulFarrak;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Instances.Scripts;

/// <summary>
/// The ScriptDev2 entry points of the six classic dungeon ports that are not creature AIs: <c>pQuestAcceptNPC</c>, <c>pGossipHello</c> /
/// <c>pGossipSelect</c>, <c>pProcessEventId</c> and <c>pAreaTrigger</c>. The world's <c>DungeonScriptFeature</c> only subscribes these to the
/// quest, gossip, spell and area-trigger services; each hook checks the player's own instance script, so it is a no-op elsewhere.
/// </summary>
public static class DungeonScriptHooks
{
    /// <summary>The spells whose SEND_EVENT effect (61) a dungeon script handles (ClassicDB z2815 spell_template EffectMiscValue).</summary>
    public const uint AltarKeeperSpell = 11568, AltarArchaedasSpell = 10340, UnlockingSpell = 10738;

    /// <summary>
    /// QuestAccept_npc_willix_the_importer (razorfen_kraul.cpp), QuestAccept_npc_belnistrasz (razorfen_downs.cpp) and QuestAccept_npc_kernobee
    /// (gnomeregan.cpp): the quest giver's script AI starts its escort or follow. Returns whether a script took the quest.
    /// </summary>
    public static bool OnQuestAccepted(Player player, ObjectGuid giverGuid, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (player.Map?.FindObject(giverGuid) is not Creature giver) return false;
        return (questId, giver.AI) switch
        {
            (WillixAi.Quest, WillixAi willix) => willix.AcceptQuest(player),
            (BelnistraszAi.Quest, BelnistraszAi belnistrasz) => belnistrasz.AcceptQuest(player),
            (KernobeeAi.Quest, KernobeeAi kernobee) => kernobee.AcceptQuest(player),
            _ => false,
        };
    }

    /// <summary>Blastmaster Emi Shortfuse's gossip in front of <paramref name="inner"/>, which keeps every other NPC's.</summary>
    public static INpcGossipScript WrapGossip(INpcGossipScript? inner) => new EmiGossipScript(inner);

    /// <summary>
    /// AreaTrigger_at_cathedral_entrance (instance_scarlet_monastery.cpp: a living non-GM player carrying the Corrupted Ashbringer) and
    /// AreaTrigger_at_zulfarrak (zulfarrak.cpp: Antu'sul attacks a living non-GM player while his encounter is not started or failed).
    /// Returns whether a script handled the trigger.
    /// </summary>
    public static bool OnAreaTrigger(Player player, uint triggerId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (player.IsGameMaster || !player.IsAlive) return false;
        InstanceData? data = player.Map?.FindUpdater<InstanceData>();
        if (triggerId == ScarletMonasteryInstance.CathedralTrigger && data is ScarletMonasteryInstance scarlet)
            return player.Inventory.GetItemCount(ScarletMonasteryInstance.CorruptedAshbringer) > 0 && scarlet.EnterCathedral();
        if (triggerId == ZulFarrakInstance.AntusulTrigger && data is ZulFarrakInstance zf
            && zf.GetData(ZulFarrakInstance.TypeAntusul) is EncounterState.NotStarted or EncounterState.Fail
            && zf.FindAntusul() is { IsAlive: true, AI: { } ai })
            return ai.AttackStart(player);
        return false;
    }

    /// <summary>
    /// The SEND_EVENT scripts (pProcessEventId) of a player's completed cast: Uldaman's altars (events 2228 and 2268 of spells 11568 and
    /// 10340: instance_uldaman StartEvent) and Zul'Farrak's event_spell_unlocking (event 2609 of spell 10738: the pyramid starts once, then the
    /// event's dbscripts_on_event script runs, stored as relay <see cref="RelayScriptCatalog.EventRelayId"/>(2609)). Returns whether a script
    /// handled it.
    /// </summary>
    public static bool OnSpellFinished(SpellCast cast, bool completed)
    {
        ArgumentNullException.ThrowIfNull(cast);
        if (!completed || cast.Caster is not Player player || player.Map is not { } map) return false;
        switch (map.FindUpdater<InstanceData>())
        {
            case UldamanInstance uldaman when cast.Spell.Id == AltarKeeperSpell:
                uldaman.StartEvent(UldamanInstance.AltarKeeperEvent, player);
                return true;
            case UldamanInstance uldaman when cast.Spell.Id == AltarArchaedasSpell:
                uldaman.StartEvent(UldamanInstance.AltarArchaedasEvent, player);
                return true;
            case ZulFarrakInstance zf when cast.Spell.Id == UnlockingSpell:
                if (!zf.StartPyramid()) return false;
                map.FindUpdater<CreatureMapSystem>()?.StartRelayScript(RelayScriptCatalog.EventRelayId(ZulFarrakInstance.PyramidEvent), player, null);
                return true;
            default:
                return false;
        }
    }
}

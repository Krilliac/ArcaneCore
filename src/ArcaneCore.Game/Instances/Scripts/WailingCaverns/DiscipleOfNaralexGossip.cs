using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>GossipHello/GossipSelect_npc_disciple_of_naralex, mangos-classic
/// wailing_caverns/wailing_cavernsScripts.cpp:464-502. The option text is
/// sql/scriptdev2/scriptdev2.sql script_gossip entry -3043000 (absent from ClassicDB z2815).</summary>
public sealed class DiscipleOfNaralexGossip : INpcGossipScript
{
    private const uint BeginAction = 1001, MainSender = 1;
    private const string BeginOption = "Let the event begin!";

    public ScriptedGossipMenu? Hello(Player player, NpcInfo npc)
    {
        if (!TryFind(player, npc, out Creature? creature, out WailingCavernsInstance? instance))
        {
            return null;
        }

        player.Map!.FindUpdater<CreatureMapSystem>()?.CastSpell(creature, 5232, player, false);
        bool ready = instance.GetData(WailingCavernsInstance.TypeDisciple) == EncounterState.Special || player.IsGameMaster;
        return new ScriptedGossipMenu((npc.NpcFlags & NpcFlags.QuestGiver) != 0, ready ? 699u : 698u,
            ready ? [new ScriptedGossipItem(0, BeginOption, MainSender, BeginAction)] : []);
    }

    public uint Select(Player player, NpcInfo npc, uint sender, uint action) => 0;

    public ScriptedGossipReply SelectReply(Player player, NpcInfo npc, uint sender, uint action)
    {
        if (sender != MainSender || action != BeginAction || !TryFind(player, npc, out Creature? creature, out WailingCavernsInstance? instance)
            || (instance.GetData(WailingCavernsInstance.TypeDisciple) != EncounterState.Special && !player.IsGameMaster))
        {
            return default;
        }

        if (creature.AI is DiscipleOfNaralexAi escort && escort.Start(pathId: DiscipleOfNaralexAi.PathId))
        {
            creature.FactionTemplate = 250; // FACTION_ESCORT_N_NEUTRAL_ACTIVE, ScriptDevAIMgr.h:50
        }

        return new ScriptedGossipReply(0, Close: true);
    }

    private static bool TryFind(Player player, NpcInfo npc, out Creature creature, out WailingCavernsInstance instance)
    {
        creature = null!;
        instance = null!;
        if (npc.Entry != WailingCavernsInstance.NpcDisciple || player.Map?.FindObject(npc.Guid) is not Creature found
            || player.Map.FindUpdater<InstanceData>() is not WailingCavernsInstance script)
        {
            return false;
        }

        creature = found;
        instance = script;
        return true;
    }
}

using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;

namespace ArcaneCore.Game.Instances.Scripts.Stratholme;

/// <summary>
/// ScriptName <c>npc_aurius</c> (Stratholme, map 329, entry 10917, quest 5122 "The Medallion of Faith"): the gossip and the quest-reward half of
/// the script, clean-room port of spec <c>329-npc_aurius</c> sections 3 and 4. The instance half (kneeling, the Baron assist, the fake death) is in
/// <see cref="StratholmeInstance"/>. His combat stays the database EventAI rows (spec 2): the creature keeps its AIName AI, because the script is
/// registered as a gossip and quest script (<see cref="CreatureAiFactory.RegisterNpcScript"/>) and so replaces nothing.
/// <para>
/// Hello: the quest menu is prepared when he is a quest giver; the text is 3757 when the Baron and Aurius are both Done, 3756 when only Aurius is
/// Done, else 3755; no options. Quest 5122 rewarded: nothing if the Baron or Aurius is already Done, else <c>TypeAurius</c> becomes Done (the
/// guard also stops a later turn-in of the repeatable quest from re-arming him, spec ambiguity 8.3).
/// </para>
/// Ownership: stateless; world thread.
/// </summary>
public sealed class AuriusScript : INpcGossipScript, INpcQuestScript
{
    public const string ScriptName = "npc_aurius";
    public const uint QuestMedallionOfFaith = 5122;
    public const uint TextBeforeQuest = 3755, TextAuriusDone = 3756, TextBaronDone = 3757;

    public static void Register(CreatureAiFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var script = new AuriusScript();
        factory.RegisterNpcScript(ScriptName, nameof(AuriusScript), script, script);
    }

    public ScriptedGossipMenu? Hello(Player player, NpcInfo npc)
    {
        if (player.Map?.FindUpdater<StratholmeInstance>() is not { } instance)
        {
            return null;
        }

        bool aurius = instance.GetData(StratholmeInstance.TypeAurius) == EncounterState.Done;
        bool baron = instance.GetData(StratholmeInstance.TypeBaron) == EncounterState.Done;
        uint text = baron && aurius ? TextBaronDone : aurius ? TextAuriusDone : TextBeforeQuest;
        return new ScriptedGossipMenu((npc.NpcFlags & NpcFlags.QuestGiver) != 0, text, []);
    }

    public uint Select(Player player, NpcInfo npc, uint sender, uint action) => 0;

    public void OnQuestRewarded(Player player, Creature creature, uint questId)
    {
        if (questId != QuestMedallionOfFaith || player.Map?.FindUpdater<StratholmeInstance>() is not { } instance)
        {
            return;
        }

        if (instance.GetData(StratholmeInstance.TypeBaron) == EncounterState.Done
            || instance.GetData(StratholmeInstance.TypeAurius) == EncounterState.Done)
        {
            return;
        }

        instance.SetData(StratholmeInstance.TypeAurius, EncounterState.Done);
    }
}

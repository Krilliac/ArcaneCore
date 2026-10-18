using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>
/// mangos-classic sunken_templeScripts.cpp AreaTrigger_at_shade_of_eranikus (:44-61): a player who has turned in "The Charge of the
/// Dragonflights" (8555) and has neither turned in nor completed "Eranikus, Tyrant of the Dream" (8733) reaches area trigger 4016 by the Shade of
/// Eranikus. Unless Malfurion is already <c>IN_PROGRESS</c>, the player summons Malfurion Stormrage (15362, TEMPSPAWN_DEAD_DESPAWN) at
/// aSunkenTempleLocation[2] (sunken_temple.h:103) and <see cref="TypeMalfurion"/> becomes <c>IN_PROGRESS</c>. His speech and the quest giver
/// flag are <see cref="SunkenTemple.MalfurionStormrageAI"/>. Without the map's quest seam nothing is summoned.
/// </summary>
public sealed partial class SunkenTempleInstance
{
    public const uint AreaTriggerShadeOfEranikus = 4016, NpcMalfurion = 15362;
    public const uint QuestChargeOfTheDragonflights = 8555, QuestEranikusTyrantOfTheDream = 8733;

    /// <summary>aSunkenTempleLocation[2], "Malfurion summon loc".</summary>
    public static readonly (float X, float Y, float Z, float O) MalfurionSummon = (-660.5277f, -16.7117f, -90.8357f, 1.6055f);

    public override void OnAreaTrigger(Player player, uint triggerId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (triggerId != AreaTriggerShadeOfEranikus || Instance.FindUpdater<IInstanceQuestRewards>() is not { } quests)
        {
            return;
        }

        if (quests.IsRewarded(player, QuestChargeOfTheDragonflights) != true || quests.IsRewarded(player, QuestEranikusTyrantOfTheDream) == true
            || QuestCompleteUnrewarded(player, QuestEranikusTyrantOfTheDream))
        {
            return;
        }

        if (GetData(TypeMalfurion) == EncounterState.InProgress)
        {
            return;
        }

        Instance.FindUpdater<CreatureMapSystem>()?.SummonDeadDespawnAt(NpcMalfurion, MalfurionSummon.X, MalfurionSummon.Y, MalfurionSummon.Z, MalfurionSummon.O);
        SetData(TypeMalfurion, EncounterState.InProgress);
    }
}

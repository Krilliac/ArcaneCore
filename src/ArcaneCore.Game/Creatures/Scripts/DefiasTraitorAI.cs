using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// The Defias Traitor (entry 467, Westfall), "The Defias Brotherhood" (quest 155): mangos-classic ScriptDev2 <c>npc_defias_traitor</c>
/// (eastern_kingdoms/westfall.cpp) on script path 467. On accept he says his start line to the player and runs the path (instant respawn).
/// At 65 he slows to a walk with his progress line; at 94 he gives his last line and the group credit and despawns after 3 s. On aggro he
/// says one of three lines three times in four.
/// Difference from SD2: his defensive react state is not modelled.
/// </summary>
public sealed class DefiasTraitorAI(Creature creature) : EscortAI(creature), IQuestScriptAI
{
    public const uint Entry = 467, QuestDefiasBrotherhood = 155, EscortPath = 467;
    public const int SayStart = 9, SayProgress = 10, SayEnd = 11, SayAggro1 = 487, SayAggro2 = 485, SayAggro3 = 489;

    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId == QuestDefiasBrotherhood)
        {
            System?.SayText(Me, SayStart, player);
            Start(run: true, instantRespawn: true, player: player, questId: questId, waypointPath: EscortPath);
        }
    }

    protected override void WaypointReached(uint pointId)
    {
        switch (pointId)
        {
            case 65:
                SetRun(false);
                if (GetPlayerForEscort() is { } walker)
                {
                    System?.SayText(Me, SayProgress, walker);
                }

                break;
            case 94:
                if (GetPlayerForEscort() is { } player)
                {
                    System?.SayText(Me, SayEnd, player);
                    System?.RewardGroupEventExplored(player, QuestDefiasBrotherhood, Me);
                }

                System?.ForcedDespawn(Me, 3_000);
                break;
        }
    }

    protected override void Aggro(Unit target)
    {
        if (System is { } system)
        {
            int pick = system.RandomInt(0, 3);
            if (pick < 3)
            {
                system.SayText(Me, pick switch { 0 => SayAggro1, 1 => SayAggro2, _ => SayAggro3 }, target);
            }
        }
    }
}

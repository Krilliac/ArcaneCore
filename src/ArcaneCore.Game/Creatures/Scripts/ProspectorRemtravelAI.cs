using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Prospector Remtravel (entry 2917, Darkshore), "The Absent Minded Prospector" (quest 731): mangos-classic ScriptDev2
/// <c>npc_prospector_remtravel</c> (kalimdor/darkshore.cpp) on script path 2917, walking cmangos <c>waypoint_path</c> 2917. On accept he
/// takes the passive escort faction (FACTION_ESCORT_A_PASSIVE, 774, given back at respawn), says his start line (925) and loses the NPC
/// immunity (the classic-db quest start script 731), then walks; at 38 he gives the group credit and at 39 the escort ends where he stands.
/// On aggro he says one of six lines. A creature that throws EventAI custom event A at him (the path's ambushers) is told to attack him.
/// The path's per-point lines, emotes and ambusher waves are classic-db movement scripts, not part of this AI.
/// </summary>
public sealed class ProspectorRemtravelAI(Creature creature) : EscortAI(creature), IQuestScriptAI
{
    public const uint Entry = 2917, QuestAbsentMindedPt2 = 731, EscortPath = 2917, FactionEscortAlliancePassive = 774;
    public const uint AiEventCustomEventAiA = 5;
    public const int SayStart = 925, SayAggro1 = 938; // aggro lines 938-943

    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId == QuestAbsentMindedPt2)
        {
            Me.SetFactionTemporary(FactionEscortAlliancePassive, TemporaryFactionFlags.RestoreRespawn);
            System?.SayText(Me, SayStart, player);
            Me.UnitFlags &= ~UnitFlags.ImmuneToNpc;
            Start(run: false, player: player, questId: questId, waypointPath: EscortPath);
        }
    }

    protected override void WaypointReached(uint pointId)
    {
        switch (pointId)
        {
            case 38:
                if (GetPlayerForEscort() is { } player)
                {
                    System?.RewardGroupEventExplored(player, QuestAbsentMindedPt2, Me);
                }

                break;
            case 39:
                Stop();
                break;
        }
    }

    protected override void Aggro(Unit target)
    {
        if (System is { } system)
        {
            int pick = system.RandomInt(0, 5);
            if (pick == 0)
            {
                system.SayText(Me, SayAggro1);
            }
            else
            {
                system.SayText(Me, SayAggro1 + pick, target);
            }
        }
    }

    public override void OnReceiveAiEvent(uint eventType, Unit sender, Unit? invoker, uint miscValue)
    {
        if (eventType == AiEventCustomEventAiA && Me.IsAlive && sender is Creature attacker)
        {
            System?.AttackStart(attacker, Me);
        }
    }
}

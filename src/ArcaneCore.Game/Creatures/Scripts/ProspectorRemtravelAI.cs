using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Prospector Remtravel (entry 2917, Darkshore), "The Absent Minded Prospector" (quest 731): mangos-classic ScriptDev2
/// <c>npc_prospector_remtravel</c> (kalimdor/darkshore.cpp) on script path 2917, walking cmangos <c>waypoint_path</c> 2917. On accept he
/// takes the passive escort faction (FACTION_ESCORT_A_PASSIVE, 774, given back at respawn), says his start line (925) and loses the NPC
/// immunity (the classic-db quest start script 731), then walks; at 38 he gives the group credit and at 39 the escort ends where he stands.
/// On aggro he says one of six lines. A creature that throws EventAI custom event A at him (the path's ambushers) is told to attack him.
/// The faction, line and flag changes happen only once the escort has started, and the line and flag only when no quest start script 731
/// is loaded (that script does them itself).
/// <para>
/// Content gap: <c>waypoint_path</c> 2917, the path's per-point lines, emotes and ambusher waves (movement scripts 291701-291708 and the
/// spawn groups) and quest start script 731 exist only in classic-db <c>Updates/4749_TDB-0572_the_absentminded_prospector.sql</c>, which
/// the content importer does not apply (ContentScanner). Until it is applied the escort logs "no points" and does not start.
/// </para>
/// </summary>
public sealed class ProspectorRemtravelAI(Creature creature) : EscortAI(creature), IQuestScriptAI
{
    public const uint Entry = 2917, QuestAbsentMindedPt2 = 731, EscortPath = 2917, FactionEscortAlliancePassive = 774;
    public const int SayStart = 925, SayAggro1 = 938; // aggro lines 938-943

    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId == QuestAbsentMindedPt2 && Start(run: false, player: player, questId: questId, waypointPath: EscortPath))
        {
            Me.SetFactionTemporary(FactionEscortAlliancePassive, TemporaryFactionFlags.RestoreRespawn);

            // The line and the flag are the classic-db quest start script's work when it is loaded (it runs after this hook).
            if (System is { } system && !system.HasDbScript(DbScriptKind.QuestStart, QuestAbsentMindedPt2))
            {
                system.SayText(Me, SayStart, player);
                Me.UnitFlags &= ~UnitFlags.ImmuneToNpc;
            }
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
        if (eventType == AiEventTypes.CustomEventAiA && Me.IsAlive && sender is Creature attacker)
        {
            attacker.AI?.AttackStart(Me);
        }
    }
}
